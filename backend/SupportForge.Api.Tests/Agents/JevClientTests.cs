using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportForge.Agents.Jev;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

/// <summary>
/// U4: JevClient wire format, typed accessors, error mapping, validation and DI. Driven through a fake
/// HttpMessageHandler, so no test reaches the network. Mirrors the convention in ConfluencePageFetcherTests.
/// </summary>
public class JevClientTests
{
    private const string ApiKey = "sk-test-SECRET-do-not-leak";
    private const string RequestId = "req_test_123";

    private static readonly object State = new { question = "Where is my order?", history = new[] { "hi", "my order is late" } };

    // Answers are built from fragments so each negative test changes one thing.
    private const string TopicAnswer = """{ "type": "choice", "choice": "billing", "confidence": 0.9, "probabilities": { "billing": 0.8, "technical": 0.2 } }""";
    private const string TopicOtherAnswer = """{ "type": "choice", "choice": "other", "confidence": 0.9, "probabilities": { "other": 1.0 } }""";
    private const string TopicNoConfidenceAnswer = """{ "type": "choice", "choice": "billing", "probabilities": { "billing": 1.0 } }""";
    private const string RefundAnswer = """{ "type": "noul", "noul": 0.98 }""";
    private const string UrgentAnswer = """{ "type": "noul", "noul": 0.12 }""";
    private const string SeverityAnswer = """{ "type": "score", "score": 1.7, "confidence": 0.9, "legend": { "0": "Low", "1": "Medium", "2": "High" }, "probabilities": { "0": 0.1, "1": 0.1, "2": 0.8 } }""";
    private const string ExtraAnswer = """{ "type": "noul", "noul": 0.5 }""";

    private static readonly string FullResponse = Body(
        ("topic", TopicAnswer),
        ("is_refund", RefundAnswer),
        ("is_urgent", UrgentAnswer),
        ("severity", SeverityAnswer),
        ("unasked_extra", ExtraAnswer));

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly string? _requestId;

        public StubHandler(HttpStatusCode status, string body, string? requestId = RequestId)
        {
            _status = status;
            _body = body;
            _requestId = requestId;
        }

        // Captured during SendAsync, because the request is disposed once the client call returns.
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Accept { get; private set; }
        public string? UserAgent { get; private set; }
        public string? ContentType { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Accept = request.Headers.Accept.ToString();
            UserAgent = request.Headers.UserAgent.ToString();
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            if (_requestId is not null)
                response.Headers.Add("x-typesafe-request-id", _requestId);
            return response;
        }
    }

    private static string Body(params (string Name, string Answer)[] answers)
    {
        var entries = string.Join(",", answers.Select(a => $"\"{a.Name}\":{a.Answer}"));
        return "{\"model\":\"jev-2026-09-15\",\"answers\":{" + entries + "},\"usage\":{\"input_tokens\":120,\"output_tokens\":12}}";
    }

    private static JevClient CreateClient(StubHandler handler, string? baseAddress = "https://api.typesafe.ai/", string apiKey = ApiKey)
    {
        var http = new HttpClient(handler) { BaseAddress = baseAddress is null ? null : new Uri(baseAddress) };
        return new JevClient(http, new JevOptions { ApiKey = apiKey });
    }

    private static IReadOnlyDictionary<string, JevQuestion> AllFourQuestions() => new Dictionary<string, JevQuestion>
    {
        ["topic"] = new JevChoiceQuestion(null, new Dictionary<string, string?> { ["billing"] = null, ["technical"] = "Technical problem" }),
        ["is_refund"] = new JevNoulQuestion("Is the customer asking for a refund?"),
        ["is_urgent"] = new JevNoulQuestion(null, WhenTrue: "The customer says the issue is urgent"),
        ["severity"] = new JevScoreQuestion(null, new[] { "Low", "Medium", "High" }),
    };

    private static IReadOnlyDictionary<string, JevQuestion> SingleNoul() => new Dictionary<string, JevQuestion>
    {
        ["is_refund"] = new JevNoulQuestion("Is the customer asking for a refund?"),
    };

    // ---- Request shape ----

    [Fact]
    public async Task EvaluateAsync_SerializesEachQuestionInTheContractShape()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FullResponse);

        await CreateClient(handler).EvaluateAsync(State, AllFourQuestions(), CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var root = doc.RootElement;
        Assert.Equal("jev-latest", root.GetProperty("model").GetString());

        // state is passed through unchanged, including the nested list.
        var sentState = root.GetProperty("state");
        Assert.Equal("Where is my order?", sentState.GetProperty("question").GetString());
        var history = sentState.GetProperty("history").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "hi", "my order is late" }, history);

        var questions = root.GetProperty("questions");
        Assert.Equal(4, questions.EnumerateObject().Count());

        // choice: null instructions are omitted, and a null description stays JSON null.
        var topic = questions.GetProperty("topic");
        Assert.Equal("choice", topic.GetProperty("type").GetString());
        Assert.False(topic.TryGetProperty("instructions", out _));
        var topicCriteria = topic.GetProperty("criteria");
        Assert.Equal(JsonValueKind.Null, topicCriteria.GetProperty("billing").ValueKind);
        Assert.Equal("Technical problem", topicCriteria.GetProperty("technical").GetString());

        // noul with instructions only: no criteria object at all.
        var refund = questions.GetProperty("is_refund");
        Assert.Equal("noul", refund.GetProperty("type").GetString());
        Assert.Equal("Is the customer asking for a refund?", refund.GetProperty("instructions").GetString());
        Assert.False(refund.TryGetProperty("criteria", out _));

        // noul with only WhenTrue: criteria carries "true" alone, and no instructions field.
        var urgent = questions.GetProperty("is_urgent");
        Assert.Equal("noul", urgent.GetProperty("type").GetString());
        Assert.False(urgent.TryGetProperty("instructions", out _));
        var urgentCriteria = urgent.GetProperty("criteria");
        Assert.Equal("The customer says the issue is urgent", urgentCriteria.GetProperty("true").GetString());
        Assert.False(urgentCriteria.TryGetProperty("false", out _));

        // score: an ordered array, so position is the level.
        var severity = questions.GetProperty("severity");
        Assert.Equal("score", severity.GetProperty("type").GetString());
        Assert.False(severity.TryGetProperty("instructions", out _));
        var levels = severity.GetProperty("criteria").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "Low", "Medium", "High" }, levels);
    }

    // ---- Transport ----

    [Theory]
    [InlineData("https://api.typesafe.ai")]
    [InlineData("https://api.typesafe.ai/")]
    public async Task EvaluateAsync_PostsToV1SystemoneWithBearerAuthAndHeaders(string baseAddress)
    {
        var handler = new StubHandler(HttpStatusCode.OK, FullResponse);

        await CreateClient(handler, baseAddress).EvaluateAsync(State, SingleNoul(), CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/v1/systemone", handler.RequestUri!.AbsolutePath);
        Assert.Equal($"Bearer {ApiKey}", handler.Authorization);
        Assert.Equal("application/json", handler.Accept);
        Assert.Equal("SupportForge/1.0", handler.UserAgent);
        Assert.Equal("application/json", handler.ContentType);
    }

    [Fact]
    public async Task EvaluateAsync_WithoutBaseAddress_FallsBackToOptionsBaseUrl()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FullResponse);

        await CreateClient(handler, baseAddress: null).EvaluateAsync(State, SingleNoul(), CancellationToken.None);

        Assert.Equal("https://api.typesafe.ai/v1/systemone", handler.RequestUri!.AbsoluteUri);
    }

    // ---- Response parsing ----

    [Fact]
    public async Task EvaluateAsync_ParsesEveryAnswerTypeUsageAndRequestId()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FullResponse);

        var result = await CreateClient(handler).EvaluateAsync(State, AllFourQuestions(), CancellationToken.None);

        Assert.Equal("jev-2026-09-15", result.Model);
        Assert.Equal(RequestId, result.RequestId);
        Assert.Equal(120, result.InputTokens);
        Assert.Equal(12, result.OutputTokens);
        Assert.Equal(132, result.TotalTokens);
        Assert.Equal(4, result.Answers.Count); // the unasked extra answer is dropped

        var topic = result.Choice("topic");
        Assert.Equal("billing", topic.Choice);
        Assert.Equal(0.9, topic.Confidence);
        Assert.Equal(0.8, topic.Probabilities["billing"]);
        Assert.Equal(0.2, topic.Probabilities["technical"]);

        Assert.Equal(0.98, result.Noul("is_refund").Noul);
        Assert.Equal(0.12, result.Noul("is_urgent").Noul);

        var severity = result.Score("severity");
        Assert.Equal(1.7, severity.Score);
        Assert.Equal(0.9, severity.Confidence);
        Assert.Equal(0.8, severity.Probabilities["2"]);
    }

    [Fact]
    public async Task TypedAccessors_ThrowOnMissingNameAndOnWrongType()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FullResponse);
        var result = await CreateClient(handler).EvaluateAsync(State, AllFourQuestions(), CancellationToken.None);

        Assert.Throws<JevResponseException>(() => result.Choice("not_asked"));
        Assert.Throws<JevResponseException>(() => result.Noul("topic"));
        Assert.Throws<JevResponseException>(() => result.Score("is_refund"));
    }

    [Fact]
    public async Task MissingAnswerForAskedQuestion_ThrowsJevResponseException()
    {
        var body = Body(("topic", TopicAnswer), ("is_urgent", UrgentAnswer), ("severity", SeverityAnswer)); // is_refund absent
        var handler = new StubHandler(HttpStatusCode.OK, body);

        var ex = await Assert.ThrowsAsync<JevResponseException>(() =>
            CreateClient(handler).EvaluateAsync(State, AllFourQuestions(), CancellationToken.None));

        Assert.Contains("is_refund", ex.Message);
        Assert.Equal(RequestId, ex.RequestId);
    }

    [Fact]
    public async Task AnswerWithDifferentType_ThrowsJevResponseException()
    {
        // is_refund answers as a score where a noul was asked.
        var body = Body(("topic", TopicAnswer), ("is_refund", SeverityAnswer), ("is_urgent", UrgentAnswer), ("severity", SeverityAnswer));
        var handler = new StubHandler(HttpStatusCode.OK, body);

        await Assert.ThrowsAsync<JevResponseException>(() =>
            CreateClient(handler).EvaluateAsync(State, AllFourQuestions(), CancellationToken.None));
    }

    [Fact]
    public async Task ChoiceOutsideTheAskedCriteria_ThrowsJevResponseException()
    {
        var body = Body(("topic", TopicOtherAnswer), ("is_refund", RefundAnswer), ("is_urgent", UrgentAnswer), ("severity", SeverityAnswer));
        var handler = new StubHandler(HttpStatusCode.OK, body);

        await Assert.ThrowsAsync<JevResponseException>(() =>
            CreateClient(handler).EvaluateAsync(State, AllFourQuestions(), CancellationToken.None));
    }

    [Fact]
    public async Task MissingOrNonNumericField_ThrowsJevResponseException()
    {
        var body = Body(("topic", TopicNoConfidenceAnswer), ("is_refund", RefundAnswer), ("is_urgent", UrgentAnswer), ("severity", SeverityAnswer));
        var handler = new StubHandler(HttpStatusCode.OK, body);

        await Assert.ThrowsAsync<JevResponseException>(() =>
            CreateClient(handler).EvaluateAsync(State, AllFourQuestions(), CancellationToken.None));
    }

    [Fact]
    public async Task MalformedJson_ThrowsJevResponseException()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{ not json");

        await Assert.ThrowsAsync<JevResponseException>(() =>
            CreateClient(handler).EvaluateAsync(State, AllFourQuestions(), CancellationToken.None));
    }

    // ---- Errors ----

    [Fact]
    public async Task Unauthorized_ThrowsJevApiExceptionWithStatusAndRequestIdWithoutTheKey()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, """{"detail":"Invalid API key"}""");

        var ex = await Assert.ThrowsAsync<JevApiException>(() =>
            CreateClient(handler).EvaluateAsync(State, SingleNoul(), CancellationToken.None));

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal(RequestId, ex.RequestId);
        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain(ApiKey, ex.Message);
    }

    [Fact]
    public async Task ValidationError_IncludesFirstDetailMessageButNotTheRequestBody()
    {
        var body = """{"detail":[{"loc":["body","state"],"msg":"Field required","type":"missing"}]}""";
        var handler = new StubHandler(HttpStatusCode.UnprocessableEntity, body);

        var ex = await Assert.ThrowsAsync<JevApiException>(() =>
            CreateClient(handler).EvaluateAsync(State, SingleNoul(), CancellationToken.None));

        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("Field required", ex.Message);
        // The request carried customer text. It must not come back in an exception message.
        Assert.DoesNotContain("Where is my order?", ex.Message);
        Assert.DoesNotContain(ApiKey, ex.Message);
    }

    // ---- Validation (rejected before any request is sent) ----

    [Fact]
    public async Task EmptyQuestions_ThrowsArgumentExceptionAndSendsNothing()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FullResponse);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateClient(handler).EvaluateAsync(State, new Dictionary<string, JevQuestion>(), CancellationToken.None));

        Assert.Null(handler.RequestBody);
    }

    [Fact]
    public async Task NoulWithoutInstructionsOrDescribedSide_ThrowsArgumentException()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FullResponse);
        var questions = new Dictionary<string, JevQuestion> { ["bare"] = new JevNoulQuestion(null) };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateClient(handler).EvaluateAsync(State, questions, CancellationToken.None));

        Assert.Null(handler.RequestBody);
    }

    // ---- DI ----

    [Fact]
    public void AddJev_WithoutApiKey_ReturnsFalseAndDoesNotRegisterTheClient()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jev:Model"] = "jev-custom" })
            .Build();

        var configured = services.AddJev(configuration);

        Assert.False(configured);
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<IJevClient>());
        var options = provider.GetRequiredService<JevOptions>();
        Assert.False(options.IsConfigured);
        Assert.Equal("jev-custom", options.Model);
    }

    [Fact]
    public void AddJev_WithApiKey_RegistersTypedClientAndBindsOptions()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jev:ApiKey"] = ApiKey,
                ["Jev:BaseUrl"] = "https://jev.example.test",
                ["Jev:Model"] = "jev-2026-09-15",
                ["Jev:Timeout"] = "00:00:03",
            })
            .Build();

        var configured = services.AddJev(configuration);

        Assert.True(configured);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<JevOptions>();
        Assert.True(options.IsConfigured);
        Assert.Equal("https://jev.example.test", options.BaseUrl);
        Assert.Equal("jev-2026-09-15", options.Model);
        Assert.Equal(TimeSpan.FromSeconds(3), options.Timeout);
        Assert.IsType<JevClient>(provider.GetRequiredService<IJevClient>());
    }
}
