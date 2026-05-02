using System.Security.Cryptography;
using System.Text;
using CodeRag.Core.Indexing.Interfaces;

namespace CodeRag.Tests.Integration.Core.Indexing.Fakes;

public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    public int CallCount { get; private set; }
    public int InputCount { get; private set; }

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> Embed(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        CallCount++;
        InputCount += inputs.Count;
        var results = inputs.Select(input => (ReadOnlyMemory<float>)SeededVectorFor(input)).ToList();
        return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(results);
    }

    private static float[] SeededVectorFor(string input)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(input));
#pragma warning disable CA5394
        var rng = new Random(BitConverter.ToInt32(seed, 0));
#pragma warning restore CA5394
        var v = new float[3072];
        for (int i = 0; i < v.Length; i++)
        {
#pragma warning disable CA5394
            v[i] = (float)(rng.NextDouble() - 0.5);
#pragma warning restore CA5394
        }
        return v;
    }
}
