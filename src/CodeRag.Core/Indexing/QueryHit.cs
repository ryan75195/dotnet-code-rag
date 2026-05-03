namespace CodeRag.Core.Indexing;

public sealed record QueryHit(
    long ChunkId,
    string RelativeFilePath,
    int LineStart,
    int LineEnd,
    string FullyQualifiedSymbolName,
    string SymbolKind,
    double Distance,
    double FusedScore,
    string SourceText);
