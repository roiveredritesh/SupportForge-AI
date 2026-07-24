using Microsoft.AspNetCore.Mvc;
using SupportForge.Agents;
using SupportForge.Api.Contracts;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private readonly CoordinatorPipeline _pipeline;

    public ChatController(CoordinatorPipeline pipeline) => _pipeline = pipeline;

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

        return Ok(new ChatQueryResponse
        {
            Draft = result.Draft,
            Confidence = result.Confidence,
            Sources = result.Sources.Select(s => new SourceDto(s.Label, s.Url)).ToList(),
        });
    }
}
