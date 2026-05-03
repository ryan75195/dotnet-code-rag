namespace CodeRag.Core.Indexing;

public sealed record QueryFilters(
    string? SymbolKind,
    string? ContainingProjectName,
    string? ContainingNamespace,
    bool? IsAsync,
    string? Accessibility,
    string? HasAttributeFullyQualifiedName,
    string? ImplementsInterfaceFullyQualifiedName,
    string? ReturnTypeContains,
    bool ExcludeTests,
    string? ExcludeNamespaceContains,
    double? MaxDistance);
