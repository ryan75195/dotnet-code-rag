using System.Collections.Immutable;

namespace CodeRag.Core.Indexing;

public sealed record ReconciliationPlan(
    ImmutableArray<InsertOp> Inserts,
    ImmutableArray<UpdateOp> Updates,
    ImmutableArray<DeleteOp> Deletes);
