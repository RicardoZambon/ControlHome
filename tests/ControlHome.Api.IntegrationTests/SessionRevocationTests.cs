using System.Net;
using System.Net.Http.Json;
using ControlHome.Application.Users;
using ControlHome.Domain.Security;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests;

/// <summary>
/// Review focus 2: a disabled user's existing cookie stops working at the next security-stamp check. The production
/// interval is one minute; here a host on the same database validates the stamp on every request.
/// </summary>
[Collection(AuthCollection.Name)]
public sealed class SessionRevocationTests(AuthApiFactory factory) : IAsyncLifetime
{
    private const string Password = "member password 1";

    private WebApplicationFactory<Program> _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero)));
        await _host.WaitUntilReadyAsync(ControlHomeApiFactory.ReadyTimeout);
        await _host.ResetAppAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Disabled_users_existing_session_is_rejected_at_the_next_request()
    {
        using var admin = await _host.CompleteSetupAsync(cancellationToken: Ct);
        var member = await admin.CreateUserAsync("member", Password, Roles.Member, Ct);
        using var memberClient = await _host.LoginAsync("member", Password, Ct);
        using (var before = await memberClient.GetAsync("/api/home", Ct))
        {
            before.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var disable = await admin.PutAsJsonAsync($"/api/users/{member.Id}", new UpdateUserRequest(member.DisplayName, Roles.Member, IsDisabled: true), Ct))
        {
            disable.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var me = await memberClient.GetAsync("/api/auth/me", Ct);
        me.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await me.ReadProblemCodeAsync(Ct)).ShouldBe("auth.required");
        using var home = await memberClient.GetAsync("/api/home", Ct);
        home.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The admin's own session is unaffected.
        using var adminMe = await admin.GetAsync("/api/auth/me", Ct);
        adminMe.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Role_change_ends_the_users_existing_session()
    {
        using var admin = await _host.CompleteSetupAsync(cancellationToken: Ct);
        var member = await admin.CreateUserAsync("member", Password, Roles.Member, Ct);
        using var memberClient = await _host.LoginAsync("member", Password, Ct);

        using (var demote = await admin.PutAsJsonAsync($"/api/users/{member.Id}", new UpdateUserRequest(member.DisplayName, Roles.Viewer, false), Ct))
        {
            demote.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var me = await memberClient.GetAsync("/api/auth/me", Ct);
        me.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Own_password_change_keeps_the_session_but_ends_the_others()
    {
        using var admin = await _host.CompleteSetupAsync(cancellationToken: Ct);
        using var otherBrowser = await _host.LoginAsync(AuthTestExtensions.AdminUserName, AuthTestExtensions.AdminPassword, Ct);

        using (var change = await admin.PostAsJsonAsync(
                   "/api/auth/me/password",
                   new Application.Auth.ChangePasswordRequest(AuthTestExtensions.AdminPassword, "another long password"),
                   Ct))
        {
            change.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var same = await admin.GetAsync("/api/auth/me", Ct);
        same.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var other = await otherBrowser.GetAsync("/api/auth/me", Ct);
        other.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
