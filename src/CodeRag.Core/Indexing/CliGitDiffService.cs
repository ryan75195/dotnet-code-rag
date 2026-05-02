using System.Diagnostics;
using CodeRag.Core.Indexing.Interfaces;

namespace CodeRag.Core.Indexing;

public sealed class CliGitDiffService : IGitDiffService
{
    public async Task<string> GetHeadSha(string repositoryRoot, CancellationToken cancellationToken)
    {
        var output = await RunGit(repositoryRoot, ["rev-parse", "HEAD"], cancellationToken);
        return output.Trim();
    }

    public async Task<IReadOnlyList<string>> GetChangedFilesSince(string repositoryRoot, string sinceSha, CancellationToken cancellationToken)
    {
        var output = await RunGit(repositoryRoot,
            ["diff", "--name-only", sinceSha, "HEAD", "--", "*.cs", "*.csproj", "*.sln", "*.slnx"],
            cancellationToken);
        return SplitLines(output);
    }

    public async Task<IReadOnlyList<string>> GetDirtyFiles(string repositoryRoot, CancellationToken cancellationToken)
    {
        var output = await RunGit(repositoryRoot, ["status", "--porcelain"], cancellationToken);
        var paths = new List<string>();
        foreach (var line in SplitLines(output))
        {
            if (line.Length < 4) { continue; }
            var path = line[3..].Trim();
            if (HasIndexableExtension(path))
            {
                paths.Add(path);
            }
        }
        return paths;
    }

    private static bool HasIndexableExtension(string path)
    {
        return path.EndsWith(".cs", StringComparison.Ordinal)
            || path.EndsWith(".csproj", StringComparison.Ordinal)
            || path.EndsWith(".sln", StringComparison.Ordinal)
            || path.EndsWith(".slnx", StringComparison.Ordinal);
    }

    private static async Task<string> RunGit(string workingDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
        }
        return stdout;
    }

    private static IReadOnlyList<string> SplitLines(string output)
    {
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
