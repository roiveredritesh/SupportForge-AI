using System.Net.Http.Headers;

namespace SupportForge.Agents.Jev;

/// <summary>
/// One Jev request: state plus N questions in, N typed answers out. Each question is evaluated independently
/// against the same state (docs/integrations/typesafe-jev-api.md). The client is logger-free by design (U4):
/// callers log the request id and outcome, which is all that is safe to log.
/// </summary>
public interface IJevClient
{
    // state is serialized with System.Text.Json as-is. Its text is customer content, so callers must not log it.
    Task<JevResult> EvaluateAsync(object state, IReadOnlyDictionary<string, JevQuestion> questions, CancellationToken ct);
}

public sealed class JevClient : IJevClient
{
    private const string EvaluatePath = "v1/systemone";

    private readonly HttpClient _http;
    private readonly JevOptions _options;
    private readonly Uri _baseUri;

    public JevClient(HttpClient http, JevOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // DI sets BaseAddress. A bare HttpClient falls back to BaseUrl. The endpoint is always built as an absolute
        // URI on a trailing-slash base, so "https://api.typesafe.ai" and "https://api.typesafe.ai/" both resolve to
        // /v1/systemone. A bare relative "v1/systemone" would drop the last path segment of a base without "/" (U4).
        _baseUri = WithTrailingSlash(http.BaseAddress ?? new Uri(options.BaseUrl));
    }

    public async Task<JevResult> EvaluateAsync(object state, IReadOnlyDictionary<string, JevQuestion> questions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questions);

        // Validation runs inside BuildRequestBody, before any bytes leave the process (U4).
        var body = JevWire.BuildRequestBody(_options.Model, state, questions);

        // Refuse to send an unauthenticated request. The composition root only registers this client when a key
        // is set, so reaching here blank is a wiring bug.
        if (!_options.IsConfigured)
            throw new InvalidOperationException("Jev is not configured: Jev:ApiKey is blank.");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, EvaluatePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("SupportForge/1.0");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _http.SendAsync(request, ct);
        var responseBody = response.Content is null
            ? Array.Empty<byte>()
            : await response.Content.ReadAsByteArrayAsync(ct);
        var requestId = JevWire.ReadRequestId(response);

        if (!response.IsSuccessStatusCode)
            throw JevWire.ToApiException(response.StatusCode, requestId, responseBody);

        return JevWire.ParseResponse(responseBody, questions, _options.Model, requestId);
    }

    // Shared with DI so both sides normalize the base the same way.
    internal static Uri WithTrailingSlash(Uri uri) =>
        uri.AbsolutePath.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}

/// <summary>Jev answered with a non-2xx status. The message names the status (and, for 422, the first validation detail) but never the key or request body.</summary>
public sealed class JevApiException : Exception
{
    public JevApiException(int statusCode, string? requestId, string message) : base(message)
    {
        StatusCode = statusCode;
        RequestId = requestId;
    }

    public int StatusCode { get; }

    public string? RequestId { get; }
}

/// <summary>Jev answered 2xx, but the body does not match what was asked: malformed JSON, a missing or mistyped answer, or an unknown choice.</summary>
public sealed class JevResponseException : Exception
{
    public JevResponseException(string message, string? requestId = null) : base(message)
    {
        RequestId = requestId;
    }

    public string? RequestId { get; }
}
