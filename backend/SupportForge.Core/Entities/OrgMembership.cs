namespace SupportForge.Core.Entities;

// Mirrors ProjectMembership: records that a user may access an org. The creator of an org is
// granted membership automatically at creation time (self-service creation) -- there is currently
// no admin-gated creation path.
public sealed record OrgMembership(string OrgId, string UserId, DateTimeOffset CreatedAt);
