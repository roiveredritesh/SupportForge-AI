using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.Api;
using SupportForge.Api.Contracts;
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
    private readonly ILogger<ChatController> _logger;

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
        ILogger<ChatController> logger)
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
        _logger = logger;
    }

    // Splits on word boundaries while keeping the trailing whitespace attached to each chunk, so
    // re-joining the yielded pieces reproduces the original string exactly.
    private static IEnumerable<string> SplitKeepingDelimiters(string text)
    {
        foreach (Match m in Regex.Matches(text, @"\S+\s*"))
            yield return m.Value;
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
    private async Task<List<(string Role, string Content)>> LoadRecapAsync(string conversationId, CancellationToken ct)
    {
        var history = await _messages.GetByConversationIdAsync(conversationId, ct);
        return history.TakeLast(6).Select(m => (m.Role, m.Content)).ToList();
    }

    // U19: context is the just-completed AgentContext for this turn -- its KbSnippets/CodeSnippets/
    // VisionFindings/ProductVersion/Config are cached onto the assistant ChatMessage so
    // EscalationsController can build a full-detail handoff Markdown later without re-running the
    // pipeline (that state doesn't survive past this request otherwise).
    private async Task RecordTurnAsync(
        Conversation conversation, string query, string answer, double confidence, IReadOnlyList<ChatSource> sources,
        int totalTokensUsed, AgentContext context, CancellationToken ct)
    {
        await _messages.AddAsync(new ChatMessage
        {
            Id = Guid.NewGuid().ToString("n"),
            ConversationId = conversation.Id,
            Role = "user",
            Content = query,
        }, ct);

        await _messages.AddAsync(new ChatMessage
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
        }, ct);

        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await _conversations.UpsertAsync(conversation, ct);
    }

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

        var result = await _pipeline.RunAsync(context, ct);
        await _tokenUsage.AddAsync(
            new TokenUsageEntry(request.ProjectId, result.TotalTokensUsed, DateTimeOffset.UtcNow, "chat", request.ProductVersion, request.Config), ct);
        _logger.LogInformation("TokenUsage project={ProjectId} total={Total} byAgent={ByAgent}",
            request.ProjectId, result.TotalTokensUsed, JsonSerializer.Serialize(result.TokensByAgent));

        var sources = BuildSources(result);
        await RecordTurnAsync(conversation, request.Query, result.Draft, result.Confidence, sources, result.TotalTokensUsed, result, ct);

        var role = this.CurrentUserRole();
        return Ok(new ChatQueryResponse
        {
            Draft = result.Draft,
            Confidence = result.Confidence,
            ConversationId = conversation.Id,
            Sources = sources,
            TotalTokensUsed = result.TotalTokensUsed,
            CodeDetails = CodeDetailsForRole(role, result),
            CommitHistory = await CommitHistoryForRoleAsync(role, request.ProjectId, result, ct),
        });
    }

    // U6: gated at response assembly, not inside the agent pipeline -- CodeAnalyzerAgent computes
    // context.CodeSnippets regardless of caller role (same "pipeline always computes full detail"
    // precedent Sprint 4's escalation caching reuses); only what reaches the HTTP response is
    // role-shaped. L1 gets null here, which JsonIgnore(WhenWritingNull) on ChatQueryResponse turns
    // into the field being entirely absent from the wire, not present-but-empty.
    private static IReadOnlyList<string>? CodeDetailsForRole(AppRole role, AgentContext context) =>
        role is AppRole.L2 or AppRole.L3 or AppRole.Admin && context.CodeSnippets.Count > 0
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
        AppRole role, string projectId, AgentContext context, CancellationToken ct)
    {
        if (role is not (AppRole.L2 or AppRole.L3 or AppRole.Admin) || context.CodeSnippets.Count == 0) return null;

        var project = await _projects.GetByIdAsync(projectId, ct);
        if (project is null || project.Repos.Count == 0) return null;

        var files = ExtractFilePaths(context.CodeSnippets);
        if (files.Count == 0) return null;

        var history = new List<CommitInfo>();
        foreach (var file in files)
            history.AddRange(await _commitLookup.LookupForProjectAsync(project, file, MaxCommitsPerFile, ct));

        return history.Count > 0 ? history : null;
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
            new TokenUsageEntry(request.ProjectId, context.TotalTokensUsed, DateTimeOffset.UtcNow, "chat", request.ProductVersion, request.Config), ct);
        _logger.LogInformation("TokenUsage project={ProjectId} total={Total} byAgent={ByAgent}",
            request.ProjectId, context.TotalTokensUsed, JsonSerializer.Serialize(context.TokensByAgent));

        _logger.LogInformation(
            "{Agent} completed in {ElapsedMs}ms: leaked={Leaked} confidence={Confidence}", DrafterName, sw.ElapsedMilliseconds, leaked, confidence);

        // Chunked word-by-word (not one SSE event) purely to preserve the typing-effect UX the
        // frontend already renders -- every word here already passed the leak check above.
        foreach (var word in SplitKeepingDelimiters(finalText))
        {
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(word)}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        var sources = BuildSources(context);
        await RecordTurnAsync(conversation, request.Query, finalText, confidence, sources, context.TotalTokensUsed, context, ct);

        var streamRole = this.CurrentUserRole();
        var done = JsonSerializer.Serialize(
            new
            {
                confidence,
                conversationId = conversation.Id,
                sources = sources.Select(s => new { label = s.Label, url = s.Url }),
                totalTokensUsed = context.TotalTokensUsed,
                codeDetails = CodeDetailsForRole(streamRole, context),
                commitHistory = await CommitHistoryForRoleAsync(streamRole, request.ProjectId, context, ct),
            },
            // U6: same "absent, not null-but-present" rule as ChatQueryResponse.CodeDetails --
            // this anonymous type can't carry a per-property [JsonIgnore], so it's set for this
            // one serialize call instead.
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        await Response.WriteAsync($"event: done\ndata: {done}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
