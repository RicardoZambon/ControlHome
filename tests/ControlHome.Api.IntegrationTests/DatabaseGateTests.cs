using System.Net;
using System.Net.Http.Json;
using ControlHome.Infrastructure.Persistence;
using ControlHome.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests;

/// <summary>Without a usable database the API still starts and answers 503 <c>db.unavailable</c> (Review Focus 4).</summary>
public sealed class DatabaseNotConfiguredTests(NoDatabaseApiFactory factory) : IClassFixture<NoDatabaseApiFactory>
{
    [Theory]
    [InlineData("/api/anything")]
    [InlineData("/api/users")]
    [InlineData("/api/auth/me")]
    public async Task Api_answers_503_db_unavailable(string path)
    {
        using var client = factory.CreateApiClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.ReadJsonAsync(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("db.unavailable");
        problem.GetProperty("state").GetString().ShouldBe(nameof(DatabaseState.NotConfigured));
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Mutations_are_gated_too()
    {
        using var client = factory.CreateApiClient();

        using var response = await client.PostAsync("/api/auth/login", content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.ReadProblemCodeAsync(TestContext.Current.CancellationToken)).ShouldBe("db.unavailable");
    }

    [Fact]
    public async Task Setup_status_still_answers()
    {
        using var client = factory.CreateApiClient();

        using var response = await client.GetAsync("/api/setup/status", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var status = await response.ReadJsonAsync(TestContext.Current.CancellationToken);
        status.GetProperty("setupCompleted").GetBoolean().ShouldBeFalse();
        status.GetProperty("databaseState").GetString().ShouldBe(nameof(DatabaseState.NotConfigured));
        status.GetProperty("databaseConfigured").GetBoolean().ShouldBeFalse();
        status.GetProperty("databaseSource").GetString().ShouldBe("none");
        status.GetProperty("version").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Setup_status_answers_even_with_a_session_cookie_that_needs_the_key_ring()
    {
        // A well-formed Data Protection payload (magic header + key id) forces a key-ring lookup, which needs the database.
        var payload = new byte[] { 0x09, 0xF0, 0xC9, 0xF0 }.Concat(Guid.NewGuid().ToByteArray()).Concat(new byte[48]).ToArray();
        using var client = factory.CreateDefaultClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/setup/status");
        request.Headers.Add("Cookie", $"ControlHome.Session={Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(payload)}");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync(TestContext.Current.CancellationToken)).GetProperty("databaseState").GetString()
            .ShouldBe(nameof(DatabaseState.NotConfigured));
    }

    [Fact]
    public async Task Ready_is_unhealthy_and_live_is_healthy()
    {
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        using var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Spa_and_unknown_non_api_paths_are_not_gated()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/hubs/nope", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadProblemCodeAsync(TestContext.Current.CancellationToken)).ShouldBe("request.not_found");
    }
}

public sealed class DatabaseUnreachableTests(UnreachableDatabaseApiFactory factory) : IClassFixture<UnreachableDatabaseApiFactory>
{
    [Fact]
    public async Task Startup_goes_from_Connecting_to_Failed_and_the_api_keeps_answering()
    {
        var readiness = factory.Services.GetRequiredService<DatabaseReadiness>();
        using var client = factory.CreateApiClient();

        // Connecting while retrying ...
        using (var early = await client.GetAsync("/api/setup/status", TestContext.Current.CancellationToken))
        {
            var status = await early.ReadJsonAsync(TestContext.Current.CancellationToken);
            status.GetProperty("databaseConfigured").GetBoolean().ShouldBeTrue();
            status.GetProperty("databaseState").GetString().ShouldBeOneOf(nameof(DatabaseState.Connecting), nameof(DatabaseState.Failed));
        }

        // ... then Failed once Database:StartupTimeout (2 s) has passed.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (readiness.State != DatabaseState.Failed)
        {
            await Task.Delay(100, timeout.Token);
        }

        readiness.LastError.ShouldBe("db.connection_failed");

        using (var failed = await client.GetAsync("/api/setup/status", TestContext.Current.CancellationToken))
        {
            var status = await failed.ReadJsonAsync(TestContext.Current.CancellationToken);
            status.GetProperty("databaseState").GetString().ShouldBe(nameof(DatabaseState.Failed));
            status.GetProperty("databaseError").GetString().ShouldBe("db.connection_failed");
            status.GetProperty("databaseSource").GetString().ShouldBe("environment");
        }

        // The wizard's database step is only for a server without a configured connection.
        using (var configure = await client.PostAsJsonAsync(
            "/api/setup/database",
            new { setupCode = ControlHomeApiFactory.SetupCode, host = "127.0.0.1", port = 1, database = "ControlHome_Test_x", userName = "sa", password = "x", trustServerCertificate = true },
            TestContext.Current.CancellationToken))
        {
            configure.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await configure.ReadProblemCodeAsync(TestContext.Current.CancellationToken)).ShouldBe("db.already_configured");
        }

        using var gated = await client.GetAsync("/api/anything", TestContext.Current.CancellationToken);
        gated.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var problem = await gated.ReadJsonAsync(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("db.unavailable");
        problem.GetProperty("state").GetString().ShouldBe(nameof(DatabaseState.Failed));
    }
}
