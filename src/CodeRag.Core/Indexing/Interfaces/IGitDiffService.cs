namespace CodeRag.Core.Indexing.Interfaces;

public interface IGitDiffService
{
    Task<string> GetHeadSha(string repositoryRoot, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetChangedFilesSince(string repositoryRoot, string sinceSha, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetDirtyFiles(string repositoryRoot, CancellationToken cancellationToken);
}
