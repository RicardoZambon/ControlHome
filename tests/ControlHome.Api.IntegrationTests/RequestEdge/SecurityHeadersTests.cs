using System.Net;

namespace ControlHome.Api.IntegrationTests.RequestEdge;

public sealed class SecurityHeadersTests(RequestEdgeApiFactory factory) : IClassFixture<RequestEdgeApiFactory>
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; connect-src 'self'; style-src 'self' 'unsafe-inline'; "
        + "script-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'";

    [Theory]
    [InlineData("GET", "/api/setup/status", HttpStatusCode.OK)]
    [InlineData("GET", "/health/live", HttpStatusCode.OK)]
    [InlineData("GET", "/api/nope", HttpStatusCode.NotFound)]
    [InlineData("GET", "/api/users", HttpStatusCode.Unauthorized)]
    [InlineData("GET", "/api/_test/throw", HttpStatusCode.InternalServerError)]
    [InlineData("POST", "/api/_test/reset", HttpStatusCode.Forbidden)] // no Origin
    [InlineData("GET", "/scalar", HttpStatusCode.NotFound)] // Scalar exists only in Development
    public async Task Every_response_carries_the_security_headers(string method, string path, HttpStatusCode status)
    {
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(status);
        ShouldHaveSecurityHeaders(response, ContentSecurityPolicy);
    }

    [Fact]
    public async Task Denied_network_responses_carry_the_security_headers()
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(HttpMethod.Get, "/api/setup/status", remoteIp: "198.51.100.200");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ShouldHaveSecurityHeaders(response, ContentSecurityPolicy);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/floors/1")]
    [InlineData("/assets/app.js")]
    public async Task Spa_responses_carry_the_csp(string path)
    {
        using var spa = new TempSpaDirectory();
        using var spaFactory = factory.WithWebHostBuilder(b => b.UseSetting("ControlHome:SpaPath", spa.Root));
        using var client = spaFactory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ShouldHaveSecurityHeaders(response, ContentSecurityPolicy);
    }

    internal static void ShouldHaveSecurityHeaders(HttpResponseMessage response, string contentSecurityPolicy)
    {
        Single(response, "Content-Security-Policy").ShouldBe(contentSecurityPolicy);
        Single(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Single(response, "Referrer-Policy").ShouldBe("same-origin");
        Single(response, "X-Frame-Options").ShouldBe("DENY");
    }

    private static string Single(HttpResponseMessage response, string name)
    {
        var found = response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values);
        found.ShouldBeTrue($"{name} is missing");
        return values!.ShouldHaveSingleItem();
    }
}

/// <summary>In Development, Scalar's API reference (<c>/scalar</c>) gets a CSP that lets it run; everything else keeps the strict one.</summary>
public sealed class ScalarContentSecurityPolicyTests(DevelopmentApiFactory factory) : IClassFixture<DevelopmentApiFactory>
{
    [Theory]
    [InlineData("/scalar")]
    [InlineData("/scalar/v1")]
    public async Task Scalar_gets_a_policy_for_its_inline_script_and_fonts(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        var policy = response.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem();
        policy.ShouldNotBe(SecurityHeadersTests.ContentSecurityPolicy);
        policy.ShouldContain("script-src 'self' 'unsafe-inline'");
        policy.ShouldContain("https://fonts.scalar.com");
        policy.ShouldContain("frame-ancestors 'none'");
        policy.ShouldContain("object-src 'none'");
        response.Headers.GetValues("X-Frame-Options").ShouldHaveSingleItem().ShouldBe("DENY");
    }

    [Theory]
    [InlineData("/api/setup/status")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalarx")]
    public async Task Other_paths_keep_the_strict_policy(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        SecurityHeadersTests.ShouldHaveSecurityHeaders(response, SecurityHeadersTests.ContentSecurityPolicy);
    }
}
