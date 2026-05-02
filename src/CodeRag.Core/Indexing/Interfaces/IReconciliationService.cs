using System.Collections.Immutable;

namespace CodeRag.Core.Indexing.Interfaces;

public interface IReconciliationService
{
    ReconciliationPlan Plan(
        IReadOnlyList<StoredChunkSummary> existingChunks,
        ImmutableArray<CodeChunk> newChunks);
}
