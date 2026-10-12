using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlHome.Application.Auth;
using ControlHome.Application.Users;
using ControlHome.Domain.Auditing;
using ControlHome.Domain.Security;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Api.IntegrationTests;

[Collection(AuthCollection.Name)]
public sealed class AuthTests(AuthApiFactory factory) : ResetAppTestBase<AuthApiFactory>(factory)
{
    private const string MemberPassword = "member password 1";

    [Fact]
    public async Task Login_returns_me_sets_the_session_cookie_and_is_audited()
    {
        (await Factory.CompleteSetupAsync(cancellationToken: Ct)).Dispose();
        using var client = Factory.CreateApiClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(AuthTestExtensions.AdminUserName, AuthTestExtensions.AdminPassword, RememberMe: true),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = (await response.Content.ReadFromJsonAsync<MeDto>(Ct))!;
        me.UserName.ShouldBe(AuthTestExtensions.AdminUserName);
        me.Roles.ShouldBe([Roles.Admin]);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ControlHome.Session=", StringComparison.Ordinal));
        cookie.ShouldContain("expires=", Case.Insensitive); // RememberMe: persistent cookie
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);

        using var meResponse = await client.GetAsync("/api/auth/me", Ct);
        meResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var user = await QueryDbAsync(db => db.Users.AsNoTracking().SingleAsync(Ct));
        user.LastLoginAtUtc.ShouldNotBeNull();
        var audit = (await AuditEntriesAsync("auth.login.succeeded")).ShouldHaveSingleItem();
        audit.ActorType.ShouldBe(AuditActorType.User);
        audit.ActorId.ShouldBe(me.Id.ToString());
        audit.ActorName.ShouldBe(AuthTestExtensions.AdminUserName);
    }

    [Fact]
    public async Task Session_cookie_without_remember_me_is_a_browser_session_cookie()
    {
        (await Factory.CompleteSetupAsync(cancellationToken: Ct)).Dispose();
        using var client = Factory.CreateApiClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(AuthTestExtensions.AdminUserName, AuthTestExtensions.AdminPassword, RememberMe: false),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ControlHome.Session=", StringComparison.Ordinal))
            .ShouldNotContain("expires=", Case.Insensitive);
    }

    [Theory]
    [InlineData(AuthTestExtensions.AdminUserName, "wrong password!")]
    [InlineData("nobody", AuthTestExtensions.AdminPassword)]
    public async Task Bad_credentials_are_401_invalid_credentials_and_audited(string userName, string password)
    {
        (await Factory.CompleteSetupAsync(cancellationToken: Ct)).Dispose();
        using var client = Factory.CreateApiClient();

        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password, false), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("auth.invalid_credentials");
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        var audit = (await AuditEntriesAsync("auth.login.failed")).ShouldHaveSingleItem();
        audit.ActorType.ShouldBe(AuditActorType.Anonymous);
        using var details = JsonDocument.Parse(audit.DetailsJson!);
        details.RootElement.GetProperty("userName").GetString().ShouldBe(userName);
        details.RootElement.GetProperty("reason").GetString().ShouldBe(userName == "nobody" ? "unknown_user" : "invalid_password");
    }

    [Fact]
    public async Task Me_is_401_auth_required_when_anonymous()
    {
        using var client = Factory.CreateApiClient();

        using var response = await client.GetAsync("/api/auth/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("auth.required");
    }

    [Fact]
    public async Task Logout_ends_the_session_and_is_audited()
    {
        using var client = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var logout = await client.PostAsync("/api/auth/logout", content: null, Ct);

        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var me = await client.GetAsync("/api/auth/me", Ct);
        me.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuditEntriesAsync("auth.logout")).ShouldHaveSingleItem().ActorName.ShouldBe(AuthTestExtensions.AdminUserName);
    }

    [Fact]
    public async Task Logout_when_anonymous_is_still_204()
    {
        using var client = Factory.CreateApiClient();

        using var logout = await client.PostAsync("/api/auth/logout", content: null, Ct);

        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Profile_update_persists()
    {
        using var client = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var response = await client.PutAsJsonAsync("/api/auth/me/profile", new UpdateProfileRequest("Dona da Casa", "en", "dark"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<MeDto>(Ct))!;
        updated.DisplayName.ShouldBe("Dona da Casa");
        updated.Locale.ShouldBe("en");
        updated.Theme.ShouldBe("dark");
        using var me = await client.GetAsync("/api/auth/me", Ct);
        (await me.Content.ReadFromJsonAsync<MeDto>(Ct))!.ShouldBeEquivalentTo(updated);
    }

    [Fact]
    public async Task Profile_update_with_unknown_theme_is_400_validation_failed()
    {
        using var client = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var response = await client.PutAsJsonAsync("/api/auth/me/profile", new UpdateProfileRequest("Admin", "en", "blue"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.ReadJsonAsync(Ct);
        problem.GetProperty("code").GetString().ShouldBe("validation.failed");
        problem.GetProperty("errors").TryGetProperty("theme", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Change_password_with_a_wrong_current_password_is_400_password_incorrect()
    {
        using var client = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var response = await client.PostAsJsonAsync(
            "/api/auth/me/password",
            new ChangePasswordRequest("not my password", "a brand new password"),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("auth.password_incorrect");
    }

    [Fact]
    public async Task Change_password_keeps_the_session_and_only_the_new_password_works()
    {
        using var client = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var response = await client.PostAsJsonAsync(
            "/api/auth/me/password",
            new ChangePasswordRequest(AuthTestExtensions.AdminPassword, "a brand new password"),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using (var me = await client.GetAsync("/api/auth/me", Ct))
        {
            me.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var anonymous = Factory.CreateApiClient();
        using (var old = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(AuthTestExtensions.AdminUserName, AuthTestExtensions.AdminPassword, false), Ct))
        {
            old.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var fresh = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(AuthTestExtensions.AdminUserName, "a brand new password", false), Ct))
        {
            fresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await AuditEntriesAsync("auth.password.changed")).ShouldHaveSingleItem().ActorName.ShouldBe(AuthTestExtensions.AdminUserName);
    }

    [Fact]
    public async Task Disabled_user_cannot_sign_in_403_auth_disabled()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var member = await admin.CreateUserAsync("member", MemberPassword, Roles.Member, Ct);
        using (var disable = await admin.PutAsJsonAsync($"/api/users/{member.Id}", new UpdateUserRequest(member.DisplayName, Roles.Member, IsDisabled: true), Ct))
        {
            disable.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var client = Factory.CreateApiClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("member", MemberPassword, false), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("auth.disabled");
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();

        // A wrong password does not reveal that the account is disabled.
        using var wrong = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("member", "not the password", false), Ct);
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task User_names_are_case_insensitive_at_sign_in()
    {
        (await Factory.CompleteSetupAsync(cancellationToken: Ct)).Dispose();

        using var client = await Factory.LoginAsync("ADMIN", AuthTestExtensions.AdminPassword, Ct);

        using var me = await client.GetAsync("/api/auth/me", Ct);
        (await me.Content.ReadFromJsonAsync<MeDto>(Ct))!.UserName.ShouldBe(AuthTestExtensions.AdminUserName);
    }
}
