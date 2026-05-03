namespace CodeRag.Core.Indexing;

public sealed record OpenAIEmbeddingOptions
{
    public const string ModelName = "text-embedding-3-large";
    public const int VectorDimensions = 3072;
    public const int MaxBatchSize = 96;
    public const int MaxConcurrentRequests = 4;
    public const int MaxRetryAttempts = 5;
}
