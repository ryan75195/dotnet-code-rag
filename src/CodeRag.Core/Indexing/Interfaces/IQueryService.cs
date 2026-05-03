namespace CodeRag.Core.Indexing.Interfaces;

public interface IQueryService
{
    Task<IReadOnlyList<QueryHit>> Run(QueryRequest request, CancellationToken cancellationToken);
}
