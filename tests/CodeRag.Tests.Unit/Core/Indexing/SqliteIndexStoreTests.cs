using System.Collections.Immutable;
using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Unit.Core.Indexing;

[TestFixture]
public class SqliteIndexStoreTests
{
    private const int TestVectorDimensions = 1024;

    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"coderag-{Guid.NewGuid():N}.db");
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Test]
    public async Task Should_create_schema_when_file_is_new()
    {
        using var store = new SqliteIndexStore(_dbPath, TestVectorDimensions);
        await store.OpenAsync(CancellationToken.None);

        var metadata = await store.TryGetMetadataAsync(CancellationToken.None);
        metadata.Should().BeNull("a brand-new file has no metadata row");

        File.Exists(_dbPath).Should().BeTrue();
    }

    [Test]
    public async Task Should_round_trip_metadata()
    {
        using var store = new SqliteIndexStore(_dbPath, TestVectorDimensions);
        await store.OpenAsync(CancellationToken.None);

        var written = new IndexMetadata(
            SchemaVersion: 2,
            SolutionFilePath: "C:\\code\\foo.sln",
            RepositoryRootPath: "C:\\code",
            IndexedAtCommitSha: "abc123",
            IndexedAtUtc: new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero),
            EmbeddingModelName: "voyage-code-3",
            EmbeddingVectorDimensions: TestVectorDimensions);
        await store.SetMetadataAsync(written, CancellationToken.None);

        var read = await store.TryGetMetadataAsync(CancellationToken.None);
        read.Should().BeEquivalentTo(written);
    }

    [Test]
    public async Task Should_throw_when_schema_version_does_not_match()
    {
        using (var store = new SqliteIndexStore(_dbPath, TestVectorDimensions))
        {
            await store.OpenAsync(CancellationToken.None);
            var meta = new IndexMetadata(99, "x", "x", "x", DateTimeOffset.UtcNow, "x", TestVectorDimensions);
            await store.SetMetadataAsync(meta, CancellationToken.None);
        }

        using var reopened = new SqliteIndexStore(_dbPath, TestVectorDimensions);
        var act = async () => await reopened.OpenAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*schema version*99*");
    }

    [Test]
    public async Task Should_persist_all_columns_and_child_rows()
    {
        using var store = new SqliteIndexStore(_dbPath, TestVectorDimensions);
        await store.OpenAsync(CancellationToken.None);

        var chunk = TestChunks.SampleMethod();
        long id = await store.InsertChunkAsync(chunk, CancellationToken.None);

        id.Should().BePositive();
        var roundTripped = await store.GetChunkByIdAsync(id, CancellationToken.None);
        roundTripped.Should().BeEquivalentTo(chunk, options => options.Excluding(c => c.SourceText));
    }

    [Test]
    public async Task Should_replace_child_rows()
    {
        using var store = new SqliteIndexStore(_dbPath, TestVectorDimensions);
        await store.OpenAsync(CancellationToken.None);

        var original = TestChunks.SampleMethod();
        long id = await store.InsertChunkAsync(original, CancellationToken.None);

        var updated = original with
        {
            Parameters = ImmutableArray.Create(new ChunkParameter(0, "x", "System.String", null, false))
        };
        await store.UpdateChunkAsync(id, updated, CancellationToken.None);

        var roundTripped = await store.GetChunkByIdAsync(id, CancellationToken.None);
        roundTripped!.Parameters.Should().HaveCount(1);
        roundTripped.Parameters[0].Name.Should().Be("x");
    }

    [Test]
    public async Task Should_persist_vector_keyed_by_chunk_id()
    {
        using var store = new SqliteIndexStore(_dbPath, TestVectorDimensions);
        await store.OpenAsync(CancellationToken.None);

        var chunk = TestChunks.SampleMethod();
        long id = await store.InsertChunkAsync(chunk, CancellationToken.None);

        var vector = new float[TestVectorDimensions];
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] = i * 0.001f;
        }
        await store.UpsertEmbeddingAsync(id, vector, CancellationToken.None);

        (await store.HasEmbeddingAsync(id, CancellationToken.None)).Should().BeTrue();
    }

    [Test]
    public async Task Should_return_only_chunks_in_that_file()
    {
        using var store = new SqliteIndexStore(_dbPath, TestVectorDimensions);
        IIndexStore api = store;
        await store.OpenAsync(CancellationToken.None);

        var fooChunk = TestChunks.SampleMethod() with { RelativeFilePath = "src/Foo.cs", FullyQualifiedSymbolName = "X.Foo.Run" };
        var barChunk = TestChunks.SampleMethod() with { RelativeFilePath = "src/Bar.cs", FullyQualifiedSymbolName = "X.Bar.Run" };

        await store.InsertChunkAsync(fooChunk, CancellationToken.None);
        await store.InsertChunkAsync(barChunk, CancellationToken.None);

        var summaries = await api.GetChunkSummariesForFileAsync("src/Foo.cs", CancellationToken.None);

        summaries.Should().HaveCount(1);
        summaries[0].FullyQualifiedSymbolName.Should().Be("X.Foo.Run");
    }

    [Test]
    public async Task Should_remove_row_and_cascade_children()
    {
        using var store = new SqliteIndexStore(_dbPath, TestVectorDimensions);
        IIndexStore api = store;
        await store.OpenAsync(CancellationToken.None);

        var chunk = TestChunks.SampleMethod();
        long id = await store.InsertChunkAsync(chunk, CancellationToken.None);
        await store.UpsertEmbeddingAsync(id, new float[TestVectorDimensions], CancellationToken.None);

        await api.DeleteChunkAsync(id, CancellationToken.None);

        var summaries = await api.GetChunkSummariesForFileAsync(chunk.RelativeFilePath, CancellationToken.None);
        summaries.Should().BeEmpty();
        (await store.HasEmbeddingAsync(id, CancellationToken.None)).Should().BeFalse();
    }

    [Test]
    public async Task Should_remove_every_chunk_in_that_file()
    {
        using var store = new SqliteIndexStore(_dbPath, TestVectorDimensions);
        IIndexStore api = store;
        await store.OpenAsync(CancellationToken.None);

        var path = "src/Foo.cs";
        await store.InsertChunkAsync(TestChunks.SampleMethod() with { RelativeFilePath = path, FullyQualifiedSymbolName = "X.Foo.Run1" }, CancellationToken.None);
        await store.InsertChunkAsync(TestChunks.SampleMethod() with { RelativeFilePath = path, FullyQualifiedSymbolName = "X.Foo.Run2" }, CancellationToken.None);

        await api.DeleteChunksForFileAsync(path, CancellationToken.None);

        var summaries = await api.GetChunkSummariesForFileAsync(path, CancellationToken.None);
        summaries.Should().BeEmpty();
    }

    [Test]
    public async Task Should_find_symbol_by_short_name_or_fqn()
    {
        using var store = new SqliteIndexStore(_dbPath, TestVectorDimensions);
        IIndexStore api = store;
        await store.OpenAsync(CancellationToken.None);

        var inner = TestChunks.SampleMethod() with
        {
            FullyQualifiedSymbolName = "Game.Inner.Foo.Run()",
            SymbolDisplayName = "Run",
        };
        var outer = TestChunks.SampleMethod() with
        {
            FullyQualifiedSymbolName = "Game.Outer.Foo.Run()",
            SymbolDisplayName = "Run",
        };
        await store.InsertChunkAsync(inner, CancellationToken.None);
        await store.InsertChunkAsync(outer, CancellationToken.None);

        var byShort = await api.FindSymbolByName("Run", null, 10, CancellationToken.None);
        byShort.Should().HaveCount(2);

        var byFqn = await api.FindSymbolByName("Game.Inner.Foo.Run()", null, 10, CancellationToken.None);
        byFqn.Should().ContainSingle().Which.FullyQualifiedSymbolName.Should().Be("Game.Inner.Foo.Run()");
    }

    [Test]
    public async Task Should_filter_find_symbol_by_kind()
    {
        using var store = new SqliteIndexStore(_dbPath, TestVectorDimensions);
        IIndexStore api = store;
        await store.OpenAsync(CancellationToken.None);

        var method = TestChunks.SampleMethod() with
        {
            FullyQualifiedSymbolName = "X.Y.DoThing()",
            SymbolDisplayName = "DoThing",
            SymbolKind = SymbolKinds.Method,
        };
        var classChunk = TestChunks.SampleMethod() with
        {
            FullyQualifiedSymbolName = "X.Y.DoThing",
            SymbolDisplayName = "DoThing",
            SymbolKind = SymbolKinds.Class,
        };
        await store.InsertChunkAsync(method, CancellationToken.None);
        await store.InsertChunkAsync(classChunk, CancellationToken.None);

        var hits = await api.FindSymbolByName("DoThing", SymbolKinds.Class, 10, CancellationToken.None);

        hits.Should().ContainSingle()
            .Which.SymbolKind.Should().Be(SymbolKinds.Class);
    }

    [Test]
    public async Task Should_list_chunks_implementing_interface()
    {
        using var store = new SqliteIndexStore(_dbPath, TestVectorDimensions);
        IIndexStore api = store;
        await store.OpenAsync(CancellationToken.None);

        var implementer = TestChunks.SampleMethod() with
        {
            FullyQualifiedSymbolName = "X.MyService",
            SymbolDisplayName = "MyService",
            SymbolKind = SymbolKinds.Class,
            ImplementedInterfaceFullyQualifiedNames = ImmutableArray.Create("X.IMyService"),
        };
        var unrelated = TestChunks.SampleMethod() with
        {
            FullyQualifiedSymbolName = "X.OtherService",
            SymbolDisplayName = "OtherService",
            SymbolKind = SymbolKinds.Class,
        };
        await store.InsertChunkAsync(implementer, CancellationToken.None);
        await store.InsertChunkAsync(unrelated, CancellationToken.None);

        var hits = await api.ListImplementations("X.IMyService", 10, CancellationToken.None);

        hits.Should().ContainSingle()
            .Which.FullyQualifiedSymbolName.Should().Be("X.MyService");
    }

    [Test]
    public async Task Should_list_chunks_decorated_with_attribute()
    {
        using var store = new SqliteIndexStore(_dbPath, TestVectorDimensions);
        IIndexStore api = store;
        await store.OpenAsync(CancellationToken.None);

        var decorated = TestChunks.SampleMethod() with
        {
            FullyQualifiedSymbolName = "X.Y.OldThing",
            SymbolDisplayName = "OldThing",
            Attributes = ImmutableArray.Create(new ChunkAttribute("System.ObsoleteAttribute", null)),
        };
        var plain = TestChunks.SampleMethod() with
        {
            FullyQualifiedSymbolName = "X.Y.NewThing",
            SymbolDisplayName = "NewThing",
            Attributes = ImmutableArray<ChunkAttribute>.Empty,
        };
        await store.InsertChunkAsync(decorated, CancellationToken.None);
        await store.InsertChunkAsync(plain, CancellationToken.None);

        var hits = await api.ListAttributedWith("System.ObsoleteAttribute", 10, CancellationToken.None);

        hits.Should().ContainSingle()
            .Which.FullyQualifiedSymbolName.Should().Be("X.Y.OldThing");
    }
}
