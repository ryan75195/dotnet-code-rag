using CodeRag.Core.Indexing.Interfaces;

namespace CodeRag.Core.Indexing;

internal sealed class QueryService : IQueryService
{
    private readonly IEmbeddingClient _embeddingClient;
    private readonly Func<string, IIndexStore> _storeFactory;

    public QueryService(IEmbeddingClient embeddingClient, Func<string, IIndexStore> storeFactory)
    {
        _embeddingClient = embeddingClient;
        _storeFactory = storeFactory;
    }

    public async Task<IReadOnlyList<QueryHit>> Run(QueryRequest request, CancellationToken cancellationToken)
    {
        var inputs = new[] { request.Text };
        var vectors = await _embeddingClient.Embed(inputs, cancellationToken);
        var queryVector = vectors[0];

        using var store = _storeFactory(request.IndexDatabasePath);
        await store.OpenAsync(cancellationToken);
        return await store.Search(queryVector, request.Filters, request.TopK, cancellationToken);
    }
}
