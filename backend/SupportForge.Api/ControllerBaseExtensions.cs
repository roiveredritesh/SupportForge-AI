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
}
