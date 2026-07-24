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
    private readonly ITokenUsageRepository _tokenUsage;
    private readonly ILlmClient _llm;

    public ChatController(CoordinatorPipeline pipeline, ITokenUsageRepository tokenUsage, ILlmClient llm)
    {
        _pipeline = pipeline;
        _tokenUsage = tokenUsage;
        _llm = llm;
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
        await _tokenUsage.AddAsync(new TokenUsageEntry(request.ProjectId, ((OpenAiLlmClient)_llm).LastTotalTokens, DateTimeOffset.UtcNow), ct);

        return Ok(new ChatQueryResponse
        {
            Draft = result.Draft,
            Confidence = result.Confidence,
            Sources = result.Sources.Select(s => new SourceDto(s.Label, s.Url)).ToList(),
        });
    }
}
