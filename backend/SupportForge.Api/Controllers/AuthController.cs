using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Api.Identity;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<AppUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly IOrgRepository _orgs;
    private readonly IOrgMembershipRepository _orgMemberships;

    public AuthController(
        UserManager<AppUser> userManager, IConfiguration configuration, IOrgRepository orgs, IOrgMembershipRepository orgMemberships)
    {
        _userManager = userManager;
        _configuration = configuration;
        _orgs = orgs;
        _orgMemberships = orgMemberships;
    }

    public sealed record TokenRequest(string UserName, string Password);
    public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt);

    [HttpPost("token")]
    public async Task<ActionResult<TokenResponse>> Token([FromBody] TokenRequest request)
    {
        var user = await _userManager.FindByNameAsync(request.UserName);
        if (user is null || !await _userManager.CheckPasswordAsync(user, request.Password))
            return Unauthorized();

        var (accessToken, expiresAt) = JwtTokenFactory.Create(user, _configuration);
        return Ok(new TokenResponse(accessToken, expiresAt));
    }

    // No default/seeded user ships with this repo -- this is how you create the first one.
    // Deliberately unauthenticated: there's nothing to authenticate with until a user exists.
    // U4: kept open indefinitely (multi-tenant self-service) -- this is the org-creating "sign up
    // my company" path, distinct from OrgsController.RegisterEmployee (Admin-gated, no org creation).
    // Every self-service registrant becomes Admin of a brand-new org, one org per admin.
    [HttpPost("register")]
    public async Task<ActionResult<TokenResponse>> Register([FromBody] TokenRequest request)
    {
        if (await _userManager.FindByNameAsync(request.UserName) is not null)
            return Conflict("Username is already taken.");

        var user = new AppUser { Id = Guid.NewGuid().ToString("n"), UserName = request.UserName, Role = AppRole.Admin };
        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(result.Errors.Select(e => e.Description));

        var org = new Org { Id = Guid.NewGuid().ToString("n"), Name = $"{request.UserName}'s Org" };
        await _orgs.UpsertAsync(org);
        await _orgMemberships.AddAsync(user.Id, org.Id);

        var (accessToken, expiresAt) = JwtTokenFactory.Create(user, _configuration);
        return Ok(new TokenResponse(accessToken, expiresAt));
    }
}
