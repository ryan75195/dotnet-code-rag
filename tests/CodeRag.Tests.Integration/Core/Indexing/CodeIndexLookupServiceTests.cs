using CodeRag.Core.Indexing;
using CodeRag.Tests.Integration.Core.Indexing.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace CodeRag.Tests.Integration.Core.Indexing;

[TestFixture]
public class CodeIndexLookupServiceTests
{
    private SampleSolutionFixture _fixture = null!;
    private FakeEmbeddingClient _embedding = null!;
    private StubGitDiffService _git = null!;
    private MsBuildWorkspaceLoadingService _workspaceLoadingService = null!;
    private string _dbPath = null!;
    private CodeIndexLookupService _lookup = null!;

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

        _lookup = new CodeIndexLookupService(storeFactory);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _workspaceLoadingService.DisposeAsync();
        _fixture.Dispose();
    }

    [Test]
    public async Task Should_find_symbol_by_short_name()
    {
        var hits = await _lookup.FindSymbol(_dbPath, "ConsoleLogger", null, 10, CancellationToken.None);

        hits.Should().NotBeEmpty();
        hits.Should().Contain(h => h.FullyQualifiedSymbolName.Contains("ConsoleLogger", StringComparison.Ordinal));
    }

    [Test]
    public async Task Should_filter_find_symbol_by_kind()
    {
        var hits = await _lookup.FindSymbol(_dbPath, "FindAsync", SymbolKinds.Method, 10, CancellationToken.None);

        hits.Should().NotBeEmpty();
        hits.Should().OnlyContain(h => h.SymbolKind == SymbolKinds.Method);
    }

    [Test]
    public async Task Should_list_classes_implementing_interface()
    {
        var hits = await _lookup.ListImplementations(_dbPath, "Sample.Lib.ILogger", 10, CancellationToken.None);

        hits.Should().NotBeEmpty();
        hits.Should().Contain(h => h.SymbolDisplayName == "ConsoleLogger");
    }

    [Test]
    public async Task Should_list_chunks_decorated_with_obsolete_attribute()
    {
        var hits = await _lookup.ListAttributedWith(_dbPath, "System.ObsoleteAttribute", 10, CancellationToken.None);

        hits.Should().NotBeEmpty();
        hits.Should().Contain(h => h.FullyQualifiedSymbolName.Contains("LegacyHelper.Format", StringComparison.Ordinal));
    }
}
