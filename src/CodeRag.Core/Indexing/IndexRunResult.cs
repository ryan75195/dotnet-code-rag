namespace CodeRag.Core.Indexing;

public sealed record IndexRunResult(
    int InsertedChunks,
    int UpdatedChunks,
    int DeletedChunks,
    int EmbeddedChunks,
    string IndexedAtCommitSha);
