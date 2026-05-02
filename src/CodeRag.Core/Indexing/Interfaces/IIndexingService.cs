namespace CodeRag.Core.Indexing.Interfaces;

public interface IIndexingService
{
    Task<IndexRunResult> Run(IndexRunRequest request, CancellationToken cancellationToken);
}
