using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Agents;
using SupportForge.Api.Contracts;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private readonly CoordinatorPipeline _pipeline;
    private readonly TriageAgent _triage;
    private readonly KbResearcherAgent _kbResearcher;
    private readonly CodeAnalyzerAgent _codeAnalyzer;
    private readonly KbResearcherVerifier _kbVerifier;
    private readonly CodeAnalyzerVerifier _codeVerifier;
    private readonly VisionAnalyzerAgent _visionAnalyzer;
    private readonly VisionAnalyzerVerifier _visionVerifier;
    private readonly ILlmClient _llm;
    private readonly ITokenUsageRepository _tokenUsage;
    private readonly IConversationRepository _conversations;
    private readonly IChatMessageRepository _messages;

    public ChatController(
        CoordinatorPipeline pipeline,
        TriageAgent triage,
        KbResearcherAgent kbResearcher,
        CodeAnalyzerAgent codeAnalyzer,
        KbResearcherVerifier kbVerifier,
        CodeAnalyzerVerifier codeVerifier,
        VisionAnalyzerAgent visionAnalyzer,
        VisionAnalyzerVerifier visionVerifier,
        ILlmClient llm,
        ITokenUsageRepository tokenUsage,
        IConversationRepository conversations,
        IChatMessageRepository messages)
    {
        _pipeline = pipeline;
        _triage = triage;
        _kbResearcher = kbResearcher;
        _codeAnalyzer = codeAnalyzer;
        _kbVerifier = kbVerifier;
        _codeVerifier = codeVerifier;
        _visionAnalyzer = visionAnalyzer;
        _visionVerifier = visionVerifier;
        _llm = llm;
        _tokenUsage = tokenUsage;
        _conversations = conversations;
        _messages = messages;
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

    private async Task RecordTurnAsync(
        Conversation conversation, string query, string answer, double confidence, CancellationToken ct)
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
        }, ct);

        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await _conversations.UpsertAsync(conversation, ct);
    }

    [HttpPost("query")]
    public async Task<ActionResult<ChatQueryResponse>> Query([FromBody] ChatQueryRequest request, CancellationToken ct = default)
    {
        var conversation = await ResolveConversationAsync(request, ct);
        if (conversation is null) return BadRequest("ConversationId does not belong to the given ProjectId.");

        var context = new AgentContext
        {
            ProjectId = request.ProjectId,
            Query = request.Query,
            ScreenshotBase64 = request.ScreenshotBase64,
        };
        context.History.AddRange(await LoadRecapAsync(conversation.Id, ct));

        var result = await _pipeline.RunAsync(context, ct);
        await _tokenUsage.AddAsync(new TokenUsageEntry(request.ProjectId, result.TotalTokensUsed, DateTimeOffset.UtcNow), ct);

        await RecordTurnAsync(conversation, request.Query, result.Draft, result.Confidence, ct);

        return Ok(new ChatQueryResponse
        {
            Draft = result.Draft,
            Confidence = result.Confidence,
            ConversationId = conversation.Id,
        });
    }

    // Runs every agent except the Drafter as before, then streams the Drafter's answer to the
    // client token-by-token over SSE instead of waiting for the full completion.
    [HttpPost("query/stream")]
    public async Task QueryStream([FromBody] ChatQueryRequest request, CancellationToken ct)
    {
        var conversation = await ResolveConversationAsync(request, ct);
        if (conversation is null)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync("ConversationId does not belong to the given ProjectId.", ct);
            return;
        }

        var context = new AgentContext
        {
            ProjectId = request.ProjectId,
            Query = request.Query,
            ScreenshotBase64 = request.ScreenshotBase64,
        };
        context.History.AddRange(await LoadRecapAsync(conversation.Id, ct));

        context = await _triage.RunAsync(context, ct);
        context = await RunWithVerificationAsync(_kbResearcher, _kbVerifier, context, c => c.KbVerification, ct);
        context = await RunWithVerificationAsync(_codeAnalyzer, _codeVerifier, context, c => c.CodeVerification, ct);
        context = await RunWithVerificationAsync(_visionAnalyzer, _visionVerifier, context, c => c.VisionVerification, ct);

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        var draft = new StringBuilder();
        await foreach (var token in _llm.StreamCompleteAsync(DrafterAgent.SystemPrompt, DrafterAgent.BuildUserPrompt(context), ct))
        {
            draft.Append(token);
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(token)}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        context.TotalTokensUsed += _llm.LastTotalTokens;
        await _tokenUsage.AddAsync(new TokenUsageEntry(request.ProjectId, context.TotalTokensUsed, DateTimeOffset.UtcNow), ct);

        // Tokens are already flushed, so a leak can't be un-sent over SSE; what we can keep clean is
        // the persisted turn (and the recap it feeds) and the final confidence the client renders.
        var leaked = DrafterAgent.LooksLikeLeak(draft.ToString(), context);
        var finalText = leaked ? DrafterAgent.LeakFallback : draft.ToString();
        var confidence = leaked ? 0.0 : DrafterAgent.ComputeConfidence(context);
        await RecordTurnAsync(conversation, request.Query, finalText, confidence, ct);

        var done = JsonSerializer.Serialize(new
        {
            confidence,
            conversationId = conversation.Id,
        });
        await Response.WriteAsync($"event: done\ndata: {done}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
