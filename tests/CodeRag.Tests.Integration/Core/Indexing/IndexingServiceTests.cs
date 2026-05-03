using CodeRag.Core.Indexing;
using CodeRag.Tests.Integration.Core.Indexing.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace CodeRag.Tests.Integration.Core.Indexing;

[TestFixture]
public class IndexingServiceTests
{
    private SampleSolutionFixture _fixture = null!;
    private FakeEmbeddingClient _embedding = null!;
    private StubGitDiffService _git = null!;
    private MsBuildWorkspaceLoadingService _workspaceLoadingService = null!;
    private string _dbPath = null!;
    private IndexingService _indexingService = null!;

    [SetUp]
    public void SetUp()
    {
        _fixture = new SampleSolutionFixture();
        _embedding = new FakeEmbeddingClient();
        _git = new StubGitDiffService();
        _dbPath = Path.Combine(_fixture.Root, ".coderag", "index.db");

        _workspaceLoadingService = new MsBuildWorkspaceLoadingService();
        var hashingService = new SourceTextHashingService();
        var extractor = new ChunkExtractor(hashingService);
        var reconciliationService = new ReconciliationService();
        var embeddingDimension = _embedding.VectorDimensions;
        Func<string, IIndexStore> storeFactory = path => new SqliteIndexStore(path, embeddingDimension);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero));

        var deps = new IndexingDependencies(
            _workspaceLoadingService,
            extractor,
            _git,
            _embedding,
            storeFactory,
            reconciliationService,
            clock);

        _indexingService = new IndexingService(deps);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _workspaceLoadingService.DisposeAsync();
        _fixture.Dispose();
    }

    private Task<IndexRunResult> RunAsync()
        => _indexingService.Run(new IndexRunRequest(_fixture.SolutionPath, _dbPath), CancellationToken.None);

    [Test]
    public async Task Should_populate_chunks_and_metadata_on_cold_index()
    {
        var result = await RunAsync();

        result.InsertedChunks.Should().BeGreaterThan(0);
        result.UpdatedChunks.Should().Be(0);
        result.DeletedChunks.Should().Be(0);

        using var verify = new SqliteIndexStore(_dbPath, _embedding.VectorDimensions);
        await verify.OpenAsync(CancellationToken.None);
        var meta = await verify.TryGetMetadataAsync(CancellationToken.None);
        meta.Should().NotBeNull();
        meta!.IndexedAtCommitSha.Should().Be(_git.HeadSha);
        meta.EmbeddingModelName.Should().Be(VoyageEmbeddingOptions.ModelName);
        meta.EmbeddingVectorDimensions.Should().Be(_embedding.VectorDimensions);
    }

    [Test]
    public async Task Should_make_zero_embedding_calls_on_no_op_rerun()
    {
        await RunAsync();
        _embedding.Reset();

        var result = await RunAsync();

        result.InsertedChunks.Should().Be(0);
        result.UpdatedChunks.Should().Be(0);
        result.DeletedChunks.Should().Be(0);
        _embedding.CallCount.Should().Be(0);
    }

    [Test]
    public async Task Should_update_one_chunk_and_one_embedding_when_a_method_is_edited()
    {
        await RunAsync();
        _embedding.Reset();
        _git.HeadSha = "newhead00000000000000000000000000000000a";
        _git.ChangedFiles.Add("Sample.Lib/UserService.cs");

        var path = Path.Combine(_fixture.Root, "Sample.Lib", "UserService.cs");
        var current = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, current.Replace("await Task.Yield();", "await Task.Delay(1);", StringComparison.Ordinal));

        var result = await RunAsync();

        result.UpdatedChunks.Should().Be(1);
        _embedding.InputCount.Should().Be(1);
    }

    [Test]
    public async Task Should_insert_chunks_when_a_new_class_is_added()
    {
        await RunAsync();
        _embedding.Reset();
        _git.HeadSha = "newhead10000000000000000000000000000000b";
        _git.ChangedFiles.Add("Sample.Lib/AuditLog.cs");
        _fixture.ModifyFile(
            "Sample.Lib/AuditLog.cs",
            "namespace Sample.Lib; public sealed class AuditLog { public void Write(string s) { } }");

        var result = await RunAsync();

        result.InsertedChunks.Should().BeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Should_remove_all_chunks_when_a_file_is_deleted()
    {
        await RunAsync();
        var relativePath = "Sample.Lib/User.cs";
        File.Delete(Path.Combine(_fixture.Root, relativePath));
        _git.HeadSha = "newhead20000000000000000000000000000000c";
        _git.ChangedFiles.Add(relativePath);

        var result = await RunAsync();

        result.DeletedChunks.Should().BeGreaterThan(0);

        using var verify = new SqliteIndexStore(_dbPath, _embedding.VectorDimensions);
        await verify.OpenAsync(CancellationToken.None);
        IIndexStore verifyStore = verify;
        var summaries = await verifyStore.GetChunkSummariesForFileAsync(relativePath, CancellationToken.None);
        summaries.Should().BeEmpty();
    }

    [Test]
    public async Task Should_reparse_whole_project_when_csproj_changes()
    {
        await RunAsync();
        _embedding.Reset();
        _git.HeadSha = "newhead30000000000000000000000000000000d";
        _git.ChangedFiles.Add("Sample.Lib/Sample.Lib.csproj");

        var result = await RunAsync();

        result.InsertedChunks.Should().Be(0);
        result.UpdatedChunks.Should().Be(0);
        result.DeletedChunks.Should().Be(0);
    }

    [Test]
    public async Task Should_force_full_reindex_when_solution_file_changes()
    {
        await RunAsync();
        _embedding.Reset();
        _git.HeadSha = "newhead40000000000000000000000000000000e";
        _git.ChangedFiles.Add("SampleSolution.slnx");

        var result = await RunAsync();

        result.DeletedChunks.Should().BeGreaterThan(0);
        result.InsertedChunks.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task Should_find_async_methods_via_filtered_knn_query()
    {
        await RunAsync();

        using var verify = new SqliteIndexStore(_dbPath, _embedding.VectorDimensions);
        await verify.OpenAsync(CancellationToken.None);
        await using var cmd = verify.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT fully_qualified_symbol_name
            FROM code_chunks
            WHERE symbol_kind = 'method' AND is_async = 1
            LIMIT 5";
        await using var reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
        var names = new List<string>();
        while (await reader.ReadAsync(CancellationToken.None))
        {
            names.Add(reader.GetString(0));
        }

        names.Should().Contain(n => n.Contains("FindAsync", StringComparison.Ordinal));
    }
}
