using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using CodeRag.Core.Indexing.Interfaces;

namespace CodeRag.Core.Indexing;

internal sealed class IndexingService : IIndexingService
{
    private readonly IndexingDependencies _deps;

    public IndexingService(IndexingDependencies dependencies)
    {
        _deps = dependencies;
    }

    public async Task<IndexRunResult> Run(IndexRunRequest request, CancellationToken cancellationToken)
    {
        var repoRoot = ResolveRepoRoot(request.SolutionFilePath);
        var headSha = await _deps.GitDiffService.GetHeadSha(repoRoot, cancellationToken);

        using var store = _deps.StoreFactory(request.OutputDatabasePath);
        await store.OpenAsync(cancellationToken);

        var existingMeta = await store.TryGetMetadataAsync(cancellationToken);
        var strategy = await ResolveStrategy(repoRoot, existingMeta, cancellationToken);

        var loaded = await _deps.WorkspaceLoadingService.OpenSolutionAsync(request.SolutionFilePath, cancellationToken);

        await store.BeginTransactionAsync(cancellationToken);
        var counts = await ApplyPlan(store, loaded, repoRoot, strategy, cancellationToken);
        await store.SetMetadataAsync(BuildMetadata(request, repoRoot, headSha), cancellationToken);
        await store.CommitAsync(cancellationToken);

        return new IndexRunResult(counts.Inserted, counts.Updated, counts.Deleted, counts.Embedded, headSha);
    }

    private async Task<RunStrategy> ResolveStrategy(string repoRoot, IndexMetadata? existingMeta, CancellationToken cancellationToken)
    {
        if (existingMeta is null)
        {
            return new RunStrategy(FullReindex: true, FilesToProcess: Array.Empty<string>());
        }

        var changed = await _deps.GitDiffService.GetChangedFilesSince(repoRoot, existingMeta.IndexedAtCommitSha, cancellationToken);
        var dirty = await _deps.GitDiffService.GetDirtyFiles(repoRoot, cancellationToken);
        var union = changed.Concat(dirty).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (union.Any(IsSolutionFile))
        {
            return new RunStrategy(FullReindex: true, FilesToProcess: Array.Empty<string>());
        }

        return new RunStrategy(FullReindex: false, FilesToProcess: union);
    }

    private static bool IsSolutionFile(string path)
    {
        return path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Transactional rollback must catch any failure to roll back state")]
    private async Task<RunCounts> ApplyPlan(
        IIndexStore store,
        LoadedSolution loaded,
        string repoRoot,
        RunStrategy strategy,
        CancellationToken cancellationToken)
    {
        try
        {
            var plan = strategy.FullReindex
                ? await PlanFullReindex(loaded, store, repoRoot, cancellationToken)
                : await PlanIncremental(loaded, store, repoRoot, strategy.FilesToProcess, cancellationToken);

            int deleted = await ApplyDeletes(store, plan.Deletes, cancellationToken);
            var insertResult = await ApplyInserts(store, plan.Inserts, cancellationToken);
            var updateResult = await ApplyUpdates(store, plan.Updates, cancellationToken);

            var embedQueue = insertResult.EmbedQueue.Concat(updateResult.EmbedQueue).ToList();
            int embedded = await EmbedAndUpsert(store, embedQueue, cancellationToken);

            return new RunCounts(insertResult.InsertedCount, updateResult.UpdatedCount, deleted, embedded);
        }
        catch
        {
            await store.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<int> ApplyDeletes(IIndexStore store, ImmutableArray<DeleteOp> deletes, CancellationToken cancellationToken)
    {
        int count = 0;
        foreach (var op in deletes)
        {
            await store.DeleteChunkAsync(op.ChunkId, cancellationToken);
            count++;
        }
        return count;
    }

    private static async Task<InsertApplyResult> ApplyInserts(IIndexStore store, ImmutableArray<InsertOp> inserts, CancellationToken cancellationToken)
    {
        var queue = new List<EmbedQueueItem>();
        int count = 0;
        foreach (var op in inserts)
        {
            var id = await store.InsertChunkAsync(op.Chunk, cancellationToken);
            queue.Add(new EmbedQueueItem(id, op.Chunk.SourceText));
            count++;
        }
        return new InsertApplyResult(count, queue);
    }

    private static async Task<UpdateApplyResult> ApplyUpdates(IIndexStore store, ImmutableArray<UpdateOp> updates, CancellationToken cancellationToken)
    {
        var queue = new List<EmbedQueueItem>();
        int count = 0;
        foreach (var op in updates)
        {
            await store.UpdateChunkAsync(op.ChunkId, op.Chunk, cancellationToken);
            if (op.ContentChanged)
            {
                queue.Add(new EmbedQueueItem(op.ChunkId, op.Chunk.SourceText));
            }
            count++;
        }
        return new UpdateApplyResult(count, queue);
    }

    private async Task<int> EmbedAndUpsert(IIndexStore store, IReadOnlyList<EmbedQueueItem> embedQueue, CancellationToken cancellationToken)
    {
        if (embedQueue.Count == 0)
        {
            return 0;
        }
        var inputs = embedQueue.Select(q => q.SourceText).ToList();
        var vectors = await _deps.EmbeddingClient.Embed(inputs, cancellationToken);
        for (int i = 0; i < embedQueue.Count; i++)
        {
            await store.UpsertEmbeddingAsync(embedQueue[i].ChunkId, vectors[i], cancellationToken);
        }
        return embedQueue.Count;
    }

    private IndexMetadata BuildMetadata(IndexRunRequest request, string repoRoot, string headSha)
    {
        return new IndexMetadata(
            SchemaVersion: 1,
            SolutionFilePath: request.SolutionFilePath,
            RepositoryRootPath: repoRoot,
            IndexedAtCommitSha: headSha,
            IndexedAtUtc: _deps.Time.GetUtcNow(),
            EmbeddingModelName: EmbeddingOptions.ModelName,
            EmbeddingVectorDimensions: EmbeddingOptions.VectorDimensions);
    }

    private async Task<ReconciliationPlan> PlanFullReindex(
        LoadedSolution loaded, IIndexStore store, string repoRoot, CancellationToken ct)
    {
        var inserts = ImmutableArray.CreateBuilder<InsertOp>();
        var deletes = ImmutableArray.CreateBuilder<DeleteOp>();

        var allFiles = loaded.Projects
            .SelectMany(p => p.Documents)
            .Select(d => d.FilePath)
            .Where(p => p is not null)
            .Cast<string>()
            .Distinct()
            .ToList();

        foreach (var path in allFiles)
        {
            var rel = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
            var summaries = await store.GetChunkSummariesForFileAsync(rel, ct);
            foreach (var s in summaries)
            {
                deletes.Add(new DeleteOp(s.ChunkId));
            }
        }

        foreach (var project in loaded.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct)
                ?? throw new InvalidOperationException($"Could not compile {project.Name}.");
            foreach (var tree in compilation.SyntaxTrees)
            {
                var chunks = _deps.ChunkExtractor.Extract(compilation, tree, project.Name, compilation.AssemblyName ?? project.Name, repoRoot, ct);
                foreach (var c in chunks)
                {
                    inserts.Add(new InsertOp(c));
                }
            }
        }

        return new ReconciliationPlan(inserts.ToImmutable(), ImmutableArray<UpdateOp>.Empty, deletes.ToImmutable());
    }

    private async Task<ReconciliationPlan> PlanIncremental(
        LoadedSolution loaded, IIndexStore store, string repoRoot, IReadOnlyList<string> changedFiles, CancellationToken ct)
    {
        var inserts = ImmutableArray.CreateBuilder<InsertOp>();
        var updates = ImmutableArray.CreateBuilder<UpdateOp>();
        var deletes = ImmutableArray.CreateBuilder<DeleteOp>();

        foreach (var changedFile in changedFiles)
        {
            await ProcessIncrementalFile(loaded, store, repoRoot, changedFile, inserts, updates, deletes, ct);
        }

        return new ReconciliationPlan(inserts.ToImmutable(), updates.ToImmutable(), deletes.ToImmutable());
    }

    private async Task ProcessIncrementalFile(
        LoadedSolution loaded,
        IIndexStore store,
        string repoRoot,
        string changedFile,
        ImmutableArray<InsertOp>.Builder inserts,
        ImmutableArray<UpdateOp>.Builder updates,
        ImmutableArray<DeleteOp>.Builder deletes,
        CancellationToken ct)
    {
        var absolute = Path.GetFullPath(Path.Combine(repoRoot, changedFile));
        if (!File.Exists(absolute))
        {
            var existingForDeletedFile = await store.GetChunkSummariesForFileAsync(changedFile, ct);
            foreach (var summary in existingForDeletedFile)
            {
                deletes.Add(new DeleteOp(summary.ChunkId));
            }
            return;
        }

        if (changedFile.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            await ProcessProjectFile(loaded, store, repoRoot, absolute, inserts, updates, deletes, ct);
            return;
        }

        await ProcessDocumentFile(loaded, store, repoRoot, absolute, changedFile, inserts, updates, deletes, ct);
    }

    private async Task ProcessProjectFile(
        LoadedSolution loaded,
        IIndexStore store,
        string repoRoot,
        string absoluteProjectPath,
        ImmutableArray<InsertOp>.Builder inserts,
        ImmutableArray<UpdateOp>.Builder updates,
        ImmutableArray<DeleteOp>.Builder deletes,
        CancellationToken ct)
    {
        var project = loaded.Projects.FirstOrDefault(p => string.Equals(p.FilePath, absoluteProjectPath, StringComparison.OrdinalIgnoreCase));
        if (project is null) { return; }
        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null) { return; }
        foreach (var tree in compilation.SyntaxTrees)
        {
            var chunks = _deps.ChunkExtractor.Extract(compilation, tree, project.Name, compilation.AssemblyName ?? project.Name, repoRoot, ct);
            var rel = Path.GetRelativePath(repoRoot, tree.FilePath).Replace('\\', '/');
            var existing = await store.GetChunkSummariesForFileAsync(rel, ct);
            var plan = _deps.ReconciliationService.Plan(existing, chunks);
            inserts.AddRange(plan.Inserts);
            updates.AddRange(plan.Updates);
            deletes.AddRange(plan.Deletes);
        }
    }

    private async Task ProcessDocumentFile(
        LoadedSolution loaded,
        IIndexStore store,
        string repoRoot,
        string absolutePath,
        string changedFile,
        ImmutableArray<InsertOp>.Builder inserts,
        ImmutableArray<UpdateOp>.Builder updates,
        ImmutableArray<DeleteOp>.Builder deletes,
        CancellationToken ct)
    {
        var doc = loaded.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, absolutePath, StringComparison.OrdinalIgnoreCase));
        if (doc is null) { return; }
        var compilation = await doc.Project.GetCompilationAsync(ct);
        if (compilation is null) { return; }
        var tree = await doc.GetSyntaxTreeAsync(ct);
        if (tree is null) { return; }
        var chunks = _deps.ChunkExtractor.Extract(compilation, tree, doc.Project.Name, compilation.AssemblyName ?? doc.Project.Name, repoRoot, ct);
        var existing = await store.GetChunkSummariesForFileAsync(changedFile, ct);
        var plan = _deps.ReconciliationService.Plan(existing, chunks);
        inserts.AddRange(plan.Inserts);
        updates.AddRange(plan.Updates);
        deletes.AddRange(plan.Deletes);
    }

    private static string ResolveRepoRoot(string solutionFilePath)
    {
        var current = Path.GetDirectoryName(Path.GetFullPath(solutionFilePath));
        while (current is not null)
        {
            var gitPath = Path.Combine(current, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return current;
            }
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException($"Could not locate .git ancestor of {solutionFilePath}.");
    }

    private sealed record RunStrategy(bool FullReindex, IReadOnlyList<string> FilesToProcess);

    private sealed record RunCounts(int Inserted, int Updated, int Deleted, int Embedded);

    private sealed record EmbedQueueItem(long ChunkId, string SourceText);

    private sealed record InsertApplyResult(int InsertedCount, IReadOnlyList<EmbedQueueItem> EmbedQueue);

    private sealed record UpdateApplyResult(int UpdatedCount, IReadOnlyList<EmbedQueueItem> EmbedQueue);
}
