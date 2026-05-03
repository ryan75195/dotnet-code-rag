namespace CodeRag.Core.Indexing;

public sealed record SymbolHit(
    long ChunkId,
    string RelativeFilePath,
    int LineStart,
    int LineEnd,
    string FullyQualifiedSymbolName,
    string SymbolDisplayName,
    string SymbolKind,
    string Accessibility,
    string SymbolSignatureDisplay,
    string? ContainingNamespace,
    string? ParentSymbolFullyQualifiedName,
    string? XmlDocSummary);
