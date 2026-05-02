using System.Collections.Immutable;
using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Unit.Core.Indexing;

[TestFixture]
public class SqliteIndexStoreTests
{
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
        using var store = new SqliteIndexStore(_dbPath);
        await store.OpenAsync(CancellationToken.None);

        var metadata = await store.TryGetMetadataAsync(CancellationToken.None);
        metadata.Should().BeNull("a brand-new file has no metadata row");

        File.Exists(_dbPath).Should().BeTrue();
    }

    [Test]
    public async Task Should_round_trip_metadata()
    {
        using var store = new SqliteIndexStore(_dbPath);
        await store.OpenAsync(CancellationToken.None);

        var written = new IndexMetadata(
            SchemaVersion: 1,
            SolutionFilePath: "C:\\code\\foo.sln",
            RepositoryRootPath: "C:\\code",
            IndexedAtCommitSha: "abc123",
            IndexedAtUtc: new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero),
            EmbeddingModelName: "text-embedding-3-large",
            EmbeddingVectorDimensions: 3072);
        await store.SetMetadataAsync(written, CancellationToken.None);

        var read = await store.TryGetMetadataAsync(CancellationToken.None);
        read.Should().BeEquivalentTo(written);
    }

    [Test]
    public async Task Should_throw_when_schema_version_does_not_match()
    {
        using (var store = new SqliteIndexStore(_dbPath))
        {
            await store.OpenAsync(CancellationToken.None);
            var meta = new IndexMetadata(99, "x", "x", "x", DateTimeOffset.UtcNow, "x", 3072);
            await store.SetMetadataAsync(meta, CancellationToken.None);
        }

        using var reopened = new SqliteIndexStore(_dbPath);
        var act = async () => await reopened.OpenAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*schema version*99*");
    }

    [Test]
    public async Task Should_persist_all_columns_and_child_rows()
    {
        using var store = new SqliteIndexStore(_dbPath);
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
        using var store = new SqliteIndexStore(_dbPath);
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
        using var store = new SqliteIndexStore(_dbPath);
        await store.OpenAsync(CancellationToken.None);

        var chunk = TestChunks.SampleMethod();
        long id = await store.InsertChunkAsync(chunk, CancellationToken.None);

        var vector = new float[3072];
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
        using var store = new SqliteIndexStore(_dbPath);
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
        using var store = new SqliteIndexStore(_dbPath);
        IIndexStore api = store;
        await store.OpenAsync(CancellationToken.None);

        var chunk = TestChunks.SampleMethod();
        long id = await store.InsertChunkAsync(chunk, CancellationToken.None);
        await store.UpsertEmbeddingAsync(id, new float[3072], CancellationToken.None);

        await api.DeleteChunkAsync(id, CancellationToken.None);

        var summaries = await api.GetChunkSummariesForFileAsync(chunk.RelativeFilePath, CancellationToken.None);
        summaries.Should().BeEmpty();
        (await store.HasEmbeddingAsync(id, CancellationToken.None)).Should().BeFalse();
    }

    [Test]
    public async Task Should_remove_every_chunk_in_that_file()
    {
        using var store = new SqliteIndexStore(_dbPath);
        IIndexStore api = store;
        await store.OpenAsync(CancellationToken.None);

        var path = "src/Foo.cs";
        await store.InsertChunkAsync(TestChunks.SampleMethod() with { RelativeFilePath = path, FullyQualifiedSymbolName = "X.Foo.Run1" }, CancellationToken.None);
        await store.InsertChunkAsync(TestChunks.SampleMethod() with { RelativeFilePath = path, FullyQualifiedSymbolName = "X.Foo.Run2" }, CancellationToken.None);

        await api.DeleteChunksForFileAsync(path, CancellationToken.None);

        var summaries = await api.GetChunkSummariesForFileAsync(path, CancellationToken.None);
        summaries.Should().BeEmpty();
    }
}
