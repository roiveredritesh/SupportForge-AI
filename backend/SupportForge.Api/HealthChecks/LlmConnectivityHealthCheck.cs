using Microsoft.Extensions.Diagnostics.HealthChecks;
using SupportForge.Agents;

namespace SupportForge.Api.HealthChecks;

// U6: reports whether the configured LLM client resolved from DI without throwing (bad config,
// missing required settings for the selected provider). Deliberately does NOT make a live
// completion call on every health poll -- that would cost real money/rate-limit budget per
// poll interval. This checks configuration/wiring health, not live API reachability.
// ponytail: upgrade path if live reachability is ever needed -- add a provider-native lightweight
// probe (e.g. a models-list call) behind a much longer poll interval than /health's default.
public sealed class LlmConnectivityHealthCheck : IHealthCheck
{
    private readonly ILlmChatClient _llm;

    public LlmConnectivityHealthCheck(ILlmChatClient llm)
    {
        _llm = llm;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Resolution already happened via constructor injection -- reaching this line proves the
        // configured provider's client built successfully.
        return Task.FromResult(_llm is not null
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("LLM client failed to resolve."));
    }
}
