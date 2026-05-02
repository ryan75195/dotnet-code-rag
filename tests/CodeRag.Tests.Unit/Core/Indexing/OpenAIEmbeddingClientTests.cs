using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Unit.Core.Indexing;

[TestFixture]
public class OpenAIEmbeddingClientTests
{
    [Test]
    public void Should_construct_with_a_dummy_api_key()
    {
        var client = new OpenAIEmbeddingClient("sk-fake");
        client.Should().NotBeNull();
    }
}
