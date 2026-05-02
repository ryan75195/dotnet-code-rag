using System.Collections.Immutable;
using CodeRag.Core.Indexing;
using CodeRag.Core.Indexing.Interfaces;
using FluentAssertions;

namespace CodeRag.Tests.Unit.Core.Indexing;

[TestFixture]
public class ReconciliationServiceTests
{
    private readonly IReconciliationService _reconciliationService = new ReconciliationService();

    [Test]
    public void Should_insert_when_chunk_is_only_in_new()
    {
        var newChunk = TestChunks.SampleMethod() with { FullyQualifiedSymbolName = "X.A", SourceTextHash = "h1" };
        var plan = _reconciliationService.Plan([], ImmutableArray.Create(newChunk));

        plan.Inserts.Should().HaveCount(1);
        plan.Updates.Should().BeEmpty();
        plan.Deletes.Should().BeEmpty();
    }

    [Test]
    public void Should_delete_when_chunk_is_only_in_old()
    {
        var existing = new[] { new StoredChunkSummary(42, "X.A", "h1") };
        var plan = _reconciliationService.Plan(existing, ImmutableArray<CodeChunk>.Empty);

        plan.Deletes.Should().BeEquivalentTo([new DeleteOp(42)]);
    }

    [Test]
    public void Should_no_op_when_hash_matches()
    {
        var existing = new[] { new StoredChunkSummary(42, "X.A", "h1") };
        var newChunk = TestChunks.SampleMethod() with { FullyQualifiedSymbolName = "X.A", SourceTextHash = "h1" };
        var plan = _reconciliationService.Plan(existing, ImmutableArray.Create(newChunk));

        plan.Inserts.Should().BeEmpty();
        plan.Updates.Should().BeEmpty();
        plan.Deletes.Should().BeEmpty();
    }

    [Test]
    public void Should_update_with_content_changed_when_hash_differs()
    {
        var existing = new[] { new StoredChunkSummary(42, "X.A", "h1") };
        var newChunk = TestChunks.SampleMethod() with { FullyQualifiedSymbolName = "X.A", SourceTextHash = "h2" };
        var plan = _reconciliationService.Plan(existing, ImmutableArray.Create(newChunk));

        plan.Updates.Should().ContainSingle();
        plan.Updates[0].ChunkId.Should().Be(42);
        plan.Updates[0].ContentChanged.Should().BeTrue();
    }
}
