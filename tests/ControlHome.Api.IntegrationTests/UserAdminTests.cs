using System.Net;
using System.Net.Http.Json;
using ControlHome.Api.Hosting;
using ControlHome.Application.Auth;
using ControlHome.Application.Users;
using ControlHome.Domain.Security;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests;

[Collection(AuthCollection.Name)]
public sealed class UserAdminTests(AuthApiFactory factory) : ResetAppTestBase<AuthApiFactory>(factory)
{
    private const string Password = "a long enough password";

    [Fact]
    public async Task Admin_creates_lists_updates_and_deletes_users_and_every_change_is_audited()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var created = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest("maria", "Maria", Password, Roles.Member), Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var maria = (await created.Content.ReadFromJsonAsync<UserDto>(Ct))!;
        created.Headers.Location!.ToString().ShouldBe($"/api/users/{maria.Id}");
        maria.UserName.ShouldBe("maria");
        maria.Role.ShouldBe(Roles.Member);
        maria.IsDisabled.ShouldBeFalse();

        var list = (await admin.GetFromJsonAsync<UserDto[]>("/api/users", Ct))!;
        list.Select(u => (u.UserName, u.Role)).ShouldBe([(AuthTestExtensions.AdminUserName, Roles.Admin), ("maria", Roles.Member)]);

        using (var updated = await admin.PutAsJsonAsync($"/api/users/{maria.Id}", new UpdateUserRequest("Maria Silva", Roles.Viewer, false), Ct))
        {
            updated.StatusCode.ShouldBe(HttpStatusCode.OK);
            var dto = (await updated.Content.ReadFromJsonAsync<UserDto>(Ct))!;
            dto.DisplayName.ShouldBe("Maria Silva");
            dto.Role.ShouldBe(Roles.Viewer);
        }

        (await admin.GetFromJsonAsync<UserDto[]>("/api/users", Ct))!.Single(u => u.Id == maria.Id).Role.ShouldBe(Roles.Viewer);

        using (var deleted = await admin.DeleteAsync($"/api/users/{maria.Id}", Ct))
        {
            deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await admin.GetFromJsonAsync<UserDto[]>("/api/users", Ct))!.Select(u => u.UserName).ShouldBe([AuthTestExtensions.AdminUserName]);
        (await AuditEntriesAsync("users.created")).ShouldHaveSingleItem().TargetId.ShouldBe(maria.Id.ToString());
        (await AuditEntriesAsync("users.updated")).ShouldHaveSingleItem().ActorName.ShouldBe(AuthTestExtensions.AdminUserName);
        (await AuditEntriesAsync("users.deleted")).ShouldHaveSingleItem().TargetId.ShouldBe(maria.Id.ToString());
    }

    [Fact]
    public async Task Created_user_can_sign_in_with_the_role_given()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        await admin.CreateUserAsync("viewer", Password, Roles.Viewer, Ct);

        using var viewer = await Factory.LoginAsync("viewer", Password, Ct);

        var me = (await viewer.GetFromJsonAsync<MeDto>("/api/auth/me", Ct))!;
        me.Roles.ShouldBe([Roles.Viewer]);
        me.Locale.ShouldBe("pt-BR"); // new users start in the home's language
    }

    [Fact]
    public async Task Duplicate_user_name_is_409_ignoring_case()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var response = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest("ADMIN", "Other", Password, Roles.Member), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("users.duplicate_username");
    }

    [Fact]
    public async Task Invalid_role_is_400_validation_failed()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var response = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest("joao", "João", Password, "Owner"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync(Ct)).GetProperty("errors").TryGetProperty("role", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData(Roles.Member)]
    [InlineData(Roles.Viewer)]
    public async Task Non_admins_get_403_on_users(string role)
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        await admin.CreateUserAsync("someone", Password, role, Ct);
        using var client = await Factory.LoginAsync("someone", Password, Ct);

        using var list = await client.GetAsync("/api/users", Ct);
        using var create = await client.PostAsJsonAsync("/api/users", new CreateUserRequest("other", "Other", Password, Roles.Admin), Ct);

        list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await list.ReadProblemCodeAsync(Ct)).ShouldBe("auth.forbidden");
        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await QueryDbAsync(db => db.Users.CountAsync(Ct))).ShouldBe(2);
    }

    [Fact]
    public async Task Unknown_user_is_404_users_not_found()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var id = Guid.NewGuid();

        using var update = await admin.PutAsJsonAsync($"/api/users/{id}", new UpdateUserRequest("X", Roles.Member, false), Ct);
        using var reset = await admin.PostAsJsonAsync($"/api/users/{id}/password", new ResetPasswordRequest(Password), Ct);
        using var delete = await admin.DeleteAsync($"/api/users/{id}", Ct);

        foreach (var response in new[] { update, reset, delete })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await response.ReadProblemCodeAsync(Ct)).ShouldBe("users.not_found");
        }
    }

    [Fact]
    public async Task The_only_admin_cannot_demote_or_disable_themselves()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var me = (await admin.GetFromJsonAsync<MeDto>("/api/auth/me", Ct))!;

        using var demote = await admin.PutAsJsonAsync($"/api/users/{me.Id}", new UpdateUserRequest("Administrator", Roles.Member, false), Ct);
        using var disable = await admin.PutAsJsonAsync($"/api/users/{me.Id}", new UpdateUserRequest("Administrator", Roles.Admin, true), Ct);

        demote.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await demote.ReadProblemCodeAsync(Ct)).ShouldBe("users.last_admin");
        disable.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await disable.ReadProblemCodeAsync(Ct)).ShouldBe("users.last_admin");
        (await admin.GetFromJsonAsync<UserDto[]>("/api/users", Ct))!.ShouldHaveSingleItem().Role.ShouldBe(Roles.Admin);
    }

    [Fact]
    public async Task Admin_cannot_delete_themselves()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        await admin.CreateUserAsync("second", Password, Roles.Admin, Ct);
        var me = (await admin.GetFromJsonAsync<MeDto>("/api/auth/me", Ct))!;

        using var response = await admin.DeleteAsync($"/api/users/{me.Id}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("users.cannot_delete_self");
    }

    [Fact]
    public async Task A_disabled_admin_cannot_delete_the_last_active_admin()
    {
        // admin is disabled by "second" (allowed: second stays active), then admin, whose cookie is still valid until the
        // next security-stamp check, tries to delete second, the last active Admin: refused before anything happens.
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var adminId = (await admin.GetFromJsonAsync<MeDto>("/api/auth/me", Ct))!.Id;
        var second = await admin.CreateUserAsync("second", Password, Roles.Admin, Ct);
        using var secondClient = await Factory.LoginAsync("second", Password, Ct);
        using (var disable = await secondClient.PutAsJsonAsync($"/api/users/{adminId}", new UpdateUserRequest("Administrator", Roles.Admin, true), Ct))
        {
            disable.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var response = await admin.DeleteAsync($"/api/users/{second.Id}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("auth.forbidden");
        (await QueryDbAsync(db => db.Users.CountAsync(Ct))).ShouldBe(2);
    }

    [Theory]
    [InlineData(true)] // demoted to Member
    [InlineData(false)] // disabled
    public async Task A_demoted_or_disabled_admins_existing_session_cannot_act_as_admin_or_restore_itself(bool demote)
    {
        // "second" keeps a valid cookie with the Admin role claim until the next security-stamp check (up to a minute).
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var adminId = (await admin.GetFromJsonAsync<MeDto>("/api/auth/me", Ct))!.Id;
        var second = await admin.CreateUserAsync("second", Password, Roles.Admin, Ct);
        using var stale = await Factory.LoginAsync("second", Password, Ct);
        var change = demote ? new UpdateUserRequest("second", Roles.Member, false) : new UpdateUserRequest("second", Roles.Admin, true);
        using (var changed = await admin.PutAsJsonAsync($"/api/users/{second.Id}", change, Ct))
        {
            changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var restore = await stale.PutAsJsonAsync($"/api/users/{second.Id}", new UpdateUserRequest("second", Roles.Admin, false), Ct);
        using var create = await stale.PostAsJsonAsync("/api/users", new CreateUserRequest("backdoor", "Backdoor", Password, Roles.Admin), Ct);
        using var takeOver = await stale.PostAsJsonAsync($"/api/users/{adminId}/password", new ResetPasswordRequest("taken over password"), Ct);
        using var delete = await stale.DeleteAsync($"/api/users/{adminId}", Ct);
        using var list = await stale.GetAsync("/api/users", Ct);
        using var home = await stale.PutAsJsonAsync("/api/home", new Application.Homes.UpdateHomeRequest("Mine", "UTC", null, null, "en", "USD", null), Ct);
        using var audit = await stale.GetAsync("/api/audit", Ct);

        foreach (var response in new[] { restore, create, takeOver, delete, list, home, audit })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{response.RequestMessage!.Method} {response.RequestMessage.RequestUri}");
            (await response.ReadProblemCodeAsync(Ct)).ShouldBe("auth.forbidden");
        }

        var users = (await admin.GetFromJsonAsync<UserDto[]>("/api/users", Ct))!;
        users.Select(u => u.UserName).ShouldBe([AuthTestExtensions.AdminUserName, "second"]);
        var secondNow = users.Single(u => u.Id == second.Id);
        secondNow.Role.ShouldBe(demote ? Roles.Member : Roles.Admin);
        secondNow.IsDisabled.ShouldBe(!demote);
        (await Factory.LoginAsync(AuthTestExtensions.AdminUserName, AuthTestExtensions.AdminPassword, Ct)).Dispose(); // unchanged
    }

    [Fact]
    public async Task User_name_with_a_trailing_newline_is_400_on_the_user_name()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var response = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest("maria\n", "Maria", Password, Roles.Member), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync(Ct)).GetProperty("errors").EnumerateObject().Select(p => p.Name).ShouldBe(["userName"]);
    }

    [Fact]
    public async Task With_two_active_admins_one_can_be_demoted()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var second = await admin.CreateUserAsync("second", Password, Roles.Admin, Ct);

        using var response = await admin.PutAsJsonAsync($"/api/users/{second.Id}", new UpdateUserRequest("second", Roles.Member, false), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<UserDto>(Ct))!.Role.ShouldBe(Roles.Member);
    }

    [Fact]
    public async Task Admin_password_reset_replaces_the_password_and_clears_a_lockout()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var member = await admin.CreateUserAsync("member", Password, Roles.Member, Ct);
        await UpdateDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == member.Id, Ct);
            user.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(30);
            user.ConsecutiveLockouts = 2;
        });

        using var reset = await admin.PostAsJsonAsync($"/api/users/{member.Id}/password", new ResetPasswordRequest("the new member password"), Ct);

        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var client = Factory.CreateApiClient();
        using (var old = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("member", Password, false), Ct))
        {
            old.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var fresh = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("member", "the new member password", false), Ct))
        {
            fresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await AuditEntriesAsync("users.password_reset")).ShouldHaveSingleItem().TargetId.ShouldBe(member.Id.ToString());
    }

    [Fact]
    public async Task Short_new_password_is_400_validation_failed()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var member = await admin.CreateUserAsync("member", Password, Roles.Member, Ct);

        using var response = await admin.PostAsJsonAsync($"/api/users/{member.Id}/password", new ResetPasswordRequest("short"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("validation.failed");
    }
}

/// <summary>
/// The users service re-checks the caller under its lock even when authorization let the request through (the policy check
/// and the change are not atomic). Here the Admin policy's database check is switched off, so only the cookie's (stale) role
/// claim is checked before the service runs.
/// </summary>
[Collection(AuthCollection.Name)]
public sealed class UserAdminCallerRecheckTests(AuthApiFactory factory) : IAsyncLifetime
{
    private const string Password = "a long enough password";

    private WebApplicationFactory<Program> _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Remove(services.Single(d => d.ImplementationType == typeof(ActiveAdminHandler)));
            services.AddSingleton<IAuthorizationHandler, CookieOnlyAdminHandler>();
        }));
        await _host.WaitUntilReadyAsync(ControlHomeApiFactory.ReadyTimeout);
        await _host.ResetAppAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_demoted_admin_whose_request_passed_authorization_is_still_refused()
    {
        using var admin = await _host.CompleteSetupAsync(cancellationToken: Ct);
        var adminId = (await admin.GetFromJsonAsync<MeDto>("/api/auth/me", Ct))!.Id;
        var second = await admin.CreateUserAsync("second", Password, Roles.Admin, Ct);
        using var stale = await _host.LoginAsync("second", Password, Ct);
        using (var demote = await admin.PutAsJsonAsync($"/api/users/{second.Id}", new UpdateUserRequest("second", Roles.Member, false), Ct))
        {
            demote.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var restore = await stale.PutAsJsonAsync($"/api/users/{second.Id}", new UpdateUserRequest("second", Roles.Admin, false), Ct);
        using var create = await stale.PostAsJsonAsync("/api/users", new CreateUserRequest("backdoor", "Backdoor", Password, Roles.Admin), Ct);
        using var takeOver = await stale.PostAsJsonAsync($"/api/users/{adminId}/password", new ResetPasswordRequest("taken over password"), Ct);
        using var delete = await stale.DeleteAsync($"/api/users/{adminId}", Ct);

        foreach (var response in new[] { restore, create, takeOver, delete })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{response.RequestMessage!.Method} {response.RequestMessage.RequestUri}");
            (await response.ReadProblemCodeAsync(Ct)).ShouldBe("auth.forbidden");
        }

        var users = (await admin.GetFromJsonAsync<UserDto[]>("/api/users", Ct))!;
        users.Select(u => (u.UserName, u.Role)).ShouldBe([(AuthTestExtensions.AdminUserName, Roles.Admin), ("second", Roles.Member)]);
        (await _host.LoginAsync(AuthTestExtensions.AdminUserName, AuthTestExtensions.AdminPassword, Ct)).Dispose(); // unchanged
    }

    /// <summary>Accepts the Admin policy's database requirement unchecked (the role claim is still required).</summary>
    private sealed class CookieOnlyAdminHandler : AuthorizationHandler<ActiveAdminRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveAdminRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }
}
