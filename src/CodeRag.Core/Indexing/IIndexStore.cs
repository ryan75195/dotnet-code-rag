namespace CodeRag.Core.Indexing;

internal interface IIndexStore : IDisposable
{
    Task OpenAsync(CancellationToken cancellationToken);
    Task<IndexMetadata?> TryGetMetadataAsync(CancellationToken cancellationToken);
    Task SetMetadataAsync(IndexMetadata metadata, CancellationToken cancellationToken);

    Task BeginTransactionAsync(CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);

    Task<long> InsertChunkAsync(CodeChunk chunk, CancellationToken cancellationToken);
    Task UpdateChunkAsync(long chunkId, CodeChunk chunk, CancellationToken cancellationToken);
    Task UpsertEmbeddingAsync(long chunkId, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken);
    Task UpsertFtsContentAsync(long chunkId, string content, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredChunkSummary>> GetChunkSummariesForFileAsync(string relativeFilePath, CancellationToken cancellationToken);
    Task DeleteChunkAsync(long chunkId, CancellationToken cancellationToken);
    Task DeleteChunksForFileAsync(string relativeFilePath, CancellationToken cancellationToken);

    Task<IReadOnlyList<QueryHit>> Search(
        ReadOnlyMemory<float> queryVector,
        string queryText,
        QueryFilters filters,
        int topK,
        CancellationToken cancellationToken);
}
