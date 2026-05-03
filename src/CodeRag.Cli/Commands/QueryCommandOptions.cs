namespace CodeRag.Cli.Commands;

internal sealed record QueryCommandOptions(
    string Text,
    string? DbPath,
    int TopK,
    string? SymbolKind,
    string? Project,
    string? ContainingNamespace,
    bool? IsAsync,
    string? Accessibility,
    string? HasAttribute,
    string? Implements,
    string? ReturnTypeContains,
    bool ExcludeTests,
    string? ExcludeNamespace,
    double? MaxDistance,
    string Format);
