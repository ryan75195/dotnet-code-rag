namespace CodeRag.Core.Indexing;

public sealed record IndexMetadata(
    int SchemaVersion,
    string SolutionFilePath,
    string RepositoryRootPath,
    string IndexedAtCommitSha,
    DateTimeOffset IndexedAtUtc,
    string EmbeddingModelName,
    int EmbeddingVectorDimensions);
