namespace CodeRag.Core.Indexing;

public sealed record QueryFilters(
    string? SymbolKind,
    string? ContainingProjectName,
    string? ContainingNamespace,
    bool? IsAsync);
