namespace CodeRag.Core.Indexing;

public sealed record VoyageEmbeddingOptions
{
    public const string ModelName = "voyage-code-3";
    public const int VectorDimensions = 1024;
    public const int MaxBatchSize = 96;
    public const int MaxRetryAttempts = 5;
}
