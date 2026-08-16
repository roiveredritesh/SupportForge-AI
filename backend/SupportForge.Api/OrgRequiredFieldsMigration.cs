using System.Text.Json;
using SupportForge.Core.Entities;

namespace SupportForge.Api;

// Sprint 7: backfills Org.ContactPerson/ContactNumber/Industry for orgs written before those
// became required fields. Must run before any IOrgRepository operation (including
// ProjectOrgMigration, which reads orgs at startup) ever touches orgs.json -- JsonFileOrgRepository
// deserializes the whole file into List<Org> in one shot, and System.Text.Json throws for the
// *entire* list the moment a single record is missing a required property, so one legacy org
// breaks every org operation, not just that record. Reads the file directly with a permissive
// shape (bypassing the strict repository) to heal it in place; only then is it safe for the
// strict Org model to read the file.
public static class OrgRequiredFieldsMigration
{
    private sealed class LegacyOrg
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? ContactPerson { get; set; }
        public string? ContactNumber { get; set; }
        public string? Industry { get; set; }
        public string? Address { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public List<McpConnection> Connections { get; set; } = new();
    }

    public static async Task RunAsync(string dataDirectory, CancellationToken ct = default)
    {
        var filePath = Path.Combine(dataDirectory, "orgs.json");
        if (!File.Exists(filePath)) return;

        var raw = await File.ReadAllTextAsync(filePath, ct);
        var orgs = JsonSerializer.Deserialize<List<LegacyOrg>>(raw) ?? new();

        var changed = false;
        foreach (var org in orgs)
        {
            if (org.ContactPerson is null) { org.ContactPerson = "Unknown"; changed = true; }
            if (org.ContactNumber is null) { org.ContactNumber = "Unknown"; changed = true; }
            if (org.Industry is null) { org.Industry = "Unknown"; changed = true; }
        }

        if (!changed) return;

        await File.WriteAllTextAsync(
            filePath,
            JsonSerializer.Serialize(orgs, new JsonSerializerOptions { WriteIndented = true }),
            ct);
    }
}
