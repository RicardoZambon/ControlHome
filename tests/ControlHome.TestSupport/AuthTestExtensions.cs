using System.Net;
using System.Net.Http.Json;
using ControlHome.Application.Auth;
using ControlHome.Application.Setup;
using ControlHome.Application.Users;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ControlHome.TestSupport;

/// <summary>Setup, sign-in and user helpers for integration tests (Testing environment only: uses <c>/api/_test/reset</c>).</summary>
public static class AuthTestExtensions
{
    public const string AdminUserName = "admin";
    public const string AdminPassword = "correct horse battery";

    /// <summary>A valid first-run request (home in São Paulo, pt-BR, BRL).</summary>
    public static CompleteSetupRequest SetupRequest(
        string setupCode = ControlHomeApiFactory.SetupCode,
        string userName = AdminUserName,
        string password = AdminPassword) =>
        new(
            setupCode,
            new AdminAccountInput(userName, "Administrator", password),
            new HomeInput("Casa", "America/Sao_Paulo", -23.55, -46.63, "pt-BR", "BRL", 0.95m));

    /// <summary><c>POST /api/_test/reset</c>: empty database (roles kept) and the setup code back to its startup value.</summary>
    public static async Task ResetAppAsync(this WebApplicationFactory<Program> factory, CancellationToken cancellationToken = default)
    {
        using var client = factory.CreateApiClient();
        using var response = await client.PostAsync(new Uri("/api/_test/reset", UriKind.Relative), content: null, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException($"POST /api/_test/reset answered {(int)response.StatusCode}.");
        }
    }

    /// <summary>Completes setup and returns a client signed in as the new admin.</summary>
    public static async Task<HttpClient> CompleteSetupAsync(
        this WebApplicationFactory<Program> factory,
        CompleteSetupRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var client = factory.CreateApiClient();
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/setup/complete", UriKind.Relative),
            request ?? SetupRequest(),
            cancellationToken);
        await EnsureAsync(response, HttpStatusCode.OK, "POST /api/setup/complete", cancellationToken);
        return client;
    }

    /// <summary>A new client signed in as <paramref name="userName"/>.</summary>
    public static async Task<HttpClient> LoginAsync(
        this WebApplicationFactory<Program> factory,
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var client = factory.CreateApiClient();
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new LoginRequest(userName, password, RememberMe: false),
            cancellationToken);
        await EnsureAsync(response, HttpStatusCode.OK, "POST /api/auth/login", cancellationToken);
        return client;
    }

    /// <summary>Creates a user through <c>POST /api/users</c> with an Admin client.</summary>
    public static async Task<UserDto> CreateUserAsync(
        this HttpClient adminClient,
        string userName,
        string password,
        string role,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adminClient);
        using var response = await adminClient.PostAsJsonAsync(
            new Uri("/api/users", UriKind.Relative),
            new CreateUserRequest(userName, $"{userName} display", password, role),
            cancellationToken);
        await EnsureAsync(response, HttpStatusCode.Created, "POST /api/users", cancellationToken);
        return (await response.Content.ReadFromJsonAsync<UserDto>(cancellationToken))!;
    }

    private static async Task EnsureAsync(HttpResponseMessage response, HttpStatusCode expected, string request, CancellationToken cancellationToken)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"{request} answered {(int)response.StatusCode}: {body}");
        }
    }
}
