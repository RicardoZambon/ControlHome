using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlHome.Application.Auth;
using ControlHome.Application.Setup;
using ControlHome.Domain.Auditing;
using ControlHome.Domain.Security;
using ControlHome.Infrastructure.Setup;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ControlHome.Api.IntegrationTests;

[Collection(AuthCollection.Name)]
public sealed class SetupTests(AuthApiFactory factory) : ResetAppTestBase<AuthApiFactory>(factory)
{
    [Fact]
    public async Task Status_reports_setup_completed_only_after_completion()
    {
        using var client = Factory.CreateApiClient();

        (await GetStatusAsync(client)).SetupCompleted.ShouldBeFalse();
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        (await GetStatusAsync(client)).SetupCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Wrong_code_is_400_setup_invalid_code_and_creates_nothing()
    {
        using var client = Factory.CreateApiClient();

        using var response = await client.PostAsJsonAsync("/api/setup/complete", AuthTestExtensions.SetupRequest("WRONG-CODE-0000"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("setup.invalid_code");
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        (await QueryDbAsync(db => db.Users.CountAsync(Ct))).ShouldBe(0);
        (await QueryDbAsync(db => db.Homes.CountAsync(Ct))).ShouldBe(0);
        (await GetStatusAsync(client)).SetupCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Valid_setup_creates_the_admin_and_home_and_signs_the_admin_in()
    {
        using var client = Factory.CreateApiClient();

        using var response = await client.PostAsJsonAsync("/api/setup/complete", AuthTestExtensions.SetupRequest(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = (await response.Content.ReadFromJsonAsync<MeDto>(Ct))!;
        me.UserName.ShouldBe(AuthTestExtensions.AdminUserName);
        me.DisplayName.ShouldBe("Administrator");
        me.Roles.ShouldBe([Roles.Admin]);
        me.Locale.ShouldBe("pt-BR");
        me.Theme.ShouldBe("system");

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ControlHome.Session=", StringComparison.Ordinal));
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);

        using var meResponse = await client.GetAsync("/api/auth/me", Ct);
        meResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await meResponse.Content.ReadFromJsonAsync<MeDto>(Ct))!.Id.ShouldBe(me.Id);

        var home = await QueryDbAsync(db => db.Homes.AsNoTracking().SingleAsync(Ct));
        home.Name.ShouldBe("Casa");
        home.TimeZoneId.ShouldBe("America/Sao_Paulo");
        home.Locale.ShouldBe("pt-BR");
        home.Currency.ShouldBe("BRL");
        home.Tariff.PricePerKwh.ShouldBe(0.95m);
        home.SetupCompletedAtUtc.ShouldNotBeNull();

        var audit = (await AuditEntriesAsync("setup.completed")).ShouldHaveSingleItem();
        audit.TargetId.ShouldBe(me.Id.ToString());
        audit.ActorType.ShouldBe(AuditActorType.Anonymous);
        using var details = JsonDocument.Parse(audit.DetailsJson!);
        details.RootElement.GetProperty("adminUserName").GetString().ShouldBe(AuthTestExtensions.AdminUserName);
    }

    [Fact]
    public async Task Completing_again_is_409_already_completed()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        using var other = Factory.CreateApiClient();

        using var response = await other.PostAsJsonAsync("/api/setup/complete", AuthTestExtensions.SetupRequest(userName: "intruder"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("setup.already_completed");
        (await QueryDbAsync(db => db.Users.Select(u => u.UserName).ToListAsync(Ct))).ShouldBe([AuthTestExtensions.AdminUserName]);
    }

    [Theory]
    [InlineData("TEST-SETUP-0001-CODE")]
    [InlineData("test-setup-0001-code")]
    [InlineData("TEST SETUP 0001 CODE")]
    [InlineData(" testsetup0001code ")]
    public async Task Configured_setup_code_is_honoured_ignoring_case_dashes_and_spaces(string typed)
    {
        using var client = Factory.CreateApiClient();

        using var response = await client.PostAsJsonAsync("/api/setup/complete", AuthTestExtensions.SetupRequest(typed), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void Configured_code_is_never_written_to_the_data_folder()
    {
        var provider = Factory.Services.GetRequiredService<SetupCodeProvider>();

        provider.IsConfigured.ShouldBeTrue();
        provider.Current.ShouldBe(ControlHomeApiFactory.SetupCode);
        File.Exists(provider.FilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Invalid_input_is_400_validation_failed_with_errors_and_creates_nothing()
    {
        using var client = Factory.CreateApiClient();
        var request = AuthTestExtensions.SetupRequest(userName: "a b", password: "short") with
        {
            Home = new HomeInput("", "Mars/Olympus_Mons", 0, 181, "fr", "real", -1),
        };

        using var response = await client.PostAsJsonAsync("/api/setup/complete", request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.ReadJsonAsync(Ct);
        problem.GetProperty("code").GetString().ShouldBe("validation.failed");
        problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).ShouldBe(
            [
                "admin.userName", "admin.password", "home.name", "home.timeZoneId", "home.longitude", "home.locale",
                "home.currency", "home.pricePerKwh",
            ],
            ignoreOrder: true);
        (await QueryDbAsync(db => db.Users.CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Twenty_wrong_codes_rotate_the_code_and_test_reset_restores_the_configured_one()
    {
        using var client = Factory.CreateApiClient();
        for (var i = 0; i < SetupCodeProvider.MaxFailedAttempts; i++)
        {
            using var wrong = await client.PostAsJsonAsync("/api/setup/complete", AuthTestExtensions.SetupRequest($"WRONG-{i}"), Ct);
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // Rotated: the configured code no longer works; the new one is announced in the data folder.
        var provider = Factory.Services.GetRequiredService<SetupCodeProvider>();
        provider.IsConfigured.ShouldBeFalse();
        (await File.ReadAllTextAsync(provider.FilePath, Ct)).Trim().ShouldBe(provider.Current);
        using (var rotated = await client.PostAsJsonAsync("/api/setup/complete", AuthTestExtensions.SetupRequest(), Ct))
        {
            rotated.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await rotated.ReadProblemCodeAsync(Ct)).ShouldBe("setup.invalid_code");
        }

        await Factory.ResetAppAsync(Ct);

        provider.IsConfigured.ShouldBeTrue();
        File.Exists(provider.FilePath).ShouldBeFalse();
        using var restored = await client.PostAsJsonAsync("/api/setup/complete", AuthTestExtensions.SetupRequest(), Ct);
        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<SetupStatusDto> GetStatusAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/setup/status", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SetupStatusDto>(Ct))!;
    }
}

/// <summary>
/// Without <c>Setup:Code</c>: a random code, announced in <c>{DataPath}/setup-code.txt</c> until setup completes. Runs on a
/// second host over the same database and data folder with <c>Setup:Code</c> cleared (no extra database).
/// </summary>
[Collection(AuthCollection.Name)]
public sealed class GeneratedSetupCodeTests(AuthApiFactory factory) : IAsyncLifetime
{
    private const string CodePattern = "^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$";

    private WebApplicationFactory<Program> _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CodeFile => Path.Combine(factory.DataPath, SetupCodeProvider.FileName);

    public async ValueTask InitializeAsync()
    {
        _host = factory.WithWebHostBuilder(builder => builder.UseSetting("Setup:Code", string.Empty));
        await _host.WaitUntilReadyAsync(ControlHomeApiFactory.ReadyTimeout);

        // Let the start-up announcement finish first, so it cannot act on the state from before the reset.
        var announcer = _host.Services.GetServices<IHostedService>().OfType<SetupCodeAnnouncer>().Single();
        await announcer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await _host.ResetAppAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Generated_code_is_written_to_the_data_folder_and_deleted_when_setup_completes()
    {
        var code = (await File.ReadAllTextAsync(CodeFile, Ct)).Trim();
        code.ShouldMatch(CodePattern);
        _host.Services.GetRequiredService<ISetupCodeProvider>().Current.ShouldBe(code);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(CodeFile).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using var admin = await _host.CompleteSetupAsync(AuthTestExtensions.SetupRequest(code.ToLowerInvariant()), Ct);

        File.Exists(CodeFile).ShouldBeFalse();
    }

    [Fact]
    public async Task Test_reset_generates_a_fresh_code()
    {
        var before = _host.Services.GetRequiredService<ISetupCodeProvider>().Current;

        await _host.ResetAppAsync(Ct);

        var after = (await File.ReadAllTextAsync(CodeFile, Ct)).Trim();
        after.ShouldMatch(CodePattern);
        after.ShouldNotBe(before);
    }
}
