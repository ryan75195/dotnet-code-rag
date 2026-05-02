using System.Collections.ObjectModel;
using CodeRag.Core.Indexing.Interfaces;

namespace CodeRag.Tests.Integration.Core.Indexing.Fakes;

#pragma warning disable CA1721
public sealed class StubGitDiffService : IGitDiffService
{
    public string HeadSha { get; set; } = "stubbedheadsha000000000000000000000000000000";
    public Collection<string> ChangedFiles { get; } = new();
    public Collection<string> DirtyFiles { get; } = new();

    public Task<string> GetHeadSha(string repositoryRoot, CancellationToken cancellationToken)
        => Task.FromResult(HeadSha);

    public Task<IReadOnlyList<string>> GetChangedFilesSince(string repositoryRoot, string sinceSha, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>(ChangedFiles.ToList());

    public Task<IReadOnlyList<string>> GetDirtyFiles(string repositoryRoot, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>(DirtyFiles.ToList());
}
#pragma warning restore CA1721
