using System.Globalization;
using System.Text.Json;
using CodeRag.Core.Indexing;
using CodeRag.Core.Indexing.Interfaces;

namespace CodeRag.Cli.Commands;

internal sealed class QueryCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IQueryService _queryService;

    public QueryCommand(IQueryService queryService)
    {
        _queryService = queryService;
    }

    public async Task<int> ExecuteAsync(
        string text,
        string? dbPath,
        int topK,
        string? kind,
        string? project,
        string? containingNamespace,
        bool? isAsync,
        string format,
        CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("OPENAI_API_KEY") is null)
        {
            await Console.Error.WriteLineAsync("OPENAI_API_KEY is not set.");
            return 2;
        }

        var resolvedDb = dbPath ?? Path.Combine(Directory.GetCurrentDirectory(), ".coderag", "index.db");
        if (!File.Exists(resolvedDb))
        {
            await Console.Error.WriteLineAsync($"Index not found: {resolvedDb}");
            return 2;
        }

        var filters = new QueryFilters(kind, project, containingNamespace, isAsync);
        var request = new QueryRequest(resolvedDb, text, topK, filters);
        var hits = await _queryService.Run(request, cancellationToken);

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(hits);
        }
        else
        {
            WriteText(hits);
        }
        return 0;
    }

    private static void WriteJson(IReadOnlyList<QueryHit> hits)
    {
        var json = JsonSerializer.Serialize(hits, JsonOptions);
        Console.WriteLine(json);
    }

    private static void WriteText(IReadOnlyList<QueryHit> hits)
    {
        foreach (var hit in hits)
        {
            var header = string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1}-{2}  {3}  ({4}, dist={5:F4})",
                hit.RelativeFilePath,
                hit.LineStart,
                hit.LineEnd,
                hit.FullyQualifiedSymbolName,
                hit.SymbolKind,
                hit.Distance);
            Console.WriteLine(header);
            Console.WriteLine(hit.SourceText);
            Console.WriteLine(new string('-', 60));
        }
    }
}
