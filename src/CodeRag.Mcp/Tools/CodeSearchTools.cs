using System.ComponentModel;
using CodeRag.Core.Indexing;
using CodeRag.Core.Indexing.Interfaces;
using ModelContextProtocol.Server;

namespace CodeRag.Mcp.Tools;

[McpServerToolType]
public static class CodeSearchTools
{
    [McpServerTool(Name = "search_code", ReadOnly = true, Idempotent = true)]
    [Description("Hybrid semantic + lexical search over the indexed codebase. Returns ranked code chunks with file path, line range, fully-qualified name, kind, and source text. Filters compose. Use this when you don't know the exact symbol name or want to find conceptually-related code.")]
    public static Task<IReadOnlyList<QueryHit>> SearchCode(
        IQueryService queryService,
        McpServerConfig config,
        [Description("Natural-language or keyword query")] string query,
        [Description("Max number of hits to return")] int topK = 10,
        [Description("Filter by symbol kind: method, class, property, field, interface, struct, enum, record")] string? kind = null,
        [Description("Filter by accessibility: public, internal, protected, private")] string? accessibility = null,
        [Description("Filter to chunks decorated with this attribute (fully-qualified name)")] string? hasAttribute = null,
        [Description("Filter to chunks implementing this interface (fully-qualified name, direct only)")] string? implements = null,
        [Description("Filter to chunks whose return type contains this substring")] string? returnTypeContains = null,
        [Description("Exclude chunks under a *.Tests* namespace (defaults true)")] bool excludeTests = true,
        [Description("Drop hits with KNN distance above this value (e.g. 0.4 for high-confidence only)")] double? maxDistance = null,
        CancellationToken cancellationToken = default)
    {
        var filters = new QueryFilters(
            SymbolKind: kind,
            ContainingProjectName: null,
            ContainingNamespace: null,
            IsAsync: null,
            Accessibility: accessibility,
            HasAttributeFullyQualifiedName: hasAttribute,
            ImplementsInterfaceFullyQualifiedName: implements,
            ReturnTypeContains: returnTypeContains,
            ExcludeTests: excludeTests,
            ExcludeNamespaceContains: null,
            MaxDistance: maxDistance);
        var request = new QueryRequest(config.IndexDatabasePath, query, topK, filters);
        return queryService.Run(request, cancellationToken);
    }
}
