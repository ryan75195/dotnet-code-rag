namespace CodeRag.Core.Indexing;

public sealed record ChunkGenericTypeParameter(
    int Ordinal,
    string Name,
    string? ConstraintsJson);
