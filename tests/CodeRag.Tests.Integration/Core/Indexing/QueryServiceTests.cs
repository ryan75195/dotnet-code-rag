using CodeRag.Core.Indexing;
using CodeRag.Tests.Integration.Core.Indexing.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace CodeRag.Tests.Integration.Core.Indexing;

[TestFixture]
public class QueryServiceTests
{
    private SampleSolutionFixture _fixture = null!;
    private FakeEmbeddingClient _embedding = null!;
    private StubGitDiffService _git = null!;
    private MsBuildWorkspaceLoadingService _workspaceLoadingService = null!;
    private string _dbPath = null!;
    private QueryService _queryService = null!;

    [SetUp]
    public async Task SetUp()
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

        var indexingService = new IndexingService(deps);
        await indexingService.Run(new IndexRunRequest(_fixture.SolutionPath, _dbPath), CancellationToken.None);

        _queryService = new QueryService(_embedding, storeFactory);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _workspaceLoadingService.DisposeAsync();
        _fixture.Dispose();
    }

    private static QueryFilters NoFilters() =>
        new(null, null, null, null, null, null, null, null, ExcludeTests: false, null, null);

    [Test]
    public async Task Should_return_chunks_ranked_by_fused_score()
    {
        var hits = await _queryService.Run(
            new QueryRequest(_dbPath, "find a user by name", 5, NoFilters()),
            CancellationToken.None);

        hits.Should().NotBeEmpty();
        hits.Should().BeInDescendingOrder(h => h.FusedScore);
        hits.Count.Should().BeLessThanOrEqualTo(5);
    }

    [Test]
    public async Task Should_apply_symbol_kind_filter()
    {
        var filters = NoFilters() with { SymbolKind = "method" };
        var hits = await _queryService.Run(
            new QueryRequest(_dbPath, "look up a record", 10, filters),
            CancellationToken.None);

        hits.Should().NotBeEmpty();
        hits.Should().OnlyContain(h => h.SymbolKind == "method");
    }

    [Test]
    public async Task Should_apply_is_async_filter()
    {
        var filters = NoFilters() with { SymbolKind = "method", IsAsync = true };
        var hits = await _queryService.Run(
            new QueryRequest(_dbPath, "asynchronous method", 20, filters),
            CancellationToken.None);

        hits.Should().NotBeEmpty();
        hits.Should().Contain(h => h.FullyQualifiedSymbolName.Contains("FindAsync", StringComparison.Ordinal));
    }

    [Test]
    public async Task Should_blend_bm25_and_vector_scores_via_rrf()
    {
        var filters = NoFilters();
        var hits = await _queryService.Run(
            new QueryRequest(_dbPath, "FindAsync UserService", 20, filters),
            CancellationToken.None);

        hits.Should().NotBeEmpty();
        hits.Should().Contain(h => h.FullyQualifiedSymbolName.Contains("FindAsync", StringComparison.Ordinal));
        hits.Should().OnlyContain(h => h.FusedScore > 0);
    }

    [Test]
    public async Task Should_apply_exclude_tests_filter()
    {
        var filters = NoFilters() with { ExcludeTests = true };
        var hits = await _queryService.Run(
            new QueryRequest(_dbPath, "user service", 50, filters),
            CancellationToken.None);

        hits.Should().OnlyContain(h => !h.FullyQualifiedSymbolName.Contains(".Tests.", StringComparison.Ordinal));
    }
}
