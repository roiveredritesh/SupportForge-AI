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
    private readonly VisionAnalyzerAgent _visionAnalyzer;
    private readonly ILlmClient _llm;
    private readonly ITokenUsageRepository _tokenUsage;

    public ChatController(
        CoordinatorPipeline pipeline,
        TriageAgent triage,
        KbResearcherAgent kbResearcher,
        CodeAnalyzerAgent codeAnalyzer,
        VisionAnalyzerAgent visionAnalyzer,
        ILlmClient llm,
        ITokenUsageRepository tokenUsage)
    {
        _pipeline = pipeline;
        _triage = triage;
        _kbResearcher = kbResearcher;
        _codeAnalyzer = codeAnalyzer;
        _visionAnalyzer = visionAnalyzer;
        _llm = llm;
        _tokenUsage = tokenUsage;
    }

    [HttpPost("query")]
    public async Task<ActionResult<ChatQueryResponse>> Query([FromBody] ChatQueryRequest request, CancellationToken ct = default)
    {
        var context = new AgentContext
        {
            ProjectId = request.ProjectId,
            Query = request.Query,
            ScreenshotBase64 = request.ScreenshotBase64,
        };

        var result = await _pipeline.RunAsync(context, ct);
        await _tokenUsage.AddAsync(new TokenUsageEntry(request.ProjectId, result.TotalTokensUsed, DateTimeOffset.UtcNow), ct);

        return Ok(new ChatQueryResponse
        {
            Draft = result.Draft,
            Confidence = result.Confidence,
            Sources = result.Sources.Select(s => new SourceDto(s.Label, s.Url)).ToList(),
        });
    }

    // Runs every agent except the Drafter as before, then streams the Drafter's answer to the
    // client token-by-token over SSE instead of waiting for the full completion.
    [HttpPost("query/stream")]
    public async Task QueryStream([FromBody] ChatQueryRequest request, CancellationToken ct)
    {
        var context = new AgentContext
        {
            ProjectId = request.ProjectId,
            Query = request.Query,
            ScreenshotBase64 = request.ScreenshotBase64,
        };

        context = await _triage.RunAsync(context, ct);
        context = await _kbResearcher.RunAsync(context, ct);
        context = await _codeAnalyzer.RunAsync(context, ct);
        context = await _visionAnalyzer.RunAsync(context, ct);

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        await foreach (var token in _llm.StreamCompleteAsync(DrafterAgent.SystemPrompt, DrafterAgent.BuildUserPrompt(context), ct))
        {
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(token)}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        context.TotalTokensUsed += _llm.LastTotalTokens;
        await _tokenUsage.AddAsync(new TokenUsageEntry(request.ProjectId, context.TotalTokensUsed, DateTimeOffset.UtcNow), ct);

        var done = JsonSerializer.Serialize(new
        {
            confidence = DrafterAgent.ComputeConfidence(context),
            sources = context.Sources.Select(s => new SourceDto(s.Label, s.Url)),
        });
        await Response.WriteAsync($"event: done\ndata: {done}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
