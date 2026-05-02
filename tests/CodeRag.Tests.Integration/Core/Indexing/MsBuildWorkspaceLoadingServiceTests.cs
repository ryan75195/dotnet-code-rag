using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Integration.Core.Indexing;

[TestFixture]
public class MsBuildWorkspaceLoadingServiceTests
{
    [Test]
    public async Task Should_open_coderag_solution_and_enumerate_projects()
    {
        var slnPath = ResolveCodeRagSolutionPath();

        await using var loader = new MsBuildWorkspaceLoadingService();
        var loaded = await loader.OpenSolutionAsync(slnPath, CancellationToken.None);

        loaded.Projects.Should().Contain(p => p.Name == "CodeRag.Core");
        loaded.Projects.Should().Contain(p => p.Name == "CodeRag.Cli");
    }

    private static string ResolveCodeRagSolutionPath()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null)
        {
            var slnx = Path.Combine(current, "CodeRag.slnx");
            if (File.Exists(slnx)) { return slnx; }
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("CodeRag.slnx not found.");
    }
}
