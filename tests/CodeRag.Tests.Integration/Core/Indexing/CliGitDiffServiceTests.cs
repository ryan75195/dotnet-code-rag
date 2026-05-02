using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Integration.Core.Indexing;

[TestFixture]
public class CliGitDiffServiceTests
{
    [Test]
    public async Task Should_return_a_40_char_head_sha()
    {
        var root = ResolveRepoRoot();
        var service = new CliGitDiffService();

        var sha = await service.GetHeadSha(root, CancellationToken.None);

        sha.Should().HaveLength(40);
        sha.Should().MatchRegex("^[0-9a-f]{40}$");
    }

    [Test]
    public async Task Should_return_empty_diff_when_comparing_head_to_itself()
    {
        var root = ResolveRepoRoot();
        var service = new CliGitDiffService();
        var head = await service.GetHeadSha(root, CancellationToken.None);

        var changed = await service.GetChangedFilesSince(root, head, CancellationToken.None);

        changed.Should().BeEmpty();
    }

    private static string ResolveRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null)
        {
            var gitPath = Path.Combine(current, ".git");
            if (File.Exists(gitPath) || Directory.Exists(gitPath)) { return current; }
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException(".git not found.");
    }
}
