namespace CodeRag.Core.Indexing;

public sealed record ChunkParameter(
    int Ordinal,
    string Name,
    string TypeFullyQualifiedName,
    string? Modifier,
    bool HasDefaultValue);
