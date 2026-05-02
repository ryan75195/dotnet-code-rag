using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Unit.Core.Indexing;

[TestFixture]
public class SourceTextHashingServiceTests
{
    [Test]
    public void Should_be_deterministic_for_same_input()
    {
        var hasher = new SourceTextHashingService();

        var first = hasher.Hash("public class Foo { }");
        var second = hasher.Hash("public class Foo { }");

        first.Should().Be(second);
    }

    [Test]
    public void Should_differ_for_different_input()
    {
        var hasher = new SourceTextHashingService();

        var first = hasher.Hash("public class Foo { }");
        var second = hasher.Hash("public class Bar { }");

        first.Should().NotBe(second);
    }

    [Test]
    public void Should_be_lowercase_hex_sha256()
    {
        var hasher = new SourceTextHashingService();

        var hash = hasher.Hash("anything");

        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }
}
