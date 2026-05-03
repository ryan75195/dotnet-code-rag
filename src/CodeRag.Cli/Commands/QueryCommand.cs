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

    public async Task<int> ExecuteAsync(QueryCommandOptions options, CancellationToken cancellationToken)
    {
        if (!IsAnyEmbeddingKeyConfigured())
        {
            await Console.Error.WriteLineAsync("Neither VOYAGE_API_KEY nor OPENAI_API_KEY is set.");
            return 2;
        }

        var resolvedDb = options.DbPath ?? Path.Combine(Directory.GetCurrentDirectory(), ".coderag", "index.db");
        if (!File.Exists(resolvedDb))
        {
            await Console.Error.WriteLineAsync($"Index not found: {resolvedDb}");
            return 2;
        }

        var filters = BuildFilters(options);
        var request = new QueryRequest(resolvedDb, options.Text, options.TopK, filters);
        var hits = await _queryService.Run(request, cancellationToken);

        if (string.Equals(options.Format, "json", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(hits);
        }
        else
        {
            WriteText(hits);
        }
        return 0;
    }

    private static bool IsAnyEmbeddingKeyConfigured()
    {
        return Environment.GetEnvironmentVariable("VOYAGE_API_KEY") is not null
            || Environment.GetEnvironmentVariable("OPENAI_API_KEY") is not null;
    }

    private static QueryFilters BuildFilters(QueryCommandOptions options)
    {
        return new QueryFilters(
            SymbolKind: options.SymbolKind,
            ContainingProjectName: options.Project,
            ContainingNamespace: options.ContainingNamespace,
            IsAsync: options.IsAsync,
            Accessibility: options.Accessibility,
            HasAttributeFullyQualifiedName: options.HasAttribute,
            ImplementsInterfaceFullyQualifiedName: options.Implements,
            ReturnTypeContains: options.ReturnTypeContains,
            ExcludeTests: options.ExcludeTests,
            ExcludeNamespaceContains: options.ExcludeNamespace,
            MaxDistance: options.MaxDistance);
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
                "{0}:{1}-{2}  {3}  ({4}, score={5:F4}, dist={6:F4})",
                hit.RelativeFilePath,
                hit.LineStart,
                hit.LineEnd,
                hit.FullyQualifiedSymbolName,
                hit.SymbolKind,
                hit.FusedScore,
                hit.Distance);
            Console.WriteLine(header);
            Console.WriteLine(hit.SourceText);
            Console.WriteLine(new string('-', 60));
        }
    }
}
