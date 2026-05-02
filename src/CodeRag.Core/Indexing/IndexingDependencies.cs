using CodeRag.Core.Indexing.Interfaces;

namespace CodeRag.Core.Indexing;

internal sealed record IndexingDependencies(
    IWorkspaceLoadingService WorkspaceLoadingService,
    IChunkExtractor ChunkExtractor,
    IGitDiffService GitDiffService,
    IEmbeddingClient EmbeddingClient,
    Func<string, IIndexStore> StoreFactory,
    IReconciliationService ReconciliationService,
    TimeProvider Time);
