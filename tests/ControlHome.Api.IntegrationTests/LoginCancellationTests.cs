using ControlHome.Application.Auth;
using ControlHome.Application.Identity;
using ControlHome.Infrastructure.Persistence;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests;

/// <summary>
/// A client that disconnects while its password is being checked (the request-abort token fires) is still counted towards
/// the lockout and audited: otherwise aborting every attempt mid-check would allow unlimited, unaudited guessing. A host on
/// the same database cancels the token from inside the password hasher, i.e. exactly during the check.
/// </summary>
[Collection(AuthCollection.Name)]
public sealed class LoginCancellationTests(AuthApiFactory factory) : IAsyncLifetime
{
    private readonly PasswordCheckSignal _signal = new();
    private WebApplicationFactory<Program> _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(_signal);
            services.AddScoped<IPasswordHasher<AppUser>, SignallingPasswordHasher>();
        }));
        await _host.WaitUntilReadyAsync(ControlHomeApiFactory.ReadyTimeout);
        await _host.ResetAppAsync(Ct);
        (await _host.CompleteSetupAsync(cancellationToken: Ct)).Dispose();
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_wrong_password_is_counted_and_audited_even_if_the_client_disconnects_during_the_check()
    {
        using var aborted = new CancellationTokenSource();
        _signal.OnVerify = aborted.Cancel;
        try
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var auth = scope.ServiceProvider.GetRequiredService<AuthService>();

            var result = await auth.LoginAsync(new LoginRequest(AuthTestExtensions.AdminUserName, "wrong password!", false), aborted.Token);

            aborted.IsCancellationRequested.ShouldBeTrue();
            result.IsFailure.ShouldBeTrue();
            result.Error.ShouldNotBeNull().Code.ShouldBe("auth.invalid_credentials");
        }
        finally
        {
            _signal.OnVerify = null;
        }

        await using var check = _host.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<ControlHomeDbContext>();
        (await db.Users.AsNoTracking().SingleAsync(Ct)).AccessFailedCount.ShouldBe(1);
        (await db.AuditLog.AsNoTracking().CountAsync(e => e.Action == "auth.login.failed", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_client_that_is_already_gone_gets_no_password_check()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var checks = 0;
        _signal.OnVerify = () => checks++;
        try
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var auth = scope.ServiceProvider.GetRequiredService<AuthService>();

            await Should.ThrowAsync<OperationCanceledException>(() =>
                auth.LoginAsync(new LoginRequest(AuthTestExtensions.AdminUserName, "wrong password!", false), aborted.Token));
        }
        finally
        {
            _signal.OnVerify = null;
        }

        checks.ShouldBe(0);
    }

    /// <summary>What to do when a password is verified (set per test).</summary>
    private sealed class PasswordCheckSignal
    {
        public Action? OnVerify { get; set; }
    }

    /// <summary>Identity's hasher, raising <see cref="PasswordCheckSignal.OnVerify"/> before each verification.</summary>
    private sealed class SignallingPasswordHasher(PasswordCheckSignal signal) : IPasswordHasher<AppUser>
    {
        private readonly PasswordHasher<AppUser> _inner = new();

        public string HashPassword(AppUser user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
        {
            signal.OnVerify?.Invoke();
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
