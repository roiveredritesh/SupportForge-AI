using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Api.Identity;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<AppUser> _userManager;
    private readonly IConfiguration _configuration;

    public AuthController(UserManager<AppUser> userManager, IConfiguration configuration)
    {
        _userManager = userManager;
        _configuration = configuration;
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
}
