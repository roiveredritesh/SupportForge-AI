using SupportForge.Agents.Tools;

namespace SupportForge.Agents;

public sealed class KbResearcherAgent : IAgent
{
    private readonly KbSearchTool _tool;
    public string Name => "KbResearcher";

    public KbResearcherAgent(KbSearchTool tool) => _tool = tool;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        var topK = context.KbVerification.Attempts > 0 ? 10 : 5;
        var results = await _tool.SearchAsync(context.ProjectId, context.Query, topK, ct: ct);

        context.KbSnippets.Clear();
        foreach (var (text, source) in results)
        {
            context.KbSnippets.Add(text);
            context.Sources.Add(($"KB: {Path.GetFileName(source)}", source));
        }
        context.KbVerification.Attempts++;
        return context;
    }
}
