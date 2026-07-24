using Microsoft.AspNetCore.Mvc;
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
}
