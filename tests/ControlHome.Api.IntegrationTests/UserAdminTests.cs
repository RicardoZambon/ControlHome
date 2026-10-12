using System.Net;
using System.Net.Http.Json;
using ControlHome.Application.Auth;
using ControlHome.Application.Users;
using ControlHome.Domain.Security;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;

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
    public async Task The_last_active_admin_cannot_be_deleted()
    {
        // admin is disabled by "second" (allowed: second stays active), then admin, whose cookie is still valid until the
        // next security-stamp check, tries to delete second, the last active Admin.
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var adminId = (await admin.GetFromJsonAsync<MeDto>("/api/auth/me", Ct))!.Id;
        var second = await admin.CreateUserAsync("second", Password, Roles.Admin, Ct);
        using var secondClient = await Factory.LoginAsync("second", Password, Ct);
        using (var disable = await secondClient.PutAsJsonAsync($"/api/users/{adminId}", new UpdateUserRequest("Administrator", Roles.Admin, true), Ct))
        {
            disable.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var response = await admin.DeleteAsync($"/api/users/{second.Id}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("users.last_admin");
        (await QueryDbAsync(db => db.Users.CountAsync(Ct))).ShouldBe(2);
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
