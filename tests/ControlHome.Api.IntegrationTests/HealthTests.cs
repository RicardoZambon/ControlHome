using System.Net;
using ControlHome.TestSupport;

namespace ControlHome.Api.IntegrationTests;

public sealed class HealthTests(ControlHomeApiFactory factory) : IClassFixture<ControlHomeApiFactory>
{
    [Fact]
    public async Task Live_returns_200_Healthy()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/plain");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task Ready_returns_200_Healthy_when_the_database_is_ready()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Healthy");
    }

    [Theory]
    [InlineData("/api/nope")]
    [InlineData("/api/nope/deeper.json")]
    [InlineData("/hubs/nope")]
    [InlineData("/health/nope")]
    public async Task Unknown_api_path_returns_404_not_spa(string path)
    {
        using var spa = new TempSpaDirectory();
        using var spaFactory = factory.WithWebHostBuilder(b => b.UseSetting("ControlHome:SpaPath", spa.Root));
        await spaFactory.WaitUntilReadyAsync(ControlHomeApiFactory.ReadyTimeout);
        using var client = spaFactory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain(TempSpaDirectory.Marker);
        body.ShouldContain("\"code\":\"request.not_found\"");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/floors/1")]
    [InlineData("/apiary")]
    [InlineData("/index.html")]
    public async Task Spa_fallback_serves_index_html(string path)
    {
        using var spa = new TempSpaDirectory();
        using var spaFactory = factory.WithWebHostBuilder(b => b.UseSetting("ControlHome:SpaPath", spa.Root));
        using var client = spaFactory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        response.Headers.CacheControl?.NoCache.ShouldBe(true);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain(TempSpaDirectory.Marker);
    }

    [Fact]
    public async Task Spa_static_assets_are_served_and_missing_assets_are_404()
    {
        using var spa = new TempSpaDirectory();
        using var spaFactory = factory.WithWebHostBuilder(b => b.UseSetting("ControlHome:SpaPath", spa.Root));
        using var client = spaFactory.CreateClient();

        using var asset = await client.GetAsync("/assets/app.js", TestContext.Current.CancellationToken);
        using var missing = await client.GetAsync("/assets/missing.js", TestContext.Current.CancellationToken);

        asset.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await asset.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe(TempSpaDirectory.AssetContent);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
