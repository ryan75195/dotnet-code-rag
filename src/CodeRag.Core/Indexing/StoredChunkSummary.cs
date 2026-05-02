namespace CodeRag.Core.Indexing;

public sealed record StoredChunkSummary(
    long ChunkId,
    string FullyQualifiedSymbolName,
    string SourceTextHash);
