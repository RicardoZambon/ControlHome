using ControlHome.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace ControlHome.Api.IntegrationTests.RequestEdge;

/// <summary>Unit tests for the same-origin comparison (<c>Origin</c> vs <c>{scheme}://{host}</c>).</summary>
public sealed class OriginMatchingTests
{
    [Theory]
    [InlineData("http://localhost", "http", "localhost")]
    [InlineData("http://LOCALHOST", "http", "localhost")]
    [InlineData("http://localhost", "http", "LocalHost")]
    [InlineData("http://localhost:5080", "http", "localhost:5080")]
    [InlineData("http://localhost", "http", "localhost:80")]
    [InlineData("https://home.example.test", "https", "home.example.test:443")]
    [InlineData("http://192.168.1.10:8711", "http", "192.168.1.10:8711")]
    [InlineData("http://[::1]:5080", "http", "[::1]:5080")]
    [InlineData("https://umbrel.local", "https", "umbrel.local")]
    public void Same_origin_matches(string origin, string scheme, string host)
    {
        OriginValidationMiddleware.IsSameOrigin(new StringValues(origin), scheme, new HostString(host)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("http://localhost:5080", "http", "localhost")] // other port
    [InlineData("http://localhost", "http", "localhost:5080")]
    [InlineData("https://localhost", "http", "localhost")] // other scheme
    [InlineData("http://localhost", "https", "localhost")]
    [InlineData("http://evil.local", "http", "localhost")]
    [InlineData("http://localhost.evil.local", "http", "localhost")]
    [InlineData("http://evil.local/http://localhost", "http", "localhost")]
    [InlineData("null", "http", "localhost")] // sandboxed iframes, file:// pages
    [InlineData("", "http", "localhost")]
    [InlineData("localhost", "http", "localhost")]
    [InlineData("http://localhost/", "http", "localhost")] // not a serialized origin
    [InlineData("http://localhost/path", "http", "localhost")]
    [InlineData("http://localhost?x=1", "http", "localhost")]
    [InlineData("http://localhost#x", "http", "localhost")]
    [InlineData("http://user@localhost", "http", "localhost")]
    [InlineData("ws://localhost", "http", "localhost")]
    [InlineData("http://localhost", "http", "")] // no Host header
    public void Anything_else_does_not_match(string origin, string scheme, string host)
    {
        OriginValidationMiddleware.IsSameOrigin(new StringValues(origin), scheme, new HostString(host)).ShouldBeFalse();
    }

    [Fact]
    public void Missing_or_repeated_origin_does_not_match()
    {
        OriginValidationMiddleware.IsSameOrigin(StringValues.Empty, "http", new HostString("localhost")).ShouldBeFalse();
        OriginValidationMiddleware.IsSameOrigin(new StringValues(["http://localhost", "http://localhost"]), "http", new HostString("localhost"))
            .ShouldBeFalse();
    }
}
