namespace CodeRag.Core.Indexing;

public sealed record ChunkAttribute(
    string AttributeFullyQualifiedName,
    string? AttributeArgumentsJson);
