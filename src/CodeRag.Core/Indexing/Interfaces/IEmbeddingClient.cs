namespace CodeRag.Core.Indexing.Interfaces;

public interface IEmbeddingClient
{
    Task<IReadOnlyList<ReadOnlyMemory<float>>> Embed(IReadOnlyList<string> inputs, CancellationToken cancellationToken);
}
