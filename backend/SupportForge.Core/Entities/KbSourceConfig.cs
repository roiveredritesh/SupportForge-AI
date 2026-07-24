namespace SupportForge.Core.Entities;

public enum KbSourceType { Documents, Confluence }

public sealed record KbSourceConfig(KbSourceType Type, string Location, DateTimeOffset? LastSyncedAt);
