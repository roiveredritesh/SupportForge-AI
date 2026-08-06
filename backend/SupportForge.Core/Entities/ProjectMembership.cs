namespace SupportForge.Core.Entities;

// B1 (gap-closing-solutions.md Phase B): records that a user may access a project. The creator of a
// project is granted membership automatically at creation time (self-service creation, per the agreed
// design) -- there is currently no admin-gated creation path.
public sealed record ProjectMembership(string ProjectId, string UserId, DateTimeOffset CreatedAt);
