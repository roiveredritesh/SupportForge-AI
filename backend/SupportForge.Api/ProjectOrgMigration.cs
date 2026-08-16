using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api;

// Sprint 0 (U2): backfills Project.OrgId for projects that predate the Org concept. Runs once,
// synchronously, before the app starts serving requests (see Program.cs). Idempotent: a fixed
// well-known org id means a second run finds the default org already there and, since every
// project by then already has an OrgId, does no further work.
public static class ProjectOrgMigration
{
    public const string DefaultOrgId = "default-org";

    public static async Task RunAsync(IProjectRepository projects, IOrgRepository orgs, CancellationToken ct = default)
    {
        var all = await projects.GetAllAsync(ct);
        var unassigned = all.Where(p => string.IsNullOrEmpty(p.OrgId)).ToList();
        if (unassigned.Count == 0) return;

        var defaultOrg = await orgs.GetByIdAsync(DefaultOrgId, ct);
        if (defaultOrg is null)
        {
            defaultOrg = new Org { Id = DefaultOrgId, Name = "Default Org", ContactPerson = "Unknown", ContactNumber = "Unknown", Industry = "Unknown" };
            await orgs.UpsertAsync(defaultOrg, ct);
        }

        foreach (var project in unassigned)
        {
            var backfilled = new Project
            {
                Id = project.Id,
                Name = project.Name,
                Repos = project.Repos,
                KbSources = project.KbSources,
                CreatedAt = project.CreatedAt,
                ScheduledSyncIntervalHours = project.ScheduledSyncIntervalHours,
                OrgId = defaultOrg.Id,
            };
            await projects.UpsertAsync(backfilled, ct);
        }
    }
}
