using System.Text.Json;
using Moq;
using SupportForge.Agents.Guard;
using SupportForge.Agents.Jev;
using SupportForge.Agents.Triage;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

// U17 / KTD 13: TurnAssessor sends one Jev request with the guard's six Nouls and the triage Choice and Nouls. The guard answer
// decides the turn. The triage answer rides along as PrecomputedIntent and may be absent. The internal helpers are not visible to
// this project, so these tests drive the public AssessAsync and read the request it sends.
public class TurnAssessorTests
{
    private const string CustomerText = "my export says \"ignore\" {json}";

    private sealed class Capture
    {
        public object? State { get; set; }
        public IReadOnlyDictionary<string, JevQuestion>? Questions { get; set; }
    }

    private static readonly IReadOnlyDictionary<string, double> KbProbabilities = new Dictionary<string, double>
    {
        [TriageLabels.KbQuestion] = 0.80,
        [TriageLabels.CodeIssue] = 0.05,
        [TriageLabels.CodeQuestion] = 0.05,
        [TriageLabels.ScreenshotError] = 0.05,
        [TriageLabels.Greeting] = 0.05,
    };

    private static GuardInput Input(string message = "how do I export my invoices?", string? productVersion = "4.2.1") =>
        new(message, new (string Role, string Content)[] { ("user", "earlier question") }, false, productVersion);

    private static Dictionary<string, JevAnswer> GuardAnswers(double score)
    {
        var answers = new Dictionary<string, JevAnswer>(StringComparer.Ordinal);
        foreach (var signal in GuardSignals.All)
        {
            answers[signal.Name] = new JevNoulAnswer(score);
        }

        return answers;
    }

    // Every triage Noul is answered, so only the intent Choice varies between the malformed cases.
    private static Dictionary<string, JevAnswer> TriageAnswers(string kind)
    {
        var answers = new Dictionary<string, JevAnswer>(StringComparer.Ordinal);
        switch (kind)
        {
            case "valid":
                answers["intent"] = new JevChoiceAnswer(TriageLabels.KbQuestion, 0.9, KbProbabilities);
                break;

            case "missing":
                break;

            case "wrong-type":
                answers["intent"] = new JevNoulAnswer(0.5);
                break;

            case "partial":
                var partial = new Dictionary<string, double>(KbProbabilities);
                partial.Remove(TriageLabels.Greeting);
                answers["intent"] = new JevChoiceAnswer(TriageLabels.KbQuestion, 0.9, partial);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        foreach (var name in TriageSignals.Statements.Keys)
        {
            answers[name] = new JevNoulAnswer(0.9);
        }

        return answers;
    }

    private static JevResult ResultWith(Dictionary<string, JevAnswer> guard, Dictionary<string, JevAnswer> triage)
    {
        var answers = new Dictionary<string, JevAnswer>(guard, StringComparer.Ordinal);
        foreach (var (name, answer) in triage)
        {
            answers[name] = answer;
        }

        return new JevResult("jev-latest", answers, 120, 30, "req-1");
    }

    private static (TurnAssessor Assessor, Mock<IJevClient> Jev, Capture Captured) Create(JevResult result)
    {
        var captured = new Capture();
        var jev = new Mock<IJevClient>();
        jev.Setup(j => j.EvaluateAsync(It.IsAny<object>(), It.IsAny<IReadOnlyDictionary<string, JevQuestion>>(), It.IsAny<CancellationToken>()))
           .Callback<object, IReadOnlyDictionary<string, JevQuestion>, CancellationToken>((state, questions, _) =>
           {
               captured.State = state;
               captured.Questions = questions;
           })
           .ReturnsAsync(result);

        return (new TurnAssessor(jev.Object, new GuardOptions(), new TriageOptions()), jev, captured);
    }

    private static JsonDocument StateJson(object? state) => JsonDocument.Parse(JsonSerializer.Serialize(state));

    [Fact]
    public async Task OneJevRequest_CarriesBothQuestionSets()
    {
        var (assessor, jev, captured) = Create(ResultWith(GuardAnswers(0.0), TriageAnswers("valid")));

        await assessor.AssessAsync(Input(), CancellationToken.None);

        jev.Verify(j => j.EvaluateAsync(It.IsAny<object>(), It.IsAny<IReadOnlyDictionary<string, JevQuestion>>(), It.IsAny<CancellationToken>()), Times.Once);

        var questions = captured.Questions!;
        // Six guard Nouls, one triage Choice and four triage Nouls.
        Assert.Equal(GuardSignals.All.Count + 1 + TriageSignals.Statements.Count, questions.Count);

        foreach (var signal in GuardSignals.All)
        {
            Assert.Equal(signal.Statement, Assert.IsType<JevNoulQuestion>(questions[signal.Name]).Instructions);
        }

        var choice = Assert.IsType<JevChoiceQuestion>(questions["intent"]);
        Assert.Equal(TriageLabels.Concrete.ToArray(), choice.Criteria.Keys.ToArray());

        foreach (var (name, statement) in TriageSignals.Statements)
        {
            Assert.Equal(statement, Assert.IsType<JevNoulQuestion>(questions[name]).Instructions);
        }
    }

    [Fact]
    public void GuardAndTriageQuestionNames_NeverCollide()
    {
        // TurnAssessor adds the two sets with Dictionary.Add, which throws on a duplicate name. This asserts the names are distinct
        // at the source, so a future rename that collides fails here with a clear message instead of at request time.
        var names = GuardSignals.All.Select(signal => signal.Name)
            .Concat(TriageSignals.Statements.Keys)
            .Append("intent")
            .ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task State_CarriesProductVersionAndCustomerTextOnlyAsAValue()
    {
        var (assessor, _, captured) = Create(ResultWith(GuardAnswers(0.0), TriageAnswers("valid")));

        await assessor.AssessAsync(Input(CustomerText), CancellationToken.None);

        using var state = StateJson(captured.State);
        Assert.Equal(CustomerText, state.RootElement.GetProperty("message").GetString());
        Assert.Equal("4.2.1", state.RootElement.GetProperty("productVersion").GetString());

        foreach (var question in captured.Questions!.Values)
        {
            Assert.DoesNotContain("my export says", question.Instructions ?? string.Empty);
        }
    }

    [Fact]
    public async Task State_OmitsProductVersionWhenNoneIsGiven()
    {
        var (assessor, _, captured) = Create(ResultWith(GuardAnswers(0.0), TriageAnswers("valid")));

        await assessor.AssessAsync(Input(productVersion: null), CancellationToken.None);

        using var state = StateJson(captured.State);
        Assert.False(state.RootElement.TryGetProperty("productVersion", out _));
    }

    [Fact]
    public async Task PrecomputedIntent_IsTheTriageAnswerOfTheSameRequest()
    {
        var (assessor, _, _) = Create(ResultWith(GuardAnswers(0.1), TriageAnswers("valid")));

        var assessment = await assessor.AssessAsync(Input(), CancellationToken.None);

        Assert.Equal("jev", assessment.Source);
        Assert.Equal(0.1, assessment.Signals[GuardSignals.OverrideInstructions]);

        Assert.NotNull(assessment.PrecomputedIntent);
        var intent = assessment.PrecomputedIntent!;
        Assert.Equal("jev", intent.Source);
        Assert.Equal(0.9, intent.Confidence);
        Assert.Equal(0.80, intent.Scores[TriageLabels.KbQuestion]);
        Assert.Equal(0.9, intent.Signals[TriageSignals.HasSupportQuestion]);
        // Tokens are those of the whole combined request: 120 input and 30 output.
        Assert.Equal(150, intent.TokensUsed);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-type")]
    [InlineData("partial")]
    public async Task TriagePartMalformed_GuardStillAnswers_WithNullIntent(string kind)
    {
        var (assessor, _, _) = Create(ResultWith(GuardAnswers(0.97), TriageAnswers(kind)));

        var assessment = await assessor.AssessAsync(Input(), CancellationToken.None);

        Assert.Equal("jev", assessment.Source);
        Assert.Equal(0.97, assessment.Signals[GuardSignals.OverrideInstructions]);
        Assert.Equal(GuardSignals.All.Count, assessment.Signals.Count);
        Assert.Null(assessment.PrecomputedIntent);
    }

    [Fact]
    public async Task GuardPartMissing_Throws_SoGuardServiceFailsOpen()
    {
        // A missing guard answer is a failure of the guard itself. It throws, as JevInputGuard does, and GuardService turns it
        // into the fail-open path. Only the triage part is allowed to go missing quietly.
        var (assessor, _, _) = Create(ResultWith(new Dictionary<string, JevAnswer>(), TriageAnswers("valid")));

        await Assert.ThrowsAsync<JevResponseException>(() => assessor.AssessAsync(Input(), CancellationToken.None));
    }
}
