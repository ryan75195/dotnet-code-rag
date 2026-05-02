namespace CodeRag.Core.Indexing.Interfaces;

public interface IWorkspaceLoadingService : IAsyncDisposable
{
    Task<LoadedSolution> OpenSolutionAsync(string solutionFilePath, CancellationToken cancellationToken);
}
