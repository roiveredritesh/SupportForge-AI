using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

public class ProjectsControllerTests
{
    [Fact]
    public async Task CreateProject_Then_ListProjects_ReturnsCreatedProject()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileProjectRepository(tempDir);
        var controller = new ProjectsController(repo);

        var project = new Project { Id = "proj1", Name = "Test Project" };
        await controller.CreateOrUpdate(project);

        var result = await controller.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var projects = Assert.IsAssignableFrom<IReadOnlyList<Project>>(ok.Value);

        Assert.Single(projects);
        Assert.Equal("Test Project", projects[0].Name);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task PostProjects_WithStringKbSourceTypeEnum_ReturnsOk()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/api/projects", JsonContent.Create(new
        {
            Id = "proj-with-kb",
            Name = "Project With KB",
            KbSources = new[] { new { Type = "Documents", Location = "docs/", LastSyncedAt = (DateTimeOffset?)null } },
        }));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200 but got {(int)response.StatusCode}: {body}");
    }
}
