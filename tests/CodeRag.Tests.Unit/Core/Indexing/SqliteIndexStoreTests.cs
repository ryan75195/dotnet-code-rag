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
}
