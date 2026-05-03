using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeRag.Core.Indexing.Interfaces;

namespace CodeRag.Core.Indexing;

public sealed class VoyageEmbeddingClient : IEmbeddingClient
{
    private const string EndpointUrl = "https://api.voyageai.com/v1/embeddings";
    private const int ApproximateCharacterLimit = 30000;

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public VoyageEmbeddingClient(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
    }

    public int VectorDimensions => VoyageEmbeddingOptions.VectorDimensions;

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> Embed(IReadOnlyList<string> inputs, EmbeddingInputType inputType, CancellationToken cancellationToken)
    {
        var results = new ReadOnlyMemory<float>[inputs.Count];
        for (int batchStart = 0; batchStart < inputs.Count; batchStart += VoyageEmbeddingOptions.MaxBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int batchEnd = Math.Min(batchStart + VoyageEmbeddingOptions.MaxBatchSize, inputs.Count);
            var batchInputs = inputs.Skip(batchStart).Take(batchEnd - batchStart).Select(TruncateForModel).ToList();
            var batchVectors = await EmbedBatchWithRetry(batchInputs, inputType, cancellationToken);
            for (int i = 0; i < batchVectors.Count; i++)
            {
                results[batchStart + i] = batchVectors[i];
            }
        }
        return results;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Retry policy must catch transient failures of any type")]
    private async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchWithRetry(IReadOnlyList<string> inputs, EmbeddingInputType inputType, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < VoyageEmbeddingOptions.MaxRetryAttempts; attempt++)
        {
            try
            {
                return await EmbedBatchOnce(inputs, inputType, cancellationToken);
            }
            catch (Exception ex) when (IsRetriable(ex))
            {
                lastError = ex;
                int delaySeconds = 1 << attempt;
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
        }
        throw new InvalidOperationException($"Voyage embed failed after {VoyageEmbeddingOptions.MaxRetryAttempts} attempts.", lastError);
    }

    private async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchOnce(IReadOnlyList<string> inputs, EmbeddingInputType inputType, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(inputs, inputType);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Voyage embed returned {(int)response.StatusCode} {response.StatusCode}: {body}");
        }
        var payload = await response.Content.ReadFromJsonAsync<VoyageEmbeddingResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Voyage embed returned a null body.");
        return ProjectVectors(payload, inputs.Count);
    }

    private HttpRequestMessage BuildRequest(IReadOnlyList<string> inputs, EmbeddingInputType inputType)
    {
        var body = new VoyageEmbeddingRequest
        {
            Input = inputs,
            Model = VoyageEmbeddingOptions.ModelName,
            InputType = ResolveInputTypeString(inputType),
            OutputDimension = VoyageEmbeddingOptions.VectorDimensions,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl)
        {
            Content = JsonContent.Create(body, options: SerializerOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return request;
    }

    private static IReadOnlyList<ReadOnlyMemory<float>> ProjectVectors(VoyageEmbeddingResponse payload, int expectedCount)
    {
        if (payload.Data is null)
        {
            throw new InvalidOperationException("Voyage embed response missing 'data' field.");
        }
        if (payload.Data.Count != expectedCount)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture,
                    "Voyage returned {0} embeddings for {1} inputs.",
                    payload.Data.Count, expectedCount));
        }
        var vectors = new ReadOnlyMemory<float>[expectedCount];
        foreach (var item in payload.Data)
        {
            if (item.Embedding is null)
            {
                throw new InvalidOperationException("Voyage embed response contained a null embedding.");
            }
            if (item.Index < 0 || item.Index >= expectedCount)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.InvariantCulture, "Voyage returned embedding with out-of-range index {0}.", item.Index));
            }
            vectors[item.Index] = item.Embedding.ToArray();
        }
        return vectors;
    }

    private static string ResolveInputTypeString(EmbeddingInputType inputType) => inputType switch
    {
        EmbeddingInputType.Query => "query",
        _ => "document",
    };

    private static bool IsRetriable(Exception ex)
    {
        if (ex is HttpRequestException || ex is TaskCanceledException)
        {
            return true;
        }
        var message = ex.Message ?? string.Empty;
        return message.Contains("429", StringComparison.Ordinal)
            || message.Contains("500", StringComparison.Ordinal)
            || message.Contains("502", StringComparison.Ordinal)
            || message.Contains("503", StringComparison.Ordinal)
            || message.Contains("504", StringComparison.Ordinal)
            || message.Contains("Internal", StringComparison.OrdinalIgnoreCase);
    }

    private static string TruncateForModel(string input)
    {
        if (input.Length <= ApproximateCharacterLimit) { return input; }
        return input[..ApproximateCharacterLimit];
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record VoyageEmbeddingRequest
    {
        [JsonPropertyName("input")]
        public IReadOnlyList<string>? Input { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("input_type")]
        public string? InputType { get; init; }

        [JsonPropertyName("output_dimension")]
        public int? OutputDimension { get; init; }
    }

    private sealed record VoyageEmbeddingResponse
    {
        [JsonPropertyName("data")]
        public IReadOnlyList<VoyageEmbeddingItem>? Data { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }
    }

    private sealed record VoyageEmbeddingItem
    {
        [JsonPropertyName("embedding")]
        public IReadOnlyList<float>? Embedding { get; init; }

        [JsonPropertyName("index")]
        public int Index { get; init; }
    }
}
