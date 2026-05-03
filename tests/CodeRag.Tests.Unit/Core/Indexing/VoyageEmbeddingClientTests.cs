using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Unit.Core.Indexing;

[TestFixture]
public class VoyageEmbeddingClientTests
{
    [Test]
    public void Should_construct_with_an_http_client_and_a_dummy_api_key()
    {
        using var http = new HttpClient();
        var client = new VoyageEmbeddingClient(http, "voyage-fake");

        client.Should().NotBeNull();
        client.VectorDimensions.Should().Be(VoyageEmbeddingOptions.VectorDimensions);
    }
}
