using System.Text.Encodings.Web;
using System.Text.Json;
using SupportForge.Agents.Jev;

namespace SupportForge.Agents.Triage;

// U5 (JEV intent classification plan), R2/R3: classifies with one Jev request. The Choice covers the five concrete
// labels only. "unclear" is not a Jev option, because TriageDecisionPolicy derives it from confidence and margin (R4, KTD2).
// The atomic signals from KTD3 go in the same request and feed the policy's override rules.
public sealed class JevIntentClassifier : IIntentClassifier
{
    private const string IntentQuestion = "intent";
    private const string IntentInstructions = "Which kind of support request is the latest customer message?";

    // One-line descriptions, adapted from LlmIntentClassifier's system prompt so both classifiers share one taxonomy.
    // This is a copy, not a shared constant, so the two are kept in step by hand: the code_question line names "which class,
    // type, interface, method, function, decorator, exception ..." in both, because the triage of those questions sent them to "unclear".
    private static readonly IReadOnlyDictionary<string, string> LabelDescriptions = new Dictionary<string, string>
    {
        [TriageLabels.KbQuestion] = "A product, feature or process question answered by documentation, not the source code.",
        [TriageLabels.CodeIssue] = "A bug report or error involving the product's source code.",
        [TriageLabels.CodeQuestion] = "A question about how the product's source code works that is not a bug report, such as how something works, where something is implemented, which class, type, interface, method, function, decorator, exception, module, middleware or service does something or is used for something, or architecture.",
        [TriageLabels.ScreenshotError] = "A question about an attached screenshot showing an error or UI state.",
        [TriageLabels.Greeting] = "A greeting, thanks, farewell or other pleasantry with no actual support question in it.",
    };

    // Sizes the state the way the wire carries it: the default encoder would turn every non-ASCII character into a
    // six-character escape, which would cut a non-English query far more than the limit requires.
    private static readonly JsonSerializerOptions MeasureOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IJevClient _jev;
    private readonly TriageOptions _options;

    public JevIntentClassifier(IJevClient jev, TriageOptions options)
    {
        _jev = jev;
        _options = options;
    }

    public async Task<IntentDecision> ClassifyAsync(AgentContext context, IReadOnlyList<string> labels, CancellationToken ct)
    {
        // `labels` is ignored. The Choice is fixed to TriageLabels.Concrete, so the probabilities always match the
        // policy's label set, and a caller can't change the question's options (U5). `labels` exists only for IIntentClassifier.
        var result = await _jev.EvaluateAsync(BuildState(context), BuildQuestions(), ct);
        return ReadDecision(result);
    }

    // Maps one Jev answer to an IntentDecision. Internal so TurnAssessor reads the combined request's triage answers with the same rules (U17).
    // It throws when the answer is missing or malformed, and callers decide what that means: ClassifyAsync lets it propagate.
    internal static IntentDecision ReadDecision(JevResult result)
    {
        // The typed accessors throw when an answer is missing or of the wrong type. That is a malformed response,
        // so it propagates and TriageAgent falls back to the LLM classifier (R6).
        var choice = result.Choice(IntentQuestion);

        var scores = new Dictionary<string, double>();
        foreach (var label in TriageLabels.Concrete)
        {
            // Jev's answer is partial when a concrete label has no probability. Treating that as "absent" would hide a
            // bad response, so it throws (R6). Extra labels in the answer are ignored.
            if (!choice.Probabilities.TryGetValue(label, out var probability))
            {
                throw new InvalidOperationException($"Jev intent answer has no probability for label '{label}'.");
            }

            scores[label] = probability;
        }

        var signals = new Dictionary<string, double>();
        foreach (var name in TriageSignals.Statements.Keys)
        {
            signals[name] = result.Noul(name).Noul;
        }

        return new IntentDecision(scores, choice.Confidence, signals, result.TotalTokens, "jev");
    }

    // R3 + U5 step 1. The state is the only place customer text goes, so question wording and criteria stay fixed in code.
    private object BuildState(AgentContext context)
    {
        var hasScreenshot = !string.IsNullOrEmpty(context.ScreenshotBase64);
        var keep = Math.Clamp(_options.HistoryTurns, 0, context.History.Count);
        var history = context.History.Skip(context.History.Count - keep).ToList();
        var query = context.Query;
        var limit = _options.MaxStateChars;

        // Drop the oldest turns first. The newest ones are the context that resolves follow-ups like "still broken".
        while (history.Count > 0 && Measure(query, history, hasScreenshot, context.ProductVersion) > limit)
        {
            history.RemoveAt(0);
        }

        // Then cut the query from its start, keeping its end. The question usually sits at the end of a long message.
        if (Measure(query, history, hasScreenshot, context.ProductVersion) > limit)
        {
            query = KeepEnd(query, history, hasScreenshot, context.ProductVersion, limit);
        }

        return Shape(query, history, hasScreenshot, context.ProductVersion);
    }

    // Largest suffix of `query` that still fits. Binary search on the suffix length, with every probe measured for real,
    // since JSON length is not exactly linear in the number of characters.
    private static string KeepEnd(
        string query,
        IReadOnlyList<(string Role, string Content)> history,
        bool hasScreenshot,
        string? productVersion,
        int limit)
    {
        var low = 0;
        var high = query.Length;
        while (low < high)
        {
            var mid = low + (high - low + 1) / 2;
            if (Measure(query[^mid..], history, hasScreenshot, productVersion) <= limit)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        var kept = query[^low..];

        // Don't start on the second half of a surrogate pair. A lone surrogate would reach Jev as an escape sequence.
        return kept.Length > 0 && char.IsLowSurrogate(kept[0]) ? kept[1..] : kept;
    }

    private static object Shape(
        string query,
        IReadOnlyList<(string Role, string Content)> history,
        bool hasScreenshot,
        string? productVersion) => new
    {
        query,
        history = history.Select(turn => new { role = turn.Role, content = turn.Content }).ToList(),
        hasScreenshot,
        productVersion,
    };

    private static int Measure(
        string query,
        IReadOnlyList<(string Role, string Content)> history,
        bool hasScreenshot,
        string? productVersion) =>
        JsonSerializer.Serialize(Shape(query, history, hasScreenshot, productVersion), MeasureOptions).Length;

    // Internal so TurnAssessor can add these questions to the combined guard request (U17). The names don't collide with the guard's.
    internal static IReadOnlyDictionary<string, JevQuestion> BuildQuestions()
    {
        var criteria = new Dictionary<string, string?>();
        foreach (var label in TriageLabels.Concrete)
        {
            criteria[label] = LabelDescriptions[label];
        }

        var questions = new Dictionary<string, JevQuestion>
        {
            [IntentQuestion] = new JevChoiceQuestion(IntentInstructions, criteria),
        };

        // KTD3: each signal is one atomic statement, asked in isolation. Its key is the signal name, so the answer
        // comes back under the name TriageSignals and the policy use.
        foreach (var (name, statement) in TriageSignals.Statements)
        {
            questions[name] = new JevNoulQuestion(statement);
        }

        return questions;
    }
}
