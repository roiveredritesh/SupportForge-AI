namespace SupportForge.Agents.Jev;

/// <summary>
/// Configuration for the TypeSafe Jev client, bound from the "Jev" config section (env
/// <c>Jev__ApiKey</c>). Plain options object: U4 binds it directly rather than through
/// <c>IOptions</c>, because callers need it as a singleton they can check with <see cref="IsConfigured"/>
/// before deciding whether to wire any Jev-backed feature.
/// </summary>
public sealed class JevOptions
{
    // Source: docs/integrations/typesafe-jev-api.md (Endpoint and auth). The SDKs read TYPESAFE_BASE_URL.
    public string BaseUrl { get; set; } = "https://api.typesafe.ai";

    // Bearer key. Never logged or put into an exception message (U4).
    public string ApiKey { get; set; } = "";

    // Contract default is "jev-latest"; the response reports the resolved model, which may differ.
    public string Model { get; set; } = "jev-latest";

    // Whole-request HTTP timeout. Callers on the critical path (guard, triage, judges) apply their own
    // tighter budgets via CancellationToken, so this is only the backstop.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    // Retries for 408/429/5xx/transport failures. The contract's SDKs retry twice; SupportForge retries
    // once because Jev sits on a request's critical path (U4).
    public int MaxRetries { get; set; } = 1;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
