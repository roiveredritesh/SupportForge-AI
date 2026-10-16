using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Verification;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

// U11: LlmFindingsJudge is the extraction of the verifiers' and the Drafter's LLM judge calls. These tests pin
// the parts that must not change: which system prompt is sent, which snippets get numbered, how the verdict is
// parsed, and where the token count comes from.
public class LlmFindingsJudgeTests
{
    private static Mock<ILlmChatClient> ClientReturning(string verdict, int tokens = 42)
    {
        var llm = new Mock<ILlmChatClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(verdict);
        llm.SetupGet(l => l.LastTotalTokens).Returns(tokens);
        return llm;
    }

    private static AgentContext NewContext(string query = "how do I reset my password?") =>
        new AgentContext { ProjectId = "p", Query = query, Intent = "kb_question" };

    [Theory]
    [InlineData("2", 1)]
    [InlineData("1", 0)]
    [InlineData("3", 2)]
    [InlineData(" 1 \n", 0)]
    [InlineData("none", null)]
    [InlineData("7", null)]
    [InlineData("0", null)]
    public async Task JudgeSnippetsAsync_Kb_ParsesOneBasedVerdictIntoZeroBasedIndex(string verdict, int? expectedIndex)
    {
        var judge = new LlmFindingsJudge(ClientReturning(verdict, tokens: 42).Object);
        var snippets = new List<string> { "a", "b", "c" };

        var judgement = await judge.JudgeSnippetsAsync(NewContext(), FindingsKind.Kb, snippets, CancellationToken.None);

        Assert.Equal(expectedIndex, judgement.BestIndex);
        Assert.Null(judgement.Scores);
        Assert.Null(judgement.Coverage);
        Assert.Equal(42, judgement.TokensUsed);
        Assert.Equal("llm", judgement.Source);
    }

    [Fact]
    public async Task JudgeSnippetsAsync_KbAndCode_SendDifferentSystemPrompts()
    {
        var systems = new List<string>();
        var llm = new Mock<ILlmChatClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((sys, _, _) => systems.Add(sys))
            .ReturnsAsync("none");
        var judge = new LlmFindingsJudge(llm.Object);
        var snippets = new List<string> { "snippet" };

        await judge.JudgeSnippetsAsync(NewContext(), FindingsKind.Kb, snippets, CancellationToken.None);
        await judge.JudgeSnippetsAsync(NewContext(), FindingsKind.Code, snippets, CancellationToken.None);

        Assert.Equal(2, systems.Count);
        Assert.Contains("knowledge-base snippets", systems[0]);
        Assert.DoesNotContain("code-graph snippets", systems[0]);
        Assert.Contains("code-graph snippets", systems[1]);
        Assert.DoesNotContain("knowledge-base snippets", systems[1]);
    }

    [Fact]
    public async Task JudgeSnippetsAsync_NumbersThePassedSnippets_NotContextKbSnippets()
    {
        string? userPrompt = null;
        var llm = new Mock<ILlmChatClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, usr, _) => userPrompt = usr)
            .ReturnsAsync("2");
        var judge = new LlmFindingsJudge(llm.Object);
        var context = NewContext("which one applies?");
        context.KbSnippets.Add("only in context");
        var passed = new List<string> { "passed first", "passed second" };

        var judgement = await judge.JudgeSnippetsAsync(context, FindingsKind.Kb, passed, CancellationToken.None);

        Assert.NotNull(userPrompt);
        Assert.Contains("Customer question: which one applies?", userPrompt);
        Assert.Contains("1. passed first", userPrompt);
        Assert.Contains("2. passed second", userPrompt);
        Assert.DoesNotContain("only in context", userPrompt);
        // "2" is in range for the two passed snippets even though context.KbSnippets holds only one.
        Assert.Equal(1, judgement.BestIndex);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData(" YES\n", true)]
    [InlineData("No", false)]
    [InlineData("maybe", false)]
    public async Task JudgeVisionAsync_RelevantOnlyForYes(string verdict, bool expectedRelevant)
    {
        var judge = new LlmFindingsJudge(ClientReturning(verdict, tokens: 9).Object);
        var context = NewContext();
        context.VisionFindings = "The screenshot shows a 500 error dialog.";

        var judgement = await judge.JudgeVisionAsync(context, CancellationToken.None);

        Assert.Equal(expectedRelevant, judgement.Relevant);
        Assert.Null(judgement.Score);
        Assert.Equal(9, judgement.TokensUsed);
        Assert.Equal("llm", judgement.Source);
    }

    [Theory]
    [InlineData("grounded", true)]
    [InlineData("UNGROUNDED ", false)]
    [InlineData("ungrounded", false)]
    public async Task JudgeGroundednessAsync_UngroundedOnlyWhenVerdictSaysSo(string verdict, bool expectedGrounded)
    {
        var judge = new LlmFindingsJudge(ClientReturning(verdict, tokens: 13).Object);

        var judgement = await judge.JudgeGroundednessAsync(NewContext(), "a draft", CancellationToken.None);

        Assert.Equal(expectedGrounded, judgement.Grounded);
        Assert.Null(judgement.UnsupportedSentences);
        Assert.Equal(13, judgement.TokensUsed);
        Assert.Equal("llm", judgement.Source);
    }

    [Fact]
    public async Task JudgeGroundednessAsync_PromptCarriesDraftAndAtMostFiveSnippetsPerSource()
    {
        string? systemPrompt = null;
        string? userPrompt = null;
        var llm = new Mock<ILlmChatClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((sys, usr, _) => { systemPrompt = sys; userPrompt = usr; })
            .ReturnsAsync("grounded");
        var judge = new LlmFindingsJudge(llm.Object);
        var context = NewContext();
        for (var i = 1; i <= 7; i++)
        {
            context.KbSnippets.Add($"kb snippet {i}");
            context.CodeSnippets.Add($"code snippet {i}");
        }

        await judge.JudgeGroundednessAsync(context, "DRAFT-MARKER", CancellationToken.None);

        Assert.NotNull(systemPrompt);
        Assert.Contains("You judge whether a drafted support answer", systemPrompt);
        Assert.NotNull(userPrompt);
        Assert.Contains("DRAFT-MARKER", userPrompt);
        Assert.Contains("kb snippet 5", userPrompt);
        Assert.DoesNotContain("kb snippet 6", userPrompt);
        Assert.Contains("code snippet 5", userPrompt);
        Assert.DoesNotContain("code snippet 6", userPrompt);
    }

    [Fact]
    public void JudgesAllVisionFindings_IsFalse_KeepingTheTwentyCharacterGate()
    {
        var judge = new LlmFindingsJudge(new Mock<ILlmChatClient>().Object);

        Assert.False(judge.JudgesAllVisionFindings);
    }
}
