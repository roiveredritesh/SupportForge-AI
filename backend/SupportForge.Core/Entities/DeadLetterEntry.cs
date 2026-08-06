namespace SupportForge.Core.Entities;

// C7 (gap-closing-solutions.md Phase C, item 7): a job that exhausted IngestionBackgroundService's
// retry budget lands here instead of only appearing in logs -- JobType is job.GetType().Name (jobs
// don't expose their target location/URL publicly, and the type name plus ProjectId plus the error
// message is enough to know what needs attention and where to look).
public sealed record DeadLetterEntry(string Id, string ProjectId, string JobType, string Error, DateTimeOffset FailedAt);
