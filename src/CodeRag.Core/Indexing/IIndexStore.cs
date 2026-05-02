namespace CodeRag.Core.Indexing;

internal interface IIndexStore : IDisposable
{
    Task OpenAsync(CancellationToken cancellationToken);
    Task<IndexMetadata?> TryGetMetadataAsync(CancellationToken cancellationToken);
    Task SetMetadataAsync(IndexMetadata metadata, CancellationToken cancellationToken);
}
