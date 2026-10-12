using System.Net;
using System.Net.Http.Headers;
using ControlHome.TestSupport;

namespace ControlHome.Api.IntegrationTests.RequestEdge;

/// <summary>
/// Cross-origin protection (Review Focus 3): mutations need Origin == the app's origin and <c>X-ControlHome: 1</c>;
/// WebSocket upgrades need the Origin; safe methods need neither.
/// </summary>
public sealed class OriginValidationTests(RequestEdgeApiFactory factory) : IClassFixture<RequestEdgeApiFactory>
{
    private const string SameOrigin = "http://localhost";

    // Answers 204 when a request reaches it (Testing environment only).
    private const string MutationPath = "/api/_test/reset";

    [Fact]
    public async Task Post_without_origin_is_403_cross_origin()
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(HttpMethod.Post, MutationPath, headers: ("X-ControlHome", "1"));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await ShouldBeForbiddenAsync(response, "request.cross_origin");
    }

    [Theory]
    [InlineData("http://evil.local")]
    [InlineData("http://localhost:8080")]
    [InlineData("https://localhost")]
    [InlineData("http://localhost.evil.local")]
    [InlineData("null")]
    public async Task Post_with_a_foreign_origin_is_403_cross_origin(string origin)
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(HttpMethod.Post, MutationPath, headers: [("Origin", origin), ("X-ControlHome", "1")]);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await ShouldBeForbiddenAsync(response, "request.cross_origin");
    }

    [Theory]
    [InlineData("PUT", "/api/home")]
    [InlineData("PATCH", "/api/home")]
    [InlineData("DELETE", "/api/users/0199a3b2-0000-7000-8000-000000000001")]
    [InlineData("POST", "/api/nope")]
    [InlineData("TRACE", "/")]
    public async Task Every_unsafe_method_is_checked_before_routing(string method, string path)
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(new HttpMethod(method), path, headers: [("Origin", "http://evil.local"), ("X-ControlHome", "1")]);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await ShouldBeForbiddenAsync(response, "request.cross_origin");
    }

    [Fact]
    public async Task Post_same_origin_without_the_header_is_403_missing_header()
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(HttpMethod.Post, MutationPath, headers: ("Origin", SameOrigin));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await ShouldBeForbiddenAsync(response, "request.missing_header");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("")]
    public async Task Post_same_origin_with_a_wrong_header_value_is_403_missing_header(string value)
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(HttpMethod.Post, MutationPath, headers: [("Origin", SameOrigin), ("X-ControlHome", value)]);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await ShouldBeForbiddenAsync(response, "request.missing_header");
    }

    [Fact]
    public async Task Post_same_origin_with_the_header_reaches_the_endpoint()
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(HttpMethod.Post, MutationPath, headers: [("Origin", SameOrigin), ("X-ControlHome", "1")]);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Testing_endpoints_are_reachable_with_the_api_client_headers()
    {
        using var client = factory.CreateApiClient();

        using var response = await client.PostAsync(MutationPath, content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Setup_status_is_reachable_with_the_api_client_headers_and_without_any()
    {
        using var apiClient = factory.CreateApiClient();
        using var bareClient = factory.CreateClient();

        using var withHeaders = await apiClient.GetAsync("/api/setup/status", TestContext.Current.CancellationToken);
        using var withoutHeaders = await bareClient.GetAsync("/api/setup/status", TestContext.Current.CancellationToken);

        withHeaders.StatusCode.ShouldBe(HttpStatusCode.OK);
        withoutHeaders.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task Safe_methods_need_neither_origin_nor_header(string method)
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(new HttpMethod(method), "/api/setup/status", headers: ("Origin", "http://evil.local"));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Inbound_hooks_are_exempt()
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(HttpMethod.Post, "/api/hooks/0123456789abcdef0123456789abcdef");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // No hook endpoint exists yet: the request gets past the edge and finds nothing.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadProblemCodeAsync(TestContext.Current.CancellationToken)).ShouldBe("request.not_found");
    }

    [Theory]
    [InlineData("http://evil.local")]
    [InlineData(null)]
    public async Task WebSocket_upgrade_with_a_foreign_or_missing_origin_is_403_cross_origin(string? origin)
    {
        using var client = factory.CreateClient();
        using var request = WebSocketUpgrade("/hubs/x", origin);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await ShouldBeForbiddenAsync(response, "request.cross_origin");
    }

    [Fact]
    public async Task WebSocket_upgrade_from_the_same_origin_needs_no_header()
    {
        using var client = factory.CreateClient();
        using var request = WebSocketUpgrade("/hubs/x", SameOrigin);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Browsers cannot add headers to WebSocket handshakes; no hub exists yet, so the request ends at the 404 fallback.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadProblemCodeAsync(TestContext.Current.CancellationToken)).ShouldBe("request.not_found");
    }

    [Fact]
    public async Task WebSocket_client_handshake_with_a_foreign_origin_is_rejected()
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Origin = "http://evil.local";

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => client.ConnectAsync(new Uri(factory.Server.BaseAddress, "/hubs/x"), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("403");
    }

    private static HttpRequestMessage WebSocketUpgrade(string path, string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Connection.Add("Upgrade");
        request.Headers.Upgrade.Add(new ProductHeaderValue("websocket"));
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Key", Convert.ToBase64String(new byte[16]));
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return request;
    }

    private static async Task ShouldBeForbiddenAsync(HttpResponseMessage response, string code)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.ReadJsonAsync(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe(code);
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
    }
}
