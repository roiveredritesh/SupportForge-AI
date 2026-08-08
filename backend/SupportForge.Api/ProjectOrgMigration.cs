using System.Text.Json;
using System.Text.Json.Nodes;

namespace SupportForge.Api;

// Backfill for pre-existing single-tenant deployments: Project.OrgId is now required, but
// projects.json predates it and old rows have no such property. Runs against the raw JSON (not
// IProjectRepository) because System.Text.Json enforces `required` members on deserialization --
// reading old rows through the typed repository would throw before this migration ever got a
// chance to patch them. Seeds a "default" org from the old single global GitHub:Token config
// value (if set), so an existing deployment upgrades with zero manual steps.
public static class ProjectOrgMigration
{
    public static async Task RunAsync(string dataDirectory, string? legacyGithubToken)
    {
        var projectsPath = Path.Combine(dataDirectory, "projects.json");
        if (!File.Exists(projectsPath)) return;

        // File-persisted JSON uses plain (Pascal-case) property names -- these repositories serialize
        // with default JsonSerializerOptions, unlike the camelCase web defaults ASP.NET Core applies
        // to API responses.
        var projectsArray = JsonNode.Parse(await File.ReadAllTextAsync(projectsPath))?.AsArray();
        if (projectsArray is null || !projectsArray.Any(p => p?["OrgId"] is null)) return;

        var orgsPath = Path.Combine(dataDirectory, "orgs.json");
        if (!File.Exists(orgsPath))
        {
            var defaultOrg = new JsonObject
            {
                ["Id"] = "default",
                ["Name"] = "Default",
                ["GitHubAccessToken"] = legacyGithubToken,
                ["CreatedAt"] = DateTimeOffset.UtcNow,
            };
            var orgsJson = new JsonArray(defaultOrg).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(orgsPath, orgsJson);
        }

        foreach (var project in projectsArray)
            project!["OrgId"] ??= "default";

        await File.WriteAllTextAsync(projectsPath, projectsArray.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
