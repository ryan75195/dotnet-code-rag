using System.Collections.Immutable;
using CodeRag.Core.Indexing.Interfaces;

namespace CodeRag.Core.Indexing;

public sealed class ReconciliationService : IReconciliationService
{
    public ReconciliationPlan Plan(
        IReadOnlyList<StoredChunkSummary> existingChunks,
        ImmutableArray<CodeChunk> newChunks)
    {
        var existingByFqn = existingChunks.ToDictionary(c => c.FullyQualifiedSymbolName, c => c);
        var newByFqn = newChunks.ToDictionary(c => c.FullyQualifiedSymbolName, c => c);

        var inserts = ImmutableArray.CreateBuilder<InsertOp>();
        var updates = ImmutableArray.CreateBuilder<UpdateOp>();
        var deletes = ImmutableArray.CreateBuilder<DeleteOp>();

        foreach (var (fqn, chunk) in newByFqn)
        {
            if (!existingByFqn.TryGetValue(fqn, out var existing))
            {
                inserts.Add(new InsertOp(chunk));
                continue;
            }
            if (existing.SourceTextHash != chunk.SourceTextHash)
            {
                updates.Add(new UpdateOp(existing.ChunkId, chunk, ContentChanged: true));
            }
        }
        foreach (var (fqn, existing) in existingByFqn)
        {
            if (!newByFqn.ContainsKey(fqn))
            {
                deletes.Add(new DeleteOp(existing.ChunkId));
            }
        }

        return new ReconciliationPlan(inserts.ToImmutable(), updates.ToImmutable(), deletes.ToImmutable());
    }
}
