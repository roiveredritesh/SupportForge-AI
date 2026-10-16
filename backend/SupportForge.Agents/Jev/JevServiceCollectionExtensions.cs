using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace SupportForge.Agents.Jev;

public static class JevServiceCollectionExtensions
{
    private const string ConfigSection = "Jev";

    /// <summary>
    /// Binds the "Jev" config section into a <see cref="JevOptions"/> singleton, which is always registered so
    /// callers can check <see cref="JevOptions.IsConfigured"/>. <see cref="IJevClient"/> is registered only when a key
    /// is set, so with the key blank every Jev-backed feature is inert (U4). Returns whether it was registered.
    /// </summary>
    public static bool AddJev(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new JevOptions();
        configuration.GetSection(ConfigSection).Bind(options);
        services.AddSingleton(options);

        if (!options.IsConfigured)
            return false;

        // The base address is normalized with a trailing slash, so the relative "v1/systemone" never drops a path
        // segment (U4). An absolute BaseUrl is required, so a bad value fails here at startup.
        services.AddHttpClient<IJevClient, JevClient>(client =>
            {
                client.BaseAddress = JevClient.WithTrailingSlash(new Uri(options.BaseUrl));
                client.Timeout = options.Timeout;
            })
            .AddResilienceHandler("jev-retry", builder => AddJevRetry(builder, options.MaxRetries));
        return true;
    }

    // U4: at most one retry by default, because Jev sits on the guard and triage critical path. The default
    // HttpRetryStrategyOptions.ShouldHandle covers 408, 429, 5xx and HttpRequestException. A 401 or 422 is not
    // retried, because it would fail identically again.
    private static void AddJevRetry(ResiliencePipelineBuilder<HttpResponseMessage> builder, int maxRetries)
    {
        if (maxRetries <= 0)
            return;

        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = maxRetries,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromMilliseconds(250),
            UseJitter = true,
        });
    }
}
