using CodeRag.Core.Indexing;
using CodeRag.Core.Indexing.Interfaces;

namespace CodeRag.Cli.Commands;

internal sealed class IndexCommand
{
    private readonly IIndexingService _indexingService;

    public IndexCommand(IIndexingService indexingService)
    {
        _indexingService = indexingService;
    }

    public async Task<int> ExecuteAsync(string solutionPath, string? outputDir, CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("OPENAI_API_KEY") is null)
        {
            await Console.Error.WriteLineAsync("OPENAI_API_KEY is not set.");
            return 2;
        }
        if (!File.Exists(solutionPath))
        {
            await Console.Error.WriteLineAsync($"Solution not found: {solutionPath}");
            return 2;
        }
        var resolvedOut = outputDir ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solutionPath))!, ".coderag");
        Directory.CreateDirectory(resolvedOut);
        var dbPath = Path.Combine(resolvedOut, "index.db");

        var request = new IndexRunRequest(solutionPath, dbPath);
        var result = await _indexingService.Run(request, cancellationToken);

        Console.WriteLine($"Indexed at commit {result.IndexedAtCommitSha[..8]}: " +
                          $"+{result.InsertedChunks} ~{result.UpdatedChunks} -{result.DeletedChunks}, " +
                          $"{result.EmbeddedChunks} embeddings.");
        Console.WriteLine($"Wrote: {dbPath}");
        return 0;
    }
}
