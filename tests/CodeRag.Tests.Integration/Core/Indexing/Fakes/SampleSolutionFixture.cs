using System.Diagnostics;

namespace CodeRag.Tests.Integration.Core.Indexing.Fakes;

public sealed class SampleSolutionFixture : IDisposable
{
    public string Root { get; }
    public string SolutionPath => Path.Combine(Root, "SampleSolution.slnx");

    public SampleSolutionFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"coderag-fixture-{Guid.NewGuid():N}");
        var src = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SampleSolution");
        CopyDirectory(src, Root);
        InitGit(Root);
    }

    public void ModifyFile(string relativePath, string newContents)
    {
        File.WriteAllText(Path.Combine(Root, relativePath), newContents);
    }

    public void Dispose()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, dest, StringComparison.Ordinal));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, dest, StringComparison.Ordinal), overwrite: true);
        }
    }

    private static void InitGit(string root)
    {
        Run(root, "init", "-b", "main");
        Run(root, "add", "-A");
        Run(root, "-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "fixture");
    }

    private static void Run(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
        }
    }
}
