using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace SupportForge.Api;

public static class ControllerBaseExtensions
{
    // Same claim RateLimiter's partition key already reads off an authenticated request
    // (Program.cs) -- the JWT's "sub" claim auto-maps to ClaimTypes.NameIdentifier.
    public static string CurrentUserId(this ControllerBase controller) =>
        controller.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated request has no NameIdentifier claim.");

    // U6: the JWT's "role" claim auto-maps to ClaimTypes.Role (same inbound-claim-type mapping
    // JwtSecurityTokenHandler applies to "sub" -> NameIdentifier above) -- this is also what
    // [Authorize(Roles="Admin")] checks against.
    public static SupportForge.Core.Entities.AppRole CurrentUserRole(this ControllerBase controller) =>
        Enum.TryParse<SupportForge.Core.Entities.AppRole>(controller.User.FindFirstValue(ClaimTypes.Role), out var role)
            ? role
            : SupportForge.Core.Entities.AppRole.L1;
}
