using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using SupportForge.Agents;
using SupportForge.Agents.Guard;
using SupportForge.Agents.Tools;
using SupportForge.Api;
using SupportForge.Api.Contracts;
using SupportForge.Api.Hubs;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public partial class ChatController : ControllerBase
{
    private readonly CoordinatorPipeline _pipeline;
    private readonly TriageAgent _triage;
    private readonly FreshnessGateAgent _freshnessGate;
    private readonly KbResearcherAgent _kbResearcher;
    private readonly CrossReferenceAgent _crossReference;
    private readonly CodeAnalyzerAgent _codeAnalyzer;
    private readonly KbResearcherVerifier _kbVerifier;
    private readonly CodeAnalyzerVerifier _codeVerifier;
    private readonly VisionAnalyzerAgent _visionAnalyzer;
    private readonly VisionAnalyzerVerifier _visionVerifier;
    private readonly ILlmChatClient _llm;
    private readonly ITokenUsageRepository _tokenUsage;
    private readonly IConversationRepository _conversations;
    private readonly IChatMessageRepository _messages;
    private readonly IProjectMembershipRepository _memberships;
    private readonly IProjectRepository _projects;
    private readonly CommitLookupTool _commitLookup;
    private readonly IBlastRadiusQueryTool _blastRadius;
    private readonly ILogger<ChatController> _logger;
    // U26: nullable with a default so every pre-existing test-constructed ChatController (six call
    // sites in ChatControllerTests.cs, none of which care about SignalR) keeps compiling unchanged;
    // production DI always supplies a real IHubContext<ConversationHub> (registered by
    // builder.Services.AddSignalR() in Program.cs). Broadcasting is a best-effort side channel, not
    // load-bearing for the HTTP response, so a null hub just means "nothing to broadcast to."
    private readonly IHubContext<ConversationHub>? _hub;
    // U17: nullable with a default for the same reason as _hub above -- the pre-existing test
    // call sites in ChatControllerTests.cs don't wire a guard and must keep compiling and behaving
    // as before. Production DI always supplies a real GuardService (registered in Program.cs).
    private readonly GuardService? _guard;

    private const string DrafterName = "Drafter";

    public ChatController(
        CoordinatorPipeline pipeline,
        TriageAgent triage,
        FreshnessGateAgent freshnessGate,
        KbResearcherAgent kbResearcher,
        CrossReferenceAgent crossReference,
        CodeAnalyzerAgent codeAnalyzer,
        KbResearcherVerifier kbVerifier,
        CodeAnalyzerVerifier codeVerifier,
        VisionAnalyzerAgent visionAnalyzer,
        VisionAnalyzerVerifier visionVerifier,
        ILlmChatClient llm,
        ITokenUsageRepository tokenUsage,
        IConversationRepository conversations,
        IChatMessageRepository messages,
        IProjectMembershipRepository memberships,
        IProjectRepository projects,
        CommitLookupTool commitLookup,
        IBlastRadiusQueryTool blastRadius,
        ILogger<ChatController> logger,
        IHubContext<ConversationHub>? hub = null,
        GuardService? guard = null)
    {
        _pipeline = pipeline;
        _triage = triage;
        _freshnessGate = freshnessGate;
        _kbResearcher = kbResearcher;
        _crossReference = crossReference;
        _codeAnalyzer = codeAnalyzer;
        _kbVerifier = kbVerifier;
        _codeVerifier = codeVerifier;
        _visionAnalyzer = visionAnalyzer;
        _visionVerifier = visionVerifier;
        _llm = llm;
        _tokenUsage = tokenUsage;
        _conversations = conversations;
        _messages = messages;
        _memberships = memberships;
        _projects = projects;
        _commitLookup = commitLookup;
        _blastRadius = blastRadius;
        _logger = logger;
        _hub = hub;
        _guard = guard;
    }

    // Splits on word boundaries while keeping the trailing whitespace attached to each chunk, so
    // re-joining the yielded pieces reproduces the original string exactly.
    private static IEnumerable<string> SplitKeepingDelimiters(string text)
    {
        foreach (Match m in Regex.Matches(text, @"\S+\s*"))
            yield return m.Value;
    }

    // Chunked word-by-word (not one SSE event) purely to preserve the typing-effect UX the frontend
    // already renders. Shared by the normal QueryStream path and U17's blocked-turn path, so both
    // stream with identical framing.
    private async Task StreamTextAsync(string text, CancellationToken ct)
    {
        foreach (var word in SplitKeepingDelimiters(text))
        {
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(word)}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    // Mirrors CoordinatorPipeline's retry-once-then-flag semantics for this manual (non-graph)
    // streaming path: run the specialist, verify, and retry exactly once if verification asks for it.
    private static async Task<AgentContext> RunWithVerificationAsync(
        IAgent specialist, IAgent verifier, AgentContext context, Func<AgentContext, VerificationResult> getResult, CancellationToken ct)
    {
        context = await specialist.RunAsync(context, ct);
        context = await verifier.RunAsync(context, ct);
        if (getResult(context).Status == VerificationStatus.FailedRetrying)
        {
            context = await specialist.RunAsync(context, ct);
            context = await verifier.RunAsync(context, ct);
        }
        return context;
    }

    private const int MaxQueryLength = 4000;
    private const int MaxScreenshotBytes = 5 * 1024 * 1024;

    // U5: reject oversized requests before any agent runs, rather than letting the LLM/vision
    // calls fail downstream or silently truncate. Returns null when the request is valid.
    private static string? ValidateRequest(ChatQueryRequest request)
    {
        if (request.Query.Length > MaxQueryLength)
        {
            return $"Query exceeds the maximum length of {MaxQueryLength} characters.";
        }

        if (!string.IsNullOrEmpty(request.ScreenshotBase64))
        {
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(request.ScreenshotBase64);
            }
            catch (FormatException)
            {
                return "ScreenshotBase64 is not valid base64.";
            }

            if (decoded.Length > MaxScreenshotBytes)
            {
                return $"Screenshot exceeds the maximum size of {MaxScreenshotBytes} bytes.";
            }
        }

        return null;
    }

    // U9: shared step-construction both Query (via CoordinatorPipeline) and QueryStream (manual,
    // for SSE) build identically before diverging on execution strategy -- see KTD3 for why the
    // two paths stay separate (QueryStream needs per-token output the Workflow API doesn't expose).
    private async Task<AgentContext> BuildInitialContextAsync(ChatQueryRequest request, string conversationId, CancellationToken ct)
    {
        var context = new AgentContext
        {
            ProjectId = request.ProjectId,
            Query = request.Query,
            ScreenshotBase64 = request.ScreenshotBase64,
            ProductVersion = request.ProductVersion,
            Config = request.Config,
        };
        context.History.AddRange(await LoadRecapAsync(conversationId, ct));
        return context;
    }

    // U17: screens the customer's message plus recent history before any agent or LLM call. Returns
    // null when no guard is wired, which callers treat as "not screened" (never as blocked).
    private async Task<GuardDecision?> ScreenTurnAsync(AgentContext context, CancellationToken ct)
    {
        var guard = _guard;
        if (guard is null) return null;

        return await guard.ScreenAsync(
            new GuardInput(context.Query, context.History, !string.IsNullOrEmpty(context.ScreenshotBase64)),
            this.CurrentUserId(),
            context.ProjectId,
            ct);
    }

    // Resolves the request's ConversationId to an existing conversation (must belong to the
    // request's ProjectId — a stale frontend conversation surviving a project switch is a bug,
    // not a valid cross-project request) or creates a new one when none was supplied.
    private async Task<Conversation?> ResolveConversationAsync(ChatQueryRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.ConversationId))
        {
            var existing = await _conversations.GetByIdAsync(request.ConversationId, ct);
            if (existing is null || existing.ProjectId != request.ProjectId) return null;
            return existing;
        }

        var conversation = new Conversation
        {
            Id = Guid.NewGuid().ToString("n"),
            ProjectId = request.ProjectId,
            Title = request.Query.Length > 60 ? request.Query[..60] + "…" : request.Query,
        };
        await _conversations.UpsertAsync(conversation, ct);
        return conversation;
    }

    // Last 6 messages (3 turns) — ponytail: fixed-window cap, revisit with token-aware
    // trimming if transcripts get long.
    // U17: a turn the guard blocked is removed before that cap is applied: the blocked assistant
    // message and the user message RecordTurnAsync wrote just before it. That keeps attack text out
    // of every later LLM prompt, and stops one blocked attack from tainting the guard's screening
    // of every following turn (the guard reads this same history).
    private async Task<List<(string Role, string Content)>> LoadRecapAsync(string conversationId, CancellationToken ct)
    {
        var history = await _messages.GetByConversationIdAsync(conversationId, ct);

        var skip = new HashSet<int>();
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Role != "assistant" || history[i].GuardVerdict != "blocked") continue;

            skip.Add(i);
            if (i > 0 && history[i - 1].Role == "user") skip.Add(i - 1);
        }

        var kept = history.Where((_, i) => !skip.Contains(i)).ToList();
        return kept.TakeLast(6).Select(m => (m.Role, m.Content)).ToList();
    }

    // U19: context is the just-completed AgentContext for this turn -- its KbSnippets/CodeSnippets/
    // VisionFindings/ProductVersion/Config are cached onto the assistant ChatMessage so
    // EscalationsController can build a full-detail handoff Markdown later without re-running the
    // pipeline (that state doesn't survive past this request otherwise).
    // U17: guard is only stamped onto the assistant message. Null guard, or the guard being Off, leaves
    // both guard fields null so the persisted turn looks exactly like a pre-U17 one.
    private async Task<ChatMessage> RecordTurnAsync(
        Conversation conversation, string query, string answer, double confidence, IReadOnlyList<ChatSource> sources,
        int totalTokensUsed, AgentContext context, CancellationToken ct, GuardDecision? guard = null)
    {
        await _messages.AddAsync(new ChatMessage
        {
            Id = Guid.NewGuid().ToString("n"),
            ConversationId = conversation.Id,
            Role = "user",
            Content = query,
        }, ct);

        var assistantMessage = new ChatMessage
        {
            Id = Guid.NewGuid().ToString("n"),
            ConversationId = conversation.Id,
            Role = "assistant",
            Content = answer,
            Confidence = confidence,
            Sources = sources,
            TotalTokensUsed = totalTokensUsed,
            KbSnippets = context.KbSnippets.ToList(),
            CodeSnippets = context.CodeSnippets.ToList(),
            VisionFindings = context.VisionFindings,
            ProductVersion = context.ProductVersion,
            Config = context.Config,
            GuardVerdict = guard is null || guard.Source == "off" ? null : guard.Verdict.ToString().ToLowerInvariant(),
            GuardSignals = guard is null || guard.Signals.Count == 0 ? null : guard.Signals,
        };
        await _messages.AddAsync(assistantMessage, ct);

        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await _conversations.UpsertAsync(conversation, ct);
        return assistantMessage;
    }

    // U26: ConversationHub broadcast is deliberately role-blind and carries only the same fields
    // ConversationsController.ToDto(ChatMessage) already exposes to every project member via
    // GET /api/conversations/{id} -- CodeDetails/CommitHistory/BlastRadius never go through here,
    // so a group with mixed elevation (e.g. the original L1 asker plus an invited L2 engineer) can
    // never leak elevated detail via the hub; each caller's own elevated fields still only ever
    // reach them through their own Query/QueryStream response (see IsElevated below).
    private Task BroadcastMessageAsync(string conversationId, ChatMessage message) =>
        _hub is null
            ? Task.CompletedTask
            : _hub.Clients.Group(ConversationHub.GroupName(conversationId)).SendAsync("receiveMessage", new
            {
                id = message.Id,
                role = message.Role,
                content = message.Content,
                confidence = message.Confidence,
                sources = message.Sources,
                createdAt = message.CreatedAt,
                totalTokensUsed = message.TotalTokensUsed,
            });

    // U26: effective permission check reused by both Query and QueryStream -- role is L2/L3/Admin
    // (unchanged U6 gate), OR the caller was invited into this one conversation (U26). Scoped
    // strictly to conversation.InvitedUserIds, never touches the caller's own Role/AppUser record,
    // so it can never leak into their access on any other conversation or project.
    private static bool IsElevated(AppRole role, Conversation conversation, string userId) =>
        role is AppRole.L2 or AppRole.L3 or AppRole.Admin || conversation.InvitedUserIds.Contains(userId);

    // E1 (gap-closing-solutions.md Phase E): context.Sources is populated by KbResearcherAgent/
    // CodeAnalyzerAgent at *retrieval* time (before the verifier judges relevance), so it can contain
    // entries for a branch the verifier later rejected. Filtering here by each branch's final
    // VerificationStatus -- rather than mutating the shared ConcurrentBag inside the verifiers, which
    // run concurrently in CoordinatorPipeline's fan-out and would race on any shared-collection edit
    // -- is what keeps a rejected branch's sources out of what the user actually sees.
    private static IReadOnlyList<ChatSource> BuildSources(AgentContext context)
    {
        var sources = new List<ChatSource>();
        if (context.KbVerification.Status == VerificationStatus.Passed)
            sources.AddRange(context.Sources.Where(s => s.Label.StartsWith("KB: ", StringComparison.Ordinal)).Select(s => new ChatSource(s.Label, s.Url)));
        if (context.CodeVerification.Status == VerificationStatus.Passed)
            sources.AddRange(context.Sources.Where(s => s.Label.StartsWith("Code: ", StringComparison.Ordinal)).Select(s => new ChatSource(s.Label, s.Url)));
        return sources;
    }

    [HttpPost("query")]
    public async Task<ActionResult<ChatQueryResponse>> Query([FromBody] ChatQueryRequest request, CancellationToken ct = default)
    {
        var validationError = ValidateRequest(request);
        if (validationError is not null) return BadRequest(validationError);
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), request.ProjectId, ct)) return Forbid();

        var conversation = await ResolveConversationAsync(request, ct);
        if (conversation is null) return BadRequest("ConversationId does not belong to the given ProjectId.");

        var context = await BuildInitialContextAsync(request, conversation.Id, ct);

        // U17 / KTD12: the guard runs before anything else touches the turn -- before the pipeline's
        // intent classification (TriageAgent) and every LLM call inside it. A blocked turn must never
        // reach a model, so this check cannot move below _pipeline.RunAsync.
        var guard = await ScreenTurnAsync(context, ct);
        if (guard?.Verdict == GuardVerdict.Blocked)
        {
            // Blocked turns cost no tokens, so no TokenUsageEntry is written, and no code/commit/blast
            // detail is computed -- there is no pipeline result to derive them from.
            var blockedMessage = await RecordTurnAsync(conversation, request.Query, GuardRefusal.Text, 0.0, new List<ChatSource>(), 0, context, ct, guard);
            await BroadcastMessageAsync(conversation.Id, blockedMessage);

            return Ok(new ChatQueryResponse
            {
                Draft = GuardRefusal.Text,
                Confidence = 0.0,
                ConversationId = conversation.Id,
                Sources = new List<ChatSource>(),
                TotalTokensUsed = 0,
            });
        }

        var result = await _pipeline.RunAsync(context, ct);
        await _tokenUsage.AddAsync(
            new TokenUsageEntry(request.ProjectId, result.TotalTokensUsed, DateTimeOffset.UtcNow, "chat", request.ProductVersion, request.Config, this.CurrentUserId()), ct);
        _logger.LogInformation("TokenUsage project={ProjectId} total={Total} byAgent={ByAgent}",
            request.ProjectId, result.TotalTokensUsed, JsonSerializer.Serialize(result.TokensByAgent));

        var sources = BuildSources(result);
        var assistantMessage = await RecordTurnAsync(conversation, request.Query, result.Draft, result.Confidence, sources, result.TotalTokensUsed, result, ct, guard);
        await BroadcastMessageAsync(conversation.Id, assistantMessage);

        var role = this.CurrentUserRole();
        var elevated = IsElevated(role, conversation, this.CurrentUserId());
        return Ok(new ChatQueryResponse
        {
            Draft = result.Draft,
            Confidence = result.Confidence,
            ConversationId = conversation.Id,
            Sources = sources,
            TotalTokensUsed = result.TotalTokensUsed,
            CodeDetails = CodeDetailsForRole(elevated, result),
            CommitHistory = await CommitHistoryForRoleAsync(elevated, request.ProjectId, result, ct),
            BlastRadius = await BlastRadiusForRoleAsync(elevated, request.ProjectId, result, ct),
        });
    }

    // U6: gated at response assembly, not inside the agent pipeline -- CodeAnalyzerAgent computes
    // context.CodeSnippets regardless of caller role (same "pipeline always computes full detail"
    // precedent Sprint 4's escalation caching reuses); only what reaches the HTTP response is
    // role-shaped. L1 gets null here, which JsonIgnore(WhenWritingNull) on ChatQueryResponse turns
    // into the field being entirely absent from the wire, not present-but-empty.
    private static IReadOnlyList<string>? CodeDetailsForRole(bool elevated, AgentContext context) =>
        elevated && context.CodeSnippets.Count > 0
            ? context.CodeSnippets.ToList()
            : null;

    // U11: matches GraphDbQueryTool's "src=<file> loc=L<line>" NODE-line format (see
    // GraphDbQueryTool.FormatAndTruncate) -- the only place CodeAnalyzerAgent's snippets carry a
    // file reference. Distinct + capped so one answer doesn't fan out into dozens of git/GitHub calls.
    [GeneratedRegex(@"src=(?<file>\S+)\s+loc=L\d+")]
    private static partial Regex CodeLocationRef();

    private const int MaxFilesForCommitLookup = 3;
    private const int MaxCommitsPerFile = 5;

    private static IReadOnlyList<string> ExtractFilePaths(IReadOnlyList<string> codeSnippets) =>
        codeSnippets
            .SelectMany(s => CodeLocationRef().Matches(s).Select(m => m.Groups["file"].Value))
            .Distinct()
            .Take(MaxFilesForCommitLookup)
            .ToList();

    // U11: gated the same way as CodeDetailsForRole (role check at response assembly, not inside the
    // agent pipeline) and wired off CodeDetails' own file references, so commit history always lines
    // up with whatever code-location matches the caller can actually see.
    private async Task<IReadOnlyList<CommitInfo>?> CommitHistoryForRoleAsync(
        bool elevated, string projectId, AgentContext context, CancellationToken ct)
    {
        if (!elevated || context.CodeSnippets.Count == 0) return null;

        var project = await _projects.GetByIdAsync(projectId, ct);
        if (project is null || project.Repos.Count == 0) return null;

        var files = ExtractFilePaths(context.CodeSnippets);
        if (files.Count == 0) return null;

        var history = new List<CommitInfo>();
        foreach (var file in files)
            history.AddRange(await _commitLookup.LookupForProjectAsync(project, file, MaxCommitsPerFile, ct));

        return history.Count > 0 ? history : null;
    }

    // U24: gated and wired the same way as CommitHistoryForRoleAsync -- same file references
    // (CodeDetails' own matched files), same L2/L3/Admin gate, same "absent, not null-but-present" rule.
    private async Task<IReadOnlyList<BlastRadiusEntry>?> BlastRadiusForRoleAsync(
        bool elevated, string projectId, AgentContext context, CancellationToken ct)
    {
        if (!elevated || context.CodeSnippets.Count == 0) return null;

        var files = ExtractFilePaths(context.CodeSnippets);
        if (files.Count == 0) return null;

        var entries = await _blastRadius.QueryAsync(projectId, files, ct);
        return entries.Count > 0 ? entries : null;
    }

    // Runs every agent except the Drafter as before, then streams the Drafter's answer to the
    // client token-by-token over SSE instead of waiting for the full completion.
    [HttpPost("query/stream")]
    public async Task QueryStream([FromBody] ChatQueryRequest request, CancellationToken ct)
    {
        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync(validationError, ct);
            return;
        }
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), request.ProjectId, ct))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var conversation = await ResolveConversationAsync(request, ct);
        if (conversation is null)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync("ConversationId does not belong to the given ProjectId.", ct);
            return;
        }

        var context = await BuildInitialContextAsync(request, conversation.Id, ct);

        // U17 / KTD12: screened before the progress ping, _triage and every other agent below, so a
        // blocked turn never reaches intent classification or any LLM call (see Query for the same rule).
        var guard = await ScreenTurnAsync(context, ct);
        if (guard?.Verdict == GuardVerdict.Blocked)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";

            await StreamTextAsync(GuardRefusal.Text, ct);

            var blockedMessage = await RecordTurnAsync(conversation, request.Query, GuardRefusal.Text, 0.0, new List<ChatSource>(), 0, context, ct, guard);
            await BroadcastMessageAsync(conversation.Id, blockedMessage);

            // Same done-event shape as the normal path below. The code/commit/blast fields are passed
            // as null, which WhenWritingNull leaves off the wire, as on the normal path for a non-elevated caller.
            var blockedDone = JsonSerializer.Serialize(
                new
                {
                    confidence = 0.0,
                    conversationId = conversation.Id,
                    sources = Array.Empty<object>(),
                    totalTokensUsed = 0,
                    codeDetails = (object?)null,
                    commitHistory = (object?)null,
                    blastRadius = (object?)null,
                },
                new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
            await Response.WriteAsync($"event: done\ndata: {blockedDone}\n\n", ct);
            await Response.Body.FlushAsync(ct);
            return;
        }

        // U26: lightweight progress ping for any other viewer connected to this conversation's
        // SignalR group (e.g. an invited engineer watching along) -- ponytail: a single
        // "processing has started" event, not per-token streaming to other viewers; add per-agent
        // progress events here if PresenceIndicator ever needs finer-grained status.
        if (_hub is not null)
            await _hub.Clients.Group(ConversationHub.GroupName(conversation.Id)).SendAsync("progress", new { conversationId = conversation.Id, stage = "processing" }, ct);

        context = await _triage.RunAsync(context, ct);
        context = await _freshnessGate.RunAsync(context, ct);
        // WS4 (retrieval-pipeline remediation plan): Code no longer runs independently of KB here --
        // mirrors CoordinatorPipeline's KB -> CrossReference -> Code topology, not the old parallel
        // RunWithVerificationAsync(kb)/RunWithVerificationAsync(code) pair.
        context = await RunWithVerificationAsync(_kbResearcher, _kbVerifier, context, c => c.KbVerification, ct);
        context = await _crossReference.RunAsync(context, ct);
        context = await RunWithVerificationAsync(_codeAnalyzer, _codeVerifier, context, c => c.CodeVerification, ct);
        context = await RunWithVerificationAsync(_visionAnalyzer, _visionVerifier, context, c => c.VisionVerification, ct);

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        // Buffered, not token-piped: LooksLikeLeak needs the complete draft to find verbatim runs
        // against source snippets, and once a token is written to the SSE response it's rendered in
        // the browser -- there's no taking it back. So the full draft is assembled server-side first,
        // checked, and only the vetted text (real answer or LeakFallback) ever reaches the client.
        _logger.LogInformation("{Agent} starting: project={ProjectId} intent={Intent}", DrafterName, context.ProjectId, context.Intent);
        var sw = Stopwatch.StartNew();

        string finalText;
        double confidence;
        bool leaked = false;

        if (DrafterAgent.HasNoUsableContext(context))
        {
            finalText = DrafterAgent.NoContextFallback;
            confidence = 0.0;
        }
        else
        {
            var userPrompt = DrafterAgent.BuildUserPrompt(context);

            async Task<string> CompleteDraftAsync(string prompt)
            {
                var sb = new StringBuilder();
                await foreach (var token in _llm.StreamCompleteAsync(DrafterAgent.SystemPrompt, prompt, ct))
                    sb.Append(token);
                context.AddTokens(DrafterName, _llm.LastTotalTokens);
                return sb.ToString();
            }

            var draft = await CompleteDraftAsync(userPrompt);
            leaked = DrafterAgent.LooksLikeLeak(draft, context);
            if (leaked)
            {
                draft = await CompleteDraftAsync(userPrompt + DrafterAgent.LeakRetryNote);
                leaked = DrafterAgent.LooksLikeLeak(draft, context);
            }

            finalText = leaked ? DrafterAgent.LeakFallback : draft;
            confidence = leaked ? 0.0 : DrafterAgent.ComputeConfidence(context);
        }

        await _tokenUsage.AddAsync(
            new TokenUsageEntry(request.ProjectId, context.TotalTokensUsed, DateTimeOffset.UtcNow, "chat", request.ProductVersion, request.Config, this.CurrentUserId()), ct);
        _logger.LogInformation("TokenUsage project={ProjectId} total={Total} byAgent={ByAgent}",
            request.ProjectId, context.TotalTokensUsed, JsonSerializer.Serialize(context.TokensByAgent));

        _logger.LogInformation(
            "{Agent} completed in {ElapsedMs}ms: leaked={Leaked} confidence={Confidence}", DrafterName, sw.ElapsedMilliseconds, leaked, confidence);

        // Every word here already passed the leak check above.
        await StreamTextAsync(finalText, ct);

        var sources = BuildSources(context);
        var assistantMessage = await RecordTurnAsync(conversation, request.Query, finalText, confidence, sources, context.TotalTokensUsed, context, ct, guard);
        await BroadcastMessageAsync(conversation.Id, assistantMessage);

        var streamRole = this.CurrentUserRole();
        var streamElevated = IsElevated(streamRole, conversation, this.CurrentUserId());
        var done = JsonSerializer.Serialize(
            new
            {
                confidence,
                conversationId = conversation.Id,
                sources = sources.Select(s => new { label = s.Label, url = s.Url }),
                totalTokensUsed = context.TotalTokensUsed,
                codeDetails = CodeDetailsForRole(streamElevated, context),
                commitHistory = await CommitHistoryForRoleAsync(streamElevated, request.ProjectId, context, ct),
                blastRadius = await BlastRadiusForRoleAsync(streamElevated, request.ProjectId, context, ct),
            },
            // U6: same "absent, not null-but-present" rule as ChatQueryResponse.CodeDetails --
            // this anonymous type can't carry a per-property [JsonIgnore], so it's set for this
            // one serialize call instead.
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        await Response.WriteAsync($"event: done\ndata: {done}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
