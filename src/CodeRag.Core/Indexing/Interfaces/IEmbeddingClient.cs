namespace CodeRag.Core.Indexing.Interfaces;

public interface IEmbeddingClient
{
    int VectorDimensions { get; }
    Task<IReadOnlyList<ReadOnlyMemory<float>>> Embed(IReadOnlyList<string> inputs, EmbeddingInputType inputType, CancellationToken cancellationToken);
}
