using System.Diagnostics.CodeAnalysis;
using CodeRag.Core.Indexing.Interfaces;
using OpenAI.Embeddings;

namespace CodeRag.Core.Indexing;

public sealed class OpenAIEmbeddingClient : IEmbeddingClient
{
    private readonly EmbeddingClient _client;

    public OpenAIEmbeddingClient(string apiKey)
    {
        _client = new EmbeddingClient(model: EmbeddingOptions.ModelName, apiKey: apiKey);
    }

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> Embed(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        var results = new ReadOnlyMemory<float>[inputs.Count];
        for (int batchStart = 0; batchStart < inputs.Count; batchStart += EmbeddingOptions.MaxBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int batchEnd = Math.Min(batchStart + EmbeddingOptions.MaxBatchSize, inputs.Count);
            var batchInputs = inputs.Skip(batchStart).Take(batchEnd - batchStart).Select(TruncateForModel).ToList();
            var batchVectors = await EmbedBatchWithRetry(batchInputs, cancellationToken);
            for (int i = 0; i < batchVectors.Count; i++)
            {
                results[batchStart + i] = batchVectors[i];
            }
        }
        return results;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Retry policy must catch transient failures of any type")]
    private async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchWithRetry(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < EmbeddingOptions.MaxRetryAttempts; attempt++)
        {
            try
            {
                var response = await _client.GenerateEmbeddingsAsync(inputs, cancellationToken: cancellationToken);
                return response.Value.Select(e => (ReadOnlyMemory<float>)e.ToFloats().ToArray()).ToList();
            }
            catch (Exception ex) when (IsRetriable(ex))
            {
                lastError = ex;
                int delaySeconds = 1 << attempt;
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
        }
        throw new InvalidOperationException($"OpenAI embed failed after {EmbeddingOptions.MaxRetryAttempts} attempts.", lastError);
    }

    private static bool IsRetriable(Exception ex)
    {
        return ex is HttpRequestException
            || ex is TaskCanceledException
            || ex.Message.Contains("429", StringComparison.Ordinal)
            || ex.Message.Contains("Internal", StringComparison.OrdinalIgnoreCase);
    }

    private static string TruncateForModel(string input)
    {
        const int approxCharLimit = 30000;
        if (input.Length <= approxCharLimit) { return input; }
        return input[..approxCharLimit];
    }
}
