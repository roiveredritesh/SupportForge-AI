using SupportForge.Agents.Tools;

namespace SupportForge.Agents;

public sealed class CodeAnalyzerAgent : IAgent
{
    private readonly CodeSearchTool _tool;
    public string Name => "CodeAnalyzer";

    public CodeAnalyzerAgent(CodeSearchTool tool) => _tool = tool;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        if (context.Intent != "code_issue") return context;

        var results = await _tool.SearchAsync(context.ProjectId, context.Query, ct: ct);
        foreach (var (text, file) in results)
        {
            context.CodeSnippets.Add(text);
            context.Sources.Add(($"Code: {file}", file));
        }
        return context;
    }
}
