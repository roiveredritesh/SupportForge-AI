using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class DrafterAgentTests
{
    [Fact]
    public void BuildUserPrompt_WithNoHistory_ShowsNone()
    {
        var context = new AgentContext { ProjectId = "proj1", Query = "Getting a 500 error" };

        var prompt = DrafterAgent.BuildUserPrompt(context);

        Assert.Contains("Conversation so far: (none)", prompt);
    }

    [Fact]
    public void BuildUserPrompt_WithHistory_IncludesRecap()
    {
        var context = new AgentContext { ProjectId = "proj1", Query = "What about the other file?" };
        context.History.Add(("user", "Getting a 500 error on checkout"));
        context.History.Add(("assistant", "The 500 comes from OrderService.cs:42"));

        var prompt = DrafterAgent.BuildUserPrompt(context);

        Assert.Contains("user: Getting a 500 error on checkout", prompt);
        Assert.Contains("assistant: The 500 comes from OrderService.cs:42", prompt);
    }

    [Fact]
    public void BuildUserPrompt_CarriesFileCitedCodeSnippets_FromCodeAnalyzer()
    {
        var context = new AgentContext { ProjectId = "proj1", Query = "why does checkout fail" };
        context.CodeSnippets.Add("// OrderService.cs\npublic void Checkout() { throw new Exception(); }");

        var prompt = DrafterAgent.BuildUserPrompt(context);

        Assert.Contains("// OrderService.cs", prompt);
    }
}
