using System.Reflection;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

// R9 (JEV intent classification plan): the Drafter prompt gets one extra line only for the "medium" confidence band.
public class DrafterIntentHintTests
{
    private const string MediumLine =
        "Intent confidence: medium. Before answering, restate in one short clause how you understood the question, then answer it.";

    // Verbatim copy of DrafterAgent.BuildUserPrompt as it was before R9, with the same raw literal shape (8-space
    // indentation, interpolation holes, closing delimiter). An unset band must produce exactly this text.
    // Take(5) matches DrafterAgent.MaxSnippetsPerSource, and the test contexts never hold more snippets than that.
    private static string LegacyBuildUserPrompt(AgentContext context) => $"""
        Conversation so far: {(context.History.Count == 0 ? "(none)" : string.Join("\n", context.History.Select(h => $"{h.Role}: {h.Content}")))}
        Customer question: {context.Query}
        Intent: {context.Intent}
        Freshness: {LegacyDescribeFreshness(context.Freshness)}
        ProductVersion: {(string.IsNullOrEmpty(context.ProductVersion) ? "(not specified)" : context.ProductVersion)}
        <retrieved_context source="kb">
        {string.Join("\n---\n", context.KbSnippets.Take(5))}
        </retrieved_context>
        <retrieved_context source="code">
        {string.Join("\n---\n", context.CodeSnippets.Take(5))}
        </retrieved_context>
        <retrieved_context source="vision">
        {context.VisionFindings}
        </retrieved_context>
        """;

    // DescribeFreshness is private and unchanged by R9, so the legacy copy calls the real one rather than duplicating it.
    private static string LegacyDescribeFreshness(FreshnessContext? freshness) =>
        (string)typeof(DrafterAgent)
            .GetMethod("DescribeFreshness", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { freshness })!;

    private static AgentContext MakeContext(string? band, bool withHistory, string? productVersion)
    {
        var context = new AgentContext
        {
            ProjectId = "proj1",
            Query = "How do I export a report?",
            Intent = "kb_question",
            IntentConfidenceBand = band,
            ProductVersion = productVersion,
        };
        if (withHistory)
        {
            context.History.Add(("Customer", "The export button is missing"));
            context.History.Add(("Assistant", "Which version are you on?"));
        }
        context.KbSnippets.Add("Reports are exported from the Reports page.");
        context.KbSnippets.Add("Large exports can take a few minutes.");
        return context;
    }

    public static TheoryData<bool, bool> HistoryAndVersionCombinations => new()
    {
        { false, false },
        { true, false },
        { false, true },
        { true, true },
    };

    [Theory]
    [MemberData(nameof(HistoryAndVersionCombinations))]
    public void NullBand_PromptIsByteIdenticalToPreR9Prompt(bool withHistory, bool withProductVersion)
    {
        var context = MakeContext(band: null, withHistory, withProductVersion ? "2.4" : null);

        Assert.Equal(LegacyBuildUserPrompt(context), DrafterAgent.BuildUserPrompt(context));
    }

    [Theory]
    [MemberData(nameof(HistoryAndVersionCombinations))]
    public void HighBand_PromptIsByteIdenticalToPreR9Prompt(bool withHistory, bool withProductVersion)
    {
        var context = MakeContext(band: "high", withHistory, withProductVersion ? "2.4" : null);

        var prompt = DrafterAgent.BuildUserPrompt(context);

        Assert.Equal(LegacyBuildUserPrompt(context), prompt);
        Assert.DoesNotContain("Intent confidence", prompt);
    }

    [Theory]
    [MemberData(nameof(HistoryAndVersionCombinations))]
    public void MediumBand_AddsOnlyTheHintLineDirectlyAfterTheIntentLine(bool withHistory, bool withProductVersion)
    {
        var context = MakeContext(band: "medium", withHistory, withProductVersion ? "2.4" : null);

        var prompt = DrafterAgent.BuildUserPrompt(context);

        // Every line of the pre-R9 prompt is still there, in order. The only difference is the hint line after "Intent:".
        var expected = LegacyBuildUserPrompt(context).Replace(
            "Intent: kb_question\n",
            "Intent: kb_question\n" + MediumLine + "\n");
        Assert.Equal(expected, prompt);
    }

    [Fact]
    public void MediumBand_HintLineSitsImmediatelyBetweenIntentAndFreshness()
    {
        var context = MakeContext(band: "medium", withHistory: false, productVersion: null);

        var lines = DrafterAgent.BuildUserPrompt(context).Split('\n');

        var intentIndex = Array.FindIndex(lines, line => line == "Intent: kb_question");
        Assert.True(intentIndex >= 0, "the Intent line is missing");
        Assert.Equal(MediumLine, lines[intentIndex + 1]);
        Assert.StartsWith("Freshness: ", lines[intentIndex + 2]);
        Assert.Single(lines.Where(line => line.StartsWith("Intent confidence:", StringComparison.Ordinal)));
    }
}
