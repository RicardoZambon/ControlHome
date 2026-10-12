using System.Net;
using System.Net.Http.Json;
using ControlHome.Application.Auth;
using ControlHome.Application.Users;
using ControlHome.Domain.Security;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Api.IntegrationTests;

/// <summary>
/// The guarantees that rest on application locks: the last-active-Admin rule under concurrent changes (lock
/// <c>ControlHome.Users</c>) and the counting of concurrent failed sign-ins (lock <c>ControlHome.Login.{id}</c>).
/// </summary>
[Collection(AuthCollection.Name)]
public sealed class UserAndLoginConcurrencyTests(AuthApiFactory factory)
    : ResetAppTestBase<AuthApiFactory>(factory)
{
    private const int Rounds = 3;
    private const string Password = "a long enough password";

    [Fact]
    public async Task Two_admins_disabling_each_other_at_once_leave_exactly_one_active_admin()
    {
        for (var round = 0; round < Rounds; round++)
        {
            if (round > 0)
            {
                await Factory.ResetAppAsync(Ct);
            }

            using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
            var adminId = (await admin.GetFromJsonAsync<MeDto>("/api/auth/me", Ct))!.Id;
            var second = await admin.CreateUserAsync("second", Password, Roles.Admin, Ct);
            using var secondClient = await Factory.LoginAsync("second", Password, Ct);
            using var start = new SemaphoreSlim(0, 2);

            var requests = new[]
            {
                PutWhenReleasedAsync(admin, $"/api/users/{second.Id}", new UpdateUserRequest("second", Roles.Admin, true), start),
                PutWhenReleasedAsync(secondClient, $"/api/users/{adminId}", new UpdateUserRequest("Administrator", Roles.Admin, true), start),
            };
            start.Release(2);
            var responses = await Task.WhenAll(requests);
            try
            {
                // The first change wins; the other is refused, either by the last-admin rule or because its caller has
                // just been disabled (inside the lock the caller is re-checked first).
                responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1, $"round {round}");
                var refused = responses.Single(r => r.StatusCode != HttpStatusCode.OK);
                refused.StatusCode.ShouldBeOneOf(HttpStatusCode.Conflict, HttpStatusCode.Forbidden);
                (await refused.ReadProblemCodeAsync(Ct)).ShouldBeOneOf("users.last_admin", "auth.forbidden");

                var activeAdmins = await QueryDbAsync(db =>
                    (from user in db.Users
                     join userRole in db.UserRoles on user.Id equals userRole.UserId
                     join role in db.Roles on userRole.RoleId equals role.Id
                     where role.Name == Roles.Admin && !user.IsDisabled
                     select user.Id).CountAsync(Ct));
                activeAdmins.ShouldBe(1, $"round {round}");
                (await AuditEntriesAsync("users.updated")).Count.ShouldBe(1);
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task Concurrent_wrong_passwords_are_all_counted_and_lock_the_account_once()
    {
        (await Factory.CompleteSetupAsync(cancellationToken: Ct)).Dispose();
        const int Attempts = 5;
        var clients = Enumerable.Range(0, Attempts).Select(_ => Factory.CreateApiClient()).ToArray();
        using var start = new SemaphoreSlim(0, Attempts);
        try
        {
            var requests = clients.Select(client => LoginWhenReleasedAsync(client, start)).ToArray();
            start.Release(Attempts);
            var responses = await Task.WhenAll(requests);
            try
            {
                responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).ShouldBe(Attempts - 1);
                responses.Count(r => r.StatusCode == (HttpStatusCode)423).ShouldBe(1);
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            var user = await QueryDbAsync(db => db.Users.AsNoTracking().SingleAsync(Ct));
            user.AccessFailedCount.ShouldBe(0);
            user.ConsecutiveLockouts.ShouldBe(1);
            user.LockoutEnd.ShouldNotBeNull();
            (await AuditEntriesAsync("auth.login.failed")).Count.ShouldBe(Attempts);
            (await AuditEntriesAsync("auth.login.locked_out")).ShouldHaveSingleItem();
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    private static async Task<HttpResponseMessage> PutWhenReleasedAsync(
        HttpClient client,
        string path,
        UpdateUserRequest request,
        SemaphoreSlim start)
    {
        await start.WaitAsync(Ct);
        return await client.PutAsJsonAsync(path, request, Ct);
    }

    private static async Task<HttpResponseMessage> LoginWhenReleasedAsync(HttpClient client, SemaphoreSlim start)
    {
        await start.WaitAsync(Ct);
        return await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(AuthTestExtensions.AdminUserName, "definitely wrong", false),
            Ct);
    }
}
