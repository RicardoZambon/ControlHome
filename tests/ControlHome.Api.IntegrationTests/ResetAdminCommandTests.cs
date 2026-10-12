using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlHome.Api.Features.Auth;
using ControlHome.Application.Auth;
using ControlHome.Domain.Auditing;
using ControlHome.Domain.Security;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Api.IntegrationTests;

/// <summary><c>reset-admin &lt;user&gt;</c> (run in-process against the factory's database and data folder) and <c>POST /api/auth/reset-password</c>.</summary>
[Collection(AuthCollection.Name)]
public sealed class ResetAdminCommandTests(AuthApiFactory factory)
    : ResetAppTestBase<AuthApiFactory>(factory)
{
    private const string NewPassword = "recovered admin password";

    [Fact]
    public async Task Printed_link_sets_a_new_password_once_and_clears_the_lockout()
    {
        (await Factory.CompleteSetupAsync(cancellationToken: Ct)).Dispose();
        await UpdateDbAsync(async db => (await db.Users.SingleAsync(Ct)).LockoutEnd = DateTimeOffset.UtcNow.AddHours(1));

        var (exitCode, output, _) = await RunCommandAsync(ResetAdminCommand.Name, AuthTestExtensions.AdminUserName);

        exitCode.ShouldBe(ResetAdminCommand.ExitSuccess);
        var path = output.Trim();
        path.ShouldStartWith($"/reset-password?user={AuthTestExtensions.AdminUserName}&token=");
        output.Trim().Split('\n').ShouldHaveSingleItem(); // the path is the only line on standard output
        var token = Uri.UnescapeDataString(path[(path.IndexOf("&token=", StringComparison.Ordinal) + "&token=".Length)..]);

        using var client = Factory.CreateApiClient();
        var request = new ResetPasswordWithTokenRequest(AuthTestExtensions.AdminUserName, token, NewPassword);
        using (var reset = await client.PostAsJsonAsync("/api/auth/reset-password", request, Ct))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(AuthTestExtensions.AdminUserName, NewPassword, false), Ct))
        {
            login.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var reused = await client.PostAsJsonAsync("/api/auth/reset-password", request with { NewPassword = "yet another password" }, Ct))
        {
            reused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await reused.ReadProblemCodeAsync(Ct)).ShouldBe("auth.reset_invalid");
        }

        var audit = await AuditEntriesAsync("auth.admin_reset");
        audit.Count.ShouldBe(2);
        audit[0].ActorType.ShouldBe(AuditActorType.System);
        Step(audit[0]).ShouldBe("token_issued");
        Step(audit[1]).ShouldBe("password_reset");
    }

    [Fact]
    public void Reset_tokens_expire_after_fifteen_minutes()
    {
        Factory.Services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value.TokenLifespan
            .ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Invalid_token_is_400_reset_invalid()
    {
        (await Factory.CompleteSetupAsync(cancellationToken: Ct)).Dispose();
        using var client = Factory.CreateApiClient();

        using var forged = await client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new ResetPasswordWithTokenRequest(AuthTestExtensions.AdminUserName, "not-a-token", NewPassword),
            Ct);
        using var unknownUser = await client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new ResetPasswordWithTokenRequest("ghost", "not-a-token", NewPassword),
            Ct);

        forged.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await forged.ReadProblemCodeAsync(Ct)).ShouldBe("auth.reset_invalid");
        unknownUser.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await unknownUser.ReadProblemCodeAsync(Ct)).ShouldBe("auth.reset_invalid");
    }

    [Fact]
    public async Task Unknown_or_non_admin_users_exit_with_1_and_print_nothing()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        await admin.CreateUserAsync("member", "member password 1", Roles.Member, Ct);

        var unknown = await RunCommandAsync(ResetAdminCommand.Name, "ghost");
        var member = await RunCommandAsync(ResetAdminCommand.Name, "member");

        unknown.ExitCode.ShouldBe(ResetAdminCommand.ExitFailure);
        unknown.Output.ShouldBeEmpty();
        unknown.Error.ShouldContain("ghost");
        member.ExitCode.ShouldBe(ResetAdminCommand.ExitFailure);
        member.Output.ShouldBeEmpty();
        member.Error.ShouldContain("not an administrator");
        (await AuditEntriesAsync("auth.admin_reset")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_user_name_prints_usage_and_exits_with_2()
    {
        var missing = await RunCommandAsync(ResetAdminCommand.Name);
        var option = await RunCommandAsync(ResetAdminCommand.Name, "--verbose");

        foreach (var result in new[] { missing, option })
        {
            result.ExitCode.ShouldBe(ResetAdminCommand.ExitUsage);
            result.Output.ShouldBeEmpty();
            result.Error.ShouldContain("Usage");
        }
    }

    [Fact]
    public async Task Without_a_reachable_database_it_exits_with_1()
    {
        var result = await RunCommandWithSettingsAsync(
            [ResetAdminCommand.Name, AuthTestExtensions.AdminUserName],
            new Dictionary<string, string?> { ["ConnectionStrings:ControlHome"] = string.Empty });

        result.ExitCode.ShouldBe(ResetAdminCommand.ExitFailure);
        result.Error.ShouldContain("database is not available");
    }

    [Fact]
    public void Only_the_reset_admin_verb_is_dispatched_as_a_command()
    {
        ResetAdminCommand.Matches(["reset-admin", "admin"]).ShouldBeTrue();
        ResetAdminCommand.Matches([]).ShouldBeFalse();
        ResetAdminCommand.Matches(["--urls", "http://localhost:5080"]).ShouldBeFalse();
        ResetAdminCommand.Matches(["Reset-Admin", "admin"]).ShouldBeFalse();
    }

    private static string? Step(AuditLogEntry entry)
    {
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        return details.RootElement.GetProperty("step").GetString();
    }

    private Task<CommandResult> RunCommandAsync(params string[] args) => RunCommandWithSettingsAsync(args, overrides: null);

    private async Task<CommandResult> RunCommandWithSettingsAsync(string[] args, IDictionary<string, string?>? overrides)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:ControlHome"] = Factory.ConnectionString,
            ["ControlHome:DataPath"] = Factory.DataPath,
            ["Database:StartupTimeout"] = "00:00:05",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await ResetAdminCommand.RunAsync(
            args,
            output,
            error,
            builder =>
            {
                builder.Configuration.AddInMemoryCollection(settings);
                builder.Logging.ClearProviders();
            },
            Ct);
        return new CommandResult(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
