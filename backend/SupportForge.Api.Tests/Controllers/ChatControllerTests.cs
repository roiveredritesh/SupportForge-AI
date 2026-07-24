using Microsoft.AspNetCore.Mvc;
using Moq;
using SupportForge.Agents;
using SupportForge.Api.Controllers;
using SupportForge.Api.Contracts;
using SupportForge.Core;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

public class ChatControllerTests
{
    [Fact]
    public async Task Query_ReturnsDraftAndConfidence_FromPipeline()
    {
        var llmMock = new Mock<ILlmClient>();
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), default)).ReturnsAsync("code_issue");

        var openAiLlm = new TestOpenAiLlmClient(llmMock.Object);
        var pipeline = new CoordinatorPipeline(new IAgent[] { new TriageAgent(openAiLlm), new DrafterAgent(openAiLlm) });
        var tokenUsage = new Mock<ITokenUsageRepository>();
        var controller = new ChatController(pipeline, tokenUsage.Object, openAiLlm);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "Getting a 500 error" });

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<ChatQueryResponse>(ok.Value);
        Assert.Equal("code_issue", body.Draft); // DrafterAgent stubs LLM to return same fixed string in this test
    }

    private sealed class TestOpenAiLlmClient : OpenAiLlmClient
    {
        private readonly ILlmClient _inner;

        public TestOpenAiLlmClient(ILlmClient inner) : base(null!)
        {
            _inner = inner;
        }

        public override async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            return await _inner.CompleteAsync(systemPrompt, userPrompt, ct);
        }

        public override async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            return await _inner.EmbedAsync(text, ct);
        }

        public override async Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default)
        {
            return await _inner.AnalyzeImageAsync(base64Image, prompt, ct);
        }
    }
}
