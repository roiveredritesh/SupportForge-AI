using System.Text.Json;
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Jev;
using SupportForge.Agents.Triage;

namespace SupportForge.Api.Tests.Agents;

// U5 (JEV intent classification plan): JevIntentClassifier sends one Jev request (state, the intent Choice and the signal Nouls)
// and maps the answer to an IntentDecision. Customer text must only ever reach the state, never a question.
public class JevIntentClassifierTests
{
    private const string IntentInstructions = "Which kind of support request is the latest customer message?";

    private sealed class Capture
    {
        public object? State { get; set; }
        public IReadOnlyDictionary<string, JevQuestion>? Questions { get; set; }
    }

    private static AgentContext NewContext(string query = "how does checkout work?") =>
        new() { ProjectId = "proj1", Query = query };

    private static Dictionary<string, double> DefaultProbabilities() => new()
    {
        [TriageLabels.KbQuestion] = 0.80,
        [TriageLabels.CodeIssue] = 0.05,
        [TriageLabels.CodeQuestion] = 0.05,
        [TriageLabels.ScreenshotError] = 0.05,
        [TriageLabels.Greeting] = 0.05,
    };

    private static Dictionary<string, double> DefaultSignals() => new()
    {
        [TriageSignals.HasSupportQuestion] = 0.9,
        [TriageSignals.ReportsMalfunction] = 0.1,
        [TriageSignals.AboutSourceCode] = 0.2,
        [TriageSignals.RefersToImage] = 0.05,
    };

    private static JevResult ResultWith(
        Dictionary<string, double> probabilities,
        double confidence,
        Dictionary<string, double> signals,
        string choice = TriageLabels.KbQuestion)
    {
        var answers = new Dictionary<string, JevAnswer>
        {
            ["intent"] = new JevChoiceAnswer(choice, confidence, probabilities),
        };
        foreach (var (name, value) in signals)
        {
            answers[name] = new JevNoulAnswer(value);
        }

        return new JevResult("jev-latest", answers, 100, 20, "req-1");
    }

    private static Mock<IJevClient> MockJev(JevResult result, Capture? capture = null)
    {
        var jev = new Mock<IJevClient>();
        jev.Setup(j => j.EvaluateAsync(It.IsAny<object>(), It.IsAny<IReadOnlyDictionary<string, JevQuestion>>(), It.IsAny<CancellationToken>()))
           .Callback((object state, IReadOnlyDictionary<string, JevQuestion> questions, CancellationToken ct) =>
           {
               if (capture is not null)
               {
                   capture.State = state;
                   capture.Questions = questions;
               }
           })
           .ReturnsAsync(result);
        return jev;
    }

    private static JsonDocument StateJson(object? state) => JsonDocument.Parse(JsonSerializer.Serialize(state));

    [Fact]
    public async Task ClassifyAsync_AsksIntentChoiceOverExactlyTheFiveConcreteLabels()
    {
        var capture = new Capture();
        var classifier = new JevIntentClassifier(
            MockJev(ResultWith(DefaultProbabilities(), 0.9, DefaultSignals()), capture).Object,
            new TriageOptions());

        // The labels argument is ignored, so passing something else must not change the Choice.
        await classifier.ClassifyAsync(NewContext(), new[] { "bogus" }, CancellationToken.None);

        var choice = Assert.IsType<JevChoiceQuestion>(capture.Questions!["intent"]);
        Assert.Equal(IntentInstructions, choice.Instructions);
        Assert.Equal(TriageLabels.Concrete.ToArray(), choice.Criteria.Keys.ToArray());
        Assert.Equal("A product, feature or process question answered by documentation, not the source code.", choice.Criteria[TriageLabels.KbQuestion]);
        Assert.Equal("A bug report or error involving the product's source code.", choice.Criteria[TriageLabels.CodeIssue]);
        Assert.Equal(
            "A question about how the product's source code works that is not a bug report, such as how something works, where something is implemented, which class, method, function, decorator, exception, module, middleware or service does something or is used for something, or architecture.",
            choice.Criteria[TriageLabels.CodeQuestion]);
        Assert.Equal("A question about an attached screenshot showing an error or UI state.", choice.Criteria[TriageLabels.ScreenshotError]);
        Assert.Equal("A greeting, thanks, farewell or other pleasantry with no actual support question in it.", choice.Criteria[TriageLabels.Greeting]);

        // "unclear" is policy-derived, so it is never a Jev option.
        Assert.DoesNotContain(TriageLabels.Unclear, choice.Criteria.Keys);
    }

    [Theory]
    [InlineData("has_support_question", "The latest message contains an actual support question or request, not only a greeting or thanks.")]
    [InlineData("reports_malfunction", "The user reports that something is broken, failing, or showing an error.")]
    [InlineData("about_source_code", "The question is about how the product's source code is implemented.")]
    [InlineData("refers_to_image", "The latest message refers to an attached image or screenshot.")]
    public async Task ClassifyAsync_AsksEachSignalAsAnAtomicNoulWithFixedStatement(string name, string statement)
    {
        var capture = new Capture();
        var classifier = new JevIntentClassifier(
            MockJev(ResultWith(DefaultProbabilities(), 0.9, DefaultSignals()), capture).Object,
            new TriageOptions());

        await classifier.ClassifyAsync(NewContext(), TriageLabels.All, CancellationToken.None);

        var noul = Assert.IsType<JevNoulQuestion>(capture.Questions![name]);
        Assert.Equal(statement, noul.Instructions);
    }

    [Fact]
    public async Task ClassifyAsync_AsksExactlyTheIntentChoiceAndFourSignals()
    {
        var capture = new Capture();
        var classifier = new JevIntentClassifier(
            MockJev(ResultWith(DefaultProbabilities(), 0.9, DefaultSignals()), capture).Object,
            new TriageOptions());

        await classifier.ClassifyAsync(NewContext(), TriageLabels.All, CancellationToken.None);

        Assert.Equal(5, capture.Questions!.Count);
        Assert.Contains("intent", capture.Questions!.Keys);
    }

    [Fact]
    public async Task ClassifyAsync_CustomerTextAppearsOnlyInState()
    {
        const string query = "zebra-quota-xyz please help";
        const string historyText = "history-marker-abc from earlier";
        var capture = new Capture();
        var options = new TriageOptions();
        var classifier = new JevIntentClassifier(
            MockJev(ResultWith(DefaultProbabilities(), 0.9, DefaultSignals()), capture).Object,
            options);
        var context = NewContext(query);
        context.History.Add(("user", historyText));

        await classifier.ClassifyAsync(context, TriageLabels.All, CancellationToken.None);

        foreach (var question in capture.Questions!.Values)
        {
            Assert.DoesNotContain("zebra-quota", question.Instructions ?? string.Empty);
            Assert.DoesNotContain("history-marker", question.Instructions ?? string.Empty);
            if (question is JevChoiceQuestion choice)
            {
                foreach (var text in choice.Criteria.Values)
                {
                    Assert.DoesNotContain("zebra-quota", text ?? string.Empty);
                    Assert.DoesNotContain("history-marker", text ?? string.Empty);
                }
            }
        }

        Assert.NotNull(capture.State);
        var json = JsonSerializer.Serialize(capture.State);
        Assert.Contains("zebra-quota-xyz please help", json);
        Assert.Contains("history-marker-abc from earlier", json);
    }

    [Fact]
    public async Task ClassifyAsync_StateCarriesScreenshotAndProductVersion()
    {
        var capture = new Capture();
        var classifier = new JevIntentClassifier(
            MockJev(ResultWith(DefaultProbabilities(), 0.9, DefaultSignals()), capture).Object,
            new TriageOptions());
        var context = new AgentContext
        {
            ProjectId = "p",
            Query = "it shows an error",
            ScreenshotBase64 = "aGVsbG8=",
            ProductVersion = "4.2.1",
        };

        await classifier.ClassifyAsync(context, TriageLabels.All, CancellationToken.None);

        using var doc = StateJson(capture.State);
        Assert.True(doc.RootElement.GetProperty("hasScreenshot").GetBoolean());
        Assert.Equal("4.2.1", doc.RootElement.GetProperty("productVersion").GetString());
    }

    [Fact]
    public async Task ClassifyAsync_TrimsHistoryToNewestTurns()
    {
        var capture = new Capture();
        var classifier = new JevIntentClassifier(
            MockJev(ResultWith(DefaultProbabilities(), 0.9, DefaultSignals()), capture).Object,
            new TriageOptions { HistoryTurns = 2 });
        var context = NewContext();
        context.History.Add(("user", "turn-1"));
        context.History.Add(("assistant", "turn-2"));
        context.History.Add(("user", "turn-3"));
        context.History.Add(("assistant", "turn-4"));

        await classifier.ClassifyAsync(context, TriageLabels.All, CancellationToken.None);

        using var doc = StateJson(capture.State);
        var history = doc.RootElement.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        Assert.Equal("user", history[0].GetProperty("role").GetString());
        Assert.Equal("turn-3", history[0].GetProperty("content").GetString());
        Assert.Equal("assistant", history[1].GetProperty("role").GetString());
        Assert.Equal("turn-4", history[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ClassifyAsync_WhenStateTooLarge_DropsOldestHistoryFirst()
    {
        var capture = new Capture();
        const int limit = 400;
        var classifier = new JevIntentClassifier(
            MockJev(ResultWith(DefaultProbabilities(), 0.9, DefaultSignals()), capture).Object,
            new TriageOptions { HistoryTurns = 4, MaxStateChars = limit });
        var context = NewContext("short question");
        for (var i = 1; i <= 4; i++)
        {
            context.History.Add(("user", $"turn-{i}-" + new string('x', 100)));
        }

        await classifier.ClassifyAsync(context, TriageLabels.All, CancellationToken.None);

        var json = JsonSerializer.Serialize(capture.State);
        Assert.True(json.Length <= limit, $"state was {json.Length} chars");
        using var doc = StateJson(capture.State);
        Assert.Equal("short question", doc.RootElement.GetProperty("query").GetString());
        var history = doc.RootElement.GetProperty("history").EnumerateArray().ToList();
        Assert.True(history.Count < 4, "oldest turns should have been dropped");
        if (history.Count > 0)
        {
            // The kept turns are the newest ones, so the last kept turn is the newest of all.
            Assert.StartsWith("turn-4-", history[^1].GetProperty("content").GetString());
        }
    }

    [Fact]
    public async Task ClassifyAsync_WhenQueryTooLarge_KeepsItsEnd()
    {
        var capture = new Capture();
        const int limit = 300;
        var classifier = new JevIntentClassifier(
            MockJev(ResultWith(DefaultProbabilities(), 0.9, DefaultSignals()), capture).Object,
            new TriageOptions { MaxStateChars = limit });
        var query = new string('x', 1000) + "NEWEST-TAIL";

        await classifier.ClassifyAsync(NewContext(query), TriageLabels.All, CancellationToken.None);

        var json = JsonSerializer.Serialize(capture.State);
        Assert.True(json.Length <= limit, $"state was {json.Length} chars");
        using var doc = StateJson(capture.State);
        var kept = doc.RootElement.GetProperty("query").GetString()!;
        Assert.EndsWith("NEWEST-TAIL", kept);
        Assert.True(kept.Length < query.Length);
    }

    [Fact]
    public async Task ClassifyAsync_WhenQueryFits_IsSentUntruncated()
    {
        var capture = new Capture();
        var classifier = new JevIntentClassifier(
            MockJev(ResultWith(DefaultProbabilities(), 0.9, DefaultSignals()), capture).Object,
            new TriageOptions { MaxStateChars = 8000 });

        await classifier.ClassifyAsync(NewContext("where is checkout implemented?"), TriageLabels.All, CancellationToken.None);

        using var doc = StateJson(capture.State);
        Assert.Equal("where is checkout implemented?", doc.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task ClassifyAsync_MapsProbabilitiesConfidenceSignalsTokensAndSource()
    {
        var probabilities = new Dictionary<string, double>
        {
            [TriageLabels.KbQuestion] = 0.55,
            [TriageLabels.CodeQuestion] = 0.38,
            [TriageLabels.CodeIssue] = 0.03,
            [TriageLabels.ScreenshotError] = 0.02,
            [TriageLabels.Greeting] = 0.02,
        };
        var signals = new Dictionary<string, double>
        {
            [TriageSignals.HasSupportQuestion] = 0.9,
            [TriageSignals.ReportsMalfunction] = 0.1,
            [TriageSignals.AboutSourceCode] = 0.3,
            [TriageSignals.RefersToImage] = 0.02,
        };
        var classifier = new JevIntentClassifier(
            MockJev(ResultWith(probabilities, 0.77, signals)).Object,
            new TriageOptions());

        var decision = await classifier.ClassifyAsync(NewContext(), TriageLabels.All, CancellationToken.None);

        Assert.Equal(5, decision.Scores.Count);
        foreach (var (label, probability) in probabilities)
        {
            Assert.Equal(probability, decision.Scores[label]);
        }

        Assert.Equal(0.77, decision.Confidence);
        Assert.Equal(4, decision.Signals.Count);
        foreach (var (name, value) in signals)
        {
            Assert.Equal(value, decision.Signals[name]);
        }

        Assert.Equal(120, decision.TokensUsed);
        Assert.Equal("jev", decision.Source);
    }

    [Fact]
    public async Task ClassifyAsync_IgnoresUnexpectedLabelsInProbabilities()
    {
        var probabilities = DefaultProbabilities();
        probabilities["unclear"] = 0.1;
        probabilities["billing"] = 0.05;
        var classifier = new JevIntentClassifier(
            MockJev(ResultWith(probabilities, 0.9, DefaultSignals())).Object,
            new TriageOptions());

        var decision = await classifier.ClassifyAsync(NewContext(), TriageLabels.All, CancellationToken.None);

        Assert.Equal(TriageLabels.Concrete.Count, decision.Scores.Count);
        Assert.DoesNotContain("unclear", decision.Scores.Keys);
        Assert.DoesNotContain("billing", decision.Scores.Keys);
    }

    [Fact]
    public async Task ClassifyAsync_WhenConcreteLabelMissingFromProbabilities_Throws()
    {
        // A partial answer is malformed (R6). Throwing lets TriageAgent fall back to the LLM classifier.
        var probabilities = DefaultProbabilities();
        probabilities.Remove(TriageLabels.Greeting);
        var classifier = new JevIntentClassifier(
            MockJev(ResultWith(probabilities, 0.9, DefaultSignals())).Object,
            new TriageOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => classifier.ClassifyAsync(NewContext(), TriageLabels.All, CancellationToken.None));
    }

    [Fact]
    public async Task ClassifyAsync_WhenJevThrows_PropagatesException()
    {
        var jev = new Mock<IJevClient>();
        jev.Setup(j => j.EvaluateAsync(It.IsAny<object>(), It.IsAny<IReadOnlyDictionary<string, JevQuestion>>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new TimeoutException("jev timed out"));
        var classifier = new JevIntentClassifier(jev.Object, new TriageOptions());

        await Assert.ThrowsAsync<TimeoutException>(
            () => classifier.ClassifyAsync(NewContext(), TriageLabels.All, CancellationToken.None));
    }
}
