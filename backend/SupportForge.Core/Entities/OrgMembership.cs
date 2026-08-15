namespace SupportForge.Core.Entities;

// Sprint 0 (U1): records that a user belongs to an org. Mirrors ProjectMembership -- the creator of
// an org is auto-joined at creation time (self-service, same design as project creation). No Role
// field yet: Sprint 1 will extend this if/when org-level roles are needed.
public sealed record OrgMembership(string OrgId, string UserId, DateTimeOffset CreatedAt);
