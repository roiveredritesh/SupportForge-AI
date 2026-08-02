using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Identity;

// Issues and validates JWTs with the same signing key (ResolveSigningKey), so AuthController
// (issuing) and Program.cs's AddJwtBearer (validating) never drift out of sync.
public static class JwtTokenFactory
{
    // ponytail: Jwt:Key follows the same "blank placeholder committed, real value via
    // IConfiguration/environment variable" convention as every LLM ApiKey in appsettings.json
    // (see LlmServiceCollectionExtensions, which tolerates a blank ApiKey the same way). Unlike an
    // LLM key, an empty signing key would throw when constructing SymmetricSecurityKey, which
    // would take the whole app down at first request -- so when Jwt:Key is unset this falls back to
    // one random key per process. That keeps a single instance internally consistent (tokens it
    // issues validate against itself) but such tokens won't survive a restart or work across
    // instances. Set Jwt__Key via environment/user-secrets for any real deployment.
    private static readonly Lazy<string> FallbackKey = new(() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    public static SymmetricSecurityKey ResolveSigningKey(IConfiguration configuration)
    {
        var key = configuration["Jwt:Key"];
        if (string.IsNullOrEmpty(key)) key = FallbackKey.Value;
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }

    public static string ResolveIssuer(IConfiguration configuration) => configuration["Jwt:Issuer"] ?? "SupportForge";
    public static string ResolveAudience(IConfiguration configuration) => configuration["Jwt:Audience"] ?? "SupportForge";

    public static (string Token, DateTimeOffset ExpiresAt) Create(AppUser user, IConfiguration configuration)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.UserName),
        };
        var credentials = new SigningCredentials(ResolveSigningKey(configuration), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            ResolveIssuer(configuration),
            ResolveAudience(configuration),
            claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
