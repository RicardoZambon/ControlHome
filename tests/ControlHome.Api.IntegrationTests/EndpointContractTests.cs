using System.Text.Json;
using ControlHome.Api.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests;

/// <summary>
/// The 0.2 route table: every route of Tasks 0.2-C/0.2-D exists with the agreed authorization and rate limiting, and the
/// committed OpenAPI document describes each of them.
/// </summary>
public sealed class EndpointContractTests(NoDatabaseApiFactory factory) : IClassFixture<NoDatabaseApiFactory>
{
    private const string Anonymous = "anonymous";
    private const string Authenticated = "authenticated";

    /// <summary>Method, route, operationId, authorization (anonymous | authenticated | policy), rate-limit policy.</summary>
    public static TheoryData<string, string, string, string, string?> Routes => new()
    {
        { "GET", "/api/setup/status", "getSetupStatus", Anonymous, null },
        { "POST", "/api/setup/complete", "completeSetup", Anonymous, RateLimitPolicies.Setup },
        { "POST", "/api/auth/login", "login", Anonymous, RateLimitPolicies.Login },
        { "POST", "/api/auth/logout", "logout", Anonymous, null },
        { "POST", "/api/auth/reset-password", "resetPasswordWithToken", Anonymous, RateLimitPolicies.Login },
        { "GET", "/api/auth/me", "getMe", Authenticated, null },
        { "PUT", "/api/auth/me/profile", "updateProfile", Authenticated, null },
        { "POST", "/api/auth/me/password", "changePassword", Authenticated, RateLimitPolicies.Login },
        { "GET", "/api/users", "listUsers", AuthorizationPolicies.Admin, null },
        { "POST", "/api/users", "createUser", AuthorizationPolicies.Admin, null },
        { "PUT", "/api/users/{id:guid}", "updateUser", AuthorizationPolicies.Admin, null },
        { "POST", "/api/users/{id:guid}/password", "resetUserPassword", AuthorizationPolicies.Admin, null },
        { "DELETE", "/api/users/{id:guid}", "deleteUser", AuthorizationPolicies.Admin, null },
        { "GET", "/api/home", "getHome", AuthorizationPolicies.Viewer, null },
        { "PUT", "/api/home", "updateHome", AuthorizationPolicies.Admin, null },
        { "GET", "/api/audit", "listAudit", AuthorizationPolicies.Admin, null },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public void Route_exists_with_its_authorization_and_rate_limit(
        string method,
        string route,
        string operationId,
        string authorization,
        string? rateLimitPolicy)
    {
        var endpoint = FindEndpoint(method, route);

        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>().ShouldNotBeNull().EndpointName.ShouldBe(operationId);
        var allowAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToArray();
        switch (authorization)
        {
            case Anonymous:
                allowAnonymous.ShouldBeTrue();
                break;
            case Authenticated:
                allowAnonymous.ShouldBeFalse();
                policies.ShouldNotBeEmpty();
                policies.ShouldAllBe(p => p == null);
                break;
            default:
                allowAnonymous.ShouldBeFalse();
                policies.ShouldContain(authorization);
                break;
        }

        // Both directions: a missing policy fails as well as an unexpected one.
        var rateLimit = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>();
        if (rateLimitPolicy is null)
        {
            rateLimit.ShouldBeNull();
        }
        else
        {
            rateLimit.ShouldNotBeNull().PolicyName.ShouldBe(rateLimitPolicy);
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Route_is_described_in_the_committed_openapi_document(
        string method,
        string route,
        string operationId,
        string authorization,
        string? rateLimitPolicy)
    {
        _ = (authorization, rateLimitPolicy);
        using var document = await OpenApiDocument.LoadAsync(TestContext.Current.CancellationToken);
        var path = route.Replace(":guid", string.Empty, StringComparison.Ordinal);

        document.RootElement.GetProperty("paths").TryGetProperty(path, out var item).ShouldBeTrue($"{path} missing from openapi.json");
        item.TryGetProperty(method.ToLowerInvariant(), out var operation).ShouldBeTrue($"{method} {path} missing from openapi.json");
        operation.GetProperty("operationId").GetString().ShouldBe(operationId);
    }

    [Fact]
    public async Task Admin_policy_requires_the_role_and_the_database_check()
    {
        var policy = await factory.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(AuthorizationPolicies.Admin);

        policy.ShouldNotBeNull();
        policy.Requirements.OfType<RolesAuthorizationRequirement>().ShouldHaveSingleItem().AllowedRoles.ShouldBe(["Admin"]);
        policy.Requirements.OfType<ActiveAdminRequirement>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Problem_details_schema_documents_code_and_trace_id()
    {
        using var document = await OpenApiDocument.LoadAsync(TestContext.Current.CancellationToken);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        foreach (var name in new[] { "ProblemDetails", "HttpValidationProblemDetails" })
        {
            var properties = schemas.GetProperty(name).GetProperty("properties");
            properties.GetProperty("code").GetProperty("type").GetString().ShouldBe("string");
            properties.TryGetProperty("traceId", out _).ShouldBeTrue();
            schemas.GetProperty(name).GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldContain("code");
        }
    }

    [Fact]
    public async Task Document_has_no_server_urls_and_only_api_paths()
    {
        using var document = await OpenApiDocument.LoadAsync(TestContext.Current.CancellationToken);

        if (document.RootElement.TryGetProperty("servers", out var servers))
        {
            servers.ValueKind.ShouldBe(JsonValueKind.Array);
            servers.GetArrayLength().ShouldBe(0);
        }

        document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ShouldAllBe(p => p.StartsWith("/api/"));
    }

    private RouteEndpoint FindEndpoint(string method, string route)
    {
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var matches = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => string.Equals("/" + e.RoutePattern.RawText?.Trim('/'), route, StringComparison.Ordinal))
            .Where(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true)
            .ToList();

        matches.Count.ShouldBe(1, $"{method} {route}");
        return matches[0];
    }
}
