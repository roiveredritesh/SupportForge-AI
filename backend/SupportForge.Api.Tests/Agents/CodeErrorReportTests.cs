using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Triage;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class CodeErrorReportTests
{
    [Theory]
    [InlineData("The login fails with a SecurityException \"User is at their maximum number of sessions.\" Where is this check done?")]
    [InlineData("A recording rule using label_replace started failing to evaluate. Where is the regex compiled?")]
    [InlineData("I set LoginAttemptsBeforeLockout to 0 but the account was disabled. Why does 0 behave like 3?")]
    [InlineData("httpAdapter.setCookie(res, 'session', token) blows up. Which part of Nest builds the Set-Cookie header?")]
    [InlineData("We set --storage.tsdb.retention.size=50GB. Why are blocks being removed before the limit?")]
    [InlineData("Our client sends ?active=1 and gets a 400. Where does Nest decide which values count as a boolean?")]
    [InlineData("Nest can't resolve dependencies of the OrdersService. Where does Nest decide to print the hint?")]
    [InlineData("Startup dies with an InvalidOperationException about free space. What decides the threshold?")]
    [InlineData("Rendering crashes with ValueError: \"Missing staticfiles manifest entry\". What code raises this?")]
    public void Matches_BugReportsThatNameCodeAndAskWhereOrWhy(string query)
    {
        Assert.True(CodeErrorReport.Matches(query));
    }

    [Theory]
    [InlineData("Why was I charged twice this month?")]
    [InlineData("Where can I find my invoices?")]
    [InlineData("Why does my iPhone app log me out?")]
    [InlineData("Why can't I connect my PayPal account?")]
    [InlineData("I get a TimeoutException when I export.")]
    [InlineData("How do I change the billing address on my account?")]
    [InlineData("it broke")]
    public void DoesNotMatch_ProductQuestionsOrReportsThatDoNotAsk(string query)
    {
        Assert.False(CodeErrorReport.Matches(query));
    }

    [Theory]
    [InlineData(TriageLabels.Unclear)]
    [InlineData(TriageLabels.KbQuestion)]
    public void Apply_TurnsUnclearAndKbIntoCodeIssue_ForACodeErrorReport(string intent)
    {
        var result = CodeErrorReport.Apply(new TriagePolicyResult(intent, "top"), "Why does label_replace never match?");

        Assert.Equal(new TriagePolicyResult(TriageLabels.CodeIssue, "code-error-report"), result);
    }

    [Theory]
    [InlineData(TriageLabels.CodeQuestion)]
    [InlineData(TriageLabels.Greeting)]
    [InlineData(TriageLabels.ScreenshotError)]
    public void Apply_KeepsOtherIntents(string intent)
    {
        var policy = new TriagePolicyResult(intent, "top");

        Assert.Same(policy, CodeErrorReport.Apply(policy, "Why does label_replace never match?"));
    }

    [Fact]
    public async Task TriageAgent_AnswersAnLlmUnclear_CodeErrorReport_AsCodeIssue()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), default)).ReturnsAsync("unclear");
        var agent = new TriageAgent(llm.Object, new ListLogger<TriageAgent>());

        var result = await agent.RunAsync(new AgentContext { ProjectId = "p", Query = "We set honor_labels to false. Where are conflicting labels renamed?" });

        Assert.Equal(TriageLabels.CodeIssue, result.Intent);
        Assert.Equal("code-error-report", result.IntentRule);
    }
}
