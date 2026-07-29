using SupportForge.Agents.Tools;

namespace SupportForge.Agents;

public sealed class CodeAnalyzerAgent : IAgent
{
    private readonly CodeSearchTool _tool;
    public string Name => "CodeAnalyzer";

    public CodeAnalyzerAgent(CodeSearchTool tool) => _tool = tool;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        if (context.Intent is not ("code_issue" or "code_question")) return context;

        var topK = context.CodeVerification.Attempts > 0 ? 10 : 5;
        var results = await _tool.SearchAsync(context.ProjectId, context.Query, topK, ct: ct);

        context.CodeSnippets.Clear();
        foreach (var (text, file) in results)
        {
            context.CodeSnippets.Add($"// {file}\n{text}");
            context.Sources.Add(($"Code: {file}", file));
        }
        context.CodeVerification.Attempts++;
        return context;
    }
}
