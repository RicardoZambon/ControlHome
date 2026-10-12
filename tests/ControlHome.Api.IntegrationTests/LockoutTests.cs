using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlHome.Application.Auth;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Api.IntegrationTests;

/// <summary>5 failures → 423 auth.locked_out for 5 min × 2^ConsecutiveLockouts (max 1 h); success resets the escalation.</summary>
[Collection(AuthCollection.Name)]
public sealed class LockoutTests(AuthApiFactory factory) : ResetAppTestBase<AuthApiFactory>(factory)
{
    private const string WrongPassword = "definitely wrong";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (await Factory.CompleteSetupAsync(cancellationToken: Ct)).Dispose();
    }

    [Fact]
    public async Task Five_failures_lock_the_account_for_five_minutes()
    {
        using var client = Factory.CreateApiClient();

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var failed = await LoginAsync(client, WrongPassword);
            failed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"attempt {attempt}");
        }

        using (var fifth = await LoginAsync(client, WrongPassword))
        {
            fifth.StatusCode.ShouldBe((HttpStatusCode)423);
            var problem = await fifth.ReadJsonAsync(Ct);
            problem.GetProperty("code").GetString().ShouldBe("auth.locked_out");
            problem.GetProperty("retryAfterSeconds").GetInt32().ShouldBeInRange(290, 300);
        }

        // Even the right password is refused while locked out.
        using (var correct = await LoginAsync(client, AuthTestExtensions.AdminPassword))
        {
            correct.StatusCode.ShouldBe((HttpStatusCode)423);
            (await correct.ReadProblemCodeAsync(Ct)).ShouldBe("auth.locked_out");
            correct.Headers.Contains("Set-Cookie").ShouldBeFalse();
        }

        var user = await QueryDbAsync(db => db.Users.AsNoTracking().SingleAsync(Ct));
        user.ConsecutiveLockouts.ShouldBe(1);
        user.AccessFailedCount.ShouldBe(0);
        user.LockoutEnd.ShouldNotBeNull();

        var lockedOut = (await AuditEntriesAsync("auth.login.locked_out")).ShouldHaveSingleItem();
        using var details = JsonDocument.Parse(lockedOut.DetailsJson!);
        details.RootElement.GetProperty("lockoutSeconds").GetInt32().ShouldBe(300);
        (await AuditEntriesAsync("auth.login.failed")).Count.ShouldBe(6); // 5 wrong passwords + 1 attempt while locked
    }

    [Theory]
    [InlineData(1, 600)]
    [InlineData(2, 1200)]
    [InlineData(3, 2400)]
    [InlineData(4, 3600)] // 80 min, capped at 1 h
    [InlineData(10, 3600)]
    public async Task Lockout_escalates_with_consecutive_lockouts_up_to_one_hour(int previousLockouts, int expectedSeconds)
    {
        await UpdateDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(Ct);
            user.ConsecutiveLockouts = previousLockouts;
        });
        using var client = Factory.CreateApiClient();

        HttpResponseMessage? last = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            last?.Dispose();
            last = await LoginAsync(client, WrongPassword);
        }

        using (last)
        {
            last!.StatusCode.ShouldBe((HttpStatusCode)423);
            (await last.ReadJsonAsync(Ct)).GetProperty("retryAfterSeconds").GetInt32().ShouldBeInRange(expectedSeconds - 10, expectedSeconds);
        }

        (await QueryDbAsync(db => db.Users.AsNoTracking().SingleAsync(Ct))).ConsecutiveLockouts.ShouldBe(previousLockouts + 1);
    }

    [Fact]
    public async Task After_the_lockout_expires_a_successful_sign_in_resets_the_escalation()
    {
        await UpdateDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(Ct);
            user.ConsecutiveLockouts = 3;
            user.AccessFailedCount = 2;
            user.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(-1);
        });
        using var client = Factory.CreateApiClient();

        using var response = await LoginAsync(client, AuthTestExtensions.AdminPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var user = await QueryDbAsync(db => db.Users.AsNoTracking().SingleAsync(Ct));
        user.ConsecutiveLockouts.ShouldBe(0);
        user.AccessFailedCount.ShouldBe(0);
        user.LockoutEnd.ShouldBeNull();
    }

    [Fact]
    public async Task A_success_before_the_fifth_failure_resets_the_failure_count()
    {
        using var client = Factory.CreateApiClient();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var failed = await LoginAsync(client, WrongPassword);
            failed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var success = await LoginAsync(client, AuthTestExtensions.AdminPassword))
        {
            success.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var afterSuccess = await LoginAsync(client, WrongPassword);
        afterSuccess.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await QueryDbAsync(db => db.Users.AsNoTracking().SingleAsync(Ct))).AccessFailedCount.ShouldBe(1);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest(AuthTestExtensions.AdminUserName, password, false), Ct);
}
