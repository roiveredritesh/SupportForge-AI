using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Triage;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class CodeElementQuestionTests
{
    [Theory]
    [InlineData("Which helper returns an object or raises a 404 error when it does not exist?")]
    [InlineData("Which method shortens a string and ends it with an ellipsis?")]
    [InlineData("Which pipe validates incoming request data against DTO classes using class-validator?")]
    [InlineData("Which decorator attaches custom metadata to a class or route handler?")]
    [InlineData("Which Reflector method retrieves metadata from several targets and merges the results?")]
    [InlineData("Which function applies relabeling rules to a set of labels?")]
    [InlineData("What exception is raised when a template cannot be found?")]
    [InlineData("Which type generates alerts from a PromQL expression?")]
    [InlineData("which built-in logger prints to the console?")]
    public void Matches_QuestionsThatAskForAPartOfTheCode(string query)
    {
        Assert.True(CodeElementQuestion.Matches(query));
    }

    [Theory]
    [InlineData("What type of account do I need for SSO?")]
    [InlineData("Which plan includes SAML SSO?")]
    [InlineData("Which SSO provider do you support?")]
    [InlineData("Which filter shows overdue tasks?")]
    [InlineData("How do I change the billing address on my account?")]
    [InlineData("it broke")]
    [InlineData("and the other one?")]
    public void DoesNotMatch_ProductQuestionsOrVagueMessages(string query)
    {
        Assert.False(CodeElementQuestion.Matches(query));
    }

    [Theory]
    [InlineData(TriageLabels.Unclear)]
    [InlineData(TriageLabels.KbQuestion)]
    public void Apply_TurnsUnclearAndKbIntoCodeQuestion_ForAWhichCodeElementQuestion(string intent)
    {
        var result = CodeElementQuestion.Apply(new TriagePolicyResult(intent, "top"), "Which class signs values?");

        Assert.Equal(new TriagePolicyResult(TriageLabels.CodeQuestion, "which-code-element"), result);
    }

    [Theory]
    [InlineData(TriageLabels.CodeIssue)]
    [InlineData(TriageLabels.Greeting)]
    [InlineData(TriageLabels.ScreenshotError)]
    public void Apply_KeepsOtherIntents(string intent)
    {
        var policy = new TriagePolicyResult(intent, "top");

        Assert.Same(policy, CodeElementQuestion.Apply(policy, "Which class signs values?"));
    }

    [Fact]
    public void Apply_KeepsUnclear_WhenTheQuestionDoesNotAskForCode()
    {
        var policy = new TriagePolicyResult(TriageLabels.Unclear, "top");

        Assert.Same(policy, CodeElementQuestion.Apply(policy, "can you check?"));
    }

    [Fact]
    public async Task TriageAgent_AnswersAnLlmUnclear_WhichCodeElementQuestion_AsCode()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), default)).ReturnsAsync("unclear");
        var agent = new TriageAgent(llm.Object, new ListLogger<TriageAgent>());

        var result = await agent.RunAsync(new AgentContext { ProjectId = "p", Query = "Which decorator attaches custom metadata?" });

        Assert.Equal(TriageLabels.CodeQuestion, result.Intent);
        Assert.Equal("which-code-element", result.IntentRule);
    }
}
