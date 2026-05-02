using CodeRag.Core.Indexing.Interfaces;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeRag.Core.Indexing;

public sealed class MsBuildWorkspaceLoadingService : IWorkspaceLoadingService
{
    private static readonly object LocatorLock = new();
    private static bool _locatorRegistered;
    private MSBuildWorkspace? _workspace;

    public async Task<LoadedSolution> OpenSolutionAsync(string solutionFilePath, CancellationToken cancellationToken)
    {
        EnsureLocatorRegistered();
        DisposePreviousWorkspace();
        _workspace = MSBuildWorkspace.Create();
        var solution = await _workspace.OpenSolutionAsync(solutionFilePath, cancellationToken: cancellationToken);
        return new LoadedSolution(solution, solution.Projects.ToList());
    }

    public ValueTask DisposeAsync()
    {
        DisposePreviousWorkspace();
        return ValueTask.CompletedTask;
    }

    private void DisposePreviousWorkspace()
    {
        _workspace?.Dispose();
        _workspace = null;
    }

    private static void EnsureLocatorRegistered()
    {
        lock (LocatorLock)
        {
            if (_locatorRegistered) { return; }
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
            _locatorRegistered = true;
        }
    }
}
