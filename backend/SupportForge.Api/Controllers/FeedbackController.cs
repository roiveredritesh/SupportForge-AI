using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/feedback")]
[Authorize]
public class FeedbackController : ControllerBase
{
    private readonly IFeedbackRepository _repo;

    public FeedbackController(IFeedbackRepository repo) => _repo = repo;

    public sealed record SubmitRequest(string ProjectId, string Query, bool? Useful, bool Escalated);

    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] SubmitRequest request, CancellationToken ct = default)
    {
        await _repo.AddAsync(new FeedbackEntry(request.ProjectId, request.Query, request.Useful, request.Escalated, DateTimeOffset.UtcNow), ct);
        return Ok();
    }
}
