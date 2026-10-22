using System.Text.Json;
using ControlHome.Api.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests;

/// <summary>
/// The route table: every route of Tasks 0.2-C/0.2-D and 1.1-A exists with the agreed authorization and rate limiting,
/// and the committed OpenAPI document describes each of them.
/// </summary>
public sealed class EndpointContractTests(NoDatabaseApiFactory factory) : IClassFixture<NoDatabaseApiFactory>
{
    private const string Anonymous = "anonymous";
    private const string Authenticated = "authenticated";

    /// <summary>Method, route, operationId, authorization (anonymous | authenticated | policy), rate-limit policy.</summary>
    public static TheoryData<string, string, string, string, string?> Routes => new()
    {
        { "GET", "/api/setup/status", "getSetupStatus", Anonymous, null },
        { "POST", "/api/setup/database", "configureDatabase", Anonymous, RateLimitPolicies.Setup },
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
        { "DELETE", "/api/users/{id:guid}/safety-pin", "resetUserSafetyPin", AuthorizationPolicies.Admin, null },
        { "GET", "/api/home", "getHome", AuthorizationPolicies.Viewer, null },
        { "PUT", "/api/home", "updateHome", AuthorizationPolicies.Admin, null },
        { "GET", "/api/audit", "listAudit", AuthorizationPolicies.Admin, null },
        { "GET", "/api/floors", "listFloors", AuthorizationPolicies.Viewer, null },
        { "GET", "/api/floors/{id:int}", "getFloor", AuthorizationPolicies.Viewer, null },
        { "POST", "/api/floors", "createFloor", AuthorizationPolicies.Admin, null },
        { "PUT", "/api/floors/{id:int}", "updateFloor", AuthorizationPolicies.Admin, null },
        { "DELETE", "/api/floors/{id:int}", "deleteFloor", AuthorizationPolicies.Admin, null },
        { "PUT", "/api/floors/{id:int}/layout", "saveFloorLayout", AuthorizationPolicies.Admin, null },
        { "GET", "/api/rooms", "listRooms", AuthorizationPolicies.Viewer, null },
        { "POST", "/api/files", "uploadFile", AuthorizationPolicies.Admin, null },
        { "GET", "/api/files/{id:guid}", "getFile", AuthorizationPolicies.Viewer, null },
        { "GET", "/api/auth/me/pin", "getSafetyPin", Authenticated, null },
        { "PUT", "/api/auth/me/pin", "setSafetyPin", Authenticated, RateLimitPolicies.Login },
        { "POST", "/api/auth/step-up", "stepUp", Authenticated, RateLimitPolicies.Login },
        { "GET", "/api/capabilities", "listCapabilities", AuthorizationPolicies.Viewer, null },
        { "GET", "/api/connectors", "listConnectors", AuthorizationPolicies.Viewer, null },
        { "GET", "/api/integrations", "listIntegrations", AuthorizationPolicies.Viewer, null },
        { "POST", "/api/integrations", "createIntegration", AuthorizationPolicies.Admin, null },
        { "DELETE", "/api/integrations/{id:guid}", "deleteIntegration", AuthorizationPolicies.Admin, null },
        { "GET", "/api/devices", "listDevices", AuthorizationPolicies.Viewer, null },
        { "GET", "/api/devices/{id:guid}", "getDevice", AuthorizationPolicies.Viewer, null },
        { "PUT", "/api/devices/{id:guid}", "updateDevice", AuthorizationPolicies.Admin, null },
        { "PUT", "/api/endpoints/{id:guid}", "updateEndpoint", AuthorizationPolicies.Admin, null },
        { "PUT", "/api/endpoints/{id:guid}/favorite", "setEndpointFavorite", AuthorizationPolicies.Member, null },
        { "POST", "/api/endpoints/{id:guid}/commands", "sendCommand", AuthorizationPolicies.Member, null },
        { "GET", "/api/state", "getState", AuthorizationPolicies.Viewer, null },
        { "GET", "/api/system/status", "getSystemStatus", AuthorizationPolicies.Viewer, null },
        { "GET", "/api/attributes/{id:int}/history", "getAttributeHistory", AuthorizationPolicies.Viewer, null },
        { "GET", "/api/energy/hourly", "listEnergyHourly", AuthorizationPolicies.Viewer, null },
        { "GET", "/api/settings/retention", "getRetentionSettings", AuthorizationPolicies.Admin, null },
        { "PUT", "/api/settings/retention", "updateRetentionSettings", AuthorizationPolicies.Admin, null },
        { "GET", "/api/placements", "listPlacements", AuthorizationPolicies.Viewer, null },
        { "PUT", "/api/placements", "placeOnFloor", AuthorizationPolicies.Admin, null },
        { "DELETE", "/api/placements/{id:int}", "deletePlacement", AuthorizationPolicies.Admin, null },
        { "PUT", "/api/integrations/{id:guid}", "updateIntegration", AuthorizationPolicies.Admin, null },
        { "POST", "/api/integrations/flows", "startIntegrationFlow", AuthorizationPolicies.Admin, null },
        { "POST", "/api/integrations/flows/{flowId:guid}", "continueIntegrationFlow", AuthorizationPolicies.Admin, null },
        { "DELETE", "/api/integrations/flows/{flowId:guid}", "cancelIntegrationFlow", AuthorizationPolicies.Admin, null },
        { "POST", "/api/integrations/{id:guid}/flows/reauth", "startIntegrationReauthFlow", AuthorizationPolicies.Admin, null },
        { "POST", "/api/devices/{id:guid}/replace", "replaceDevice", AuthorizationPolicies.Admin, null },
        { "GET", "/api/endpoints/{id:guid}/camera/snapshot", "getCameraSnapshot", AuthorizationPolicies.Viewer, null },
        { "GET", "/api/dashboard", "getDashboard", AuthorizationPolicies.Viewer, null },
        { "GET", "/api/backup/export", "exportBackup", AuthorizationPolicies.Admin, null },
        { "POST", "/api/backup/export", "exportBackupWithSecrets", AuthorizationPolicies.Admin, null },
        { "POST", "/api/backup/import", "importBackup", AuthorizationPolicies.Admin, null },
        { "GET", "/api/backup/scheduled", "listScheduledBackups", AuthorizationPolicies.Admin, null },
        { "POST", "/api/backup/scheduled", "runScheduledBackup", AuthorizationPolicies.Admin, null },
        { "GET", "/api/backup/scheduled/{name}", "getScheduledBackup", AuthorizationPolicies.Admin, null },
    };

    [Fact]
    public async Task Hub_message_payloads_are_in_the_committed_openapi_components()
    {
        using var document = await OpenApiDocument.LoadAsync(TestContext.Current.CancellationToken);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        foreach (var name in new[] { "StateDelta", "AvailabilityChangedDto", "IntegrationStatusChangedDto", "CommandStatusDto", "SystemStatusDto", "StateValue" })
        {
            schemas.TryGetProperty(name, out _).ShouldBeTrue($"{name} missing from openapi.json components");
        }

        schemas.GetProperty("StateDelta").GetProperty("properties").GetProperty("attributeId").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task State_values_require_only_their_kind_as_on_the_wire()
    {
        using var document = await OpenApiDocument.LoadAsync(TestContext.Current.CancellationToken);
        var stateValue = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("StateValue");

        stateValue.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(["kind"]);
        foreach (var member in new[] { "b", "n", "s" })
        {
            stateValue.GetProperty("properties").GetProperty(member).GetProperty("type").ToString().ShouldNotContain("null");
        }
    }

    [Fact]
    public async Task Device_list_query_parameters_are_camel_case()
    {
        using var document = await OpenApiDocument.LoadAsync(TestContext.Current.CancellationToken);
        var parameters = document.RootElement.GetProperty("paths").GetProperty("/api/devices").GetProperty("get").GetProperty("parameters");

        parameters.EnumerateArray().Select(p => p.GetProperty("name").GetString()).ShouldBe(["roomId", "integrationId", "capability", "q"]);
        parameters.EnumerateArray().ShouldAllBe(p => p.GetProperty("in").GetString() == "query");
    }

    [Fact]
    public void The_home_hub_is_mapped_and_requires_a_signed_in_user()
    {
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var hub = dataSource.Endpoints.OfType<RouteEndpoint>().Where(e => e.RoutePattern.RawText == "/hubs/home").ToList();

        hub.ShouldNotBeEmpty();
        hub.ShouldAllBe(e => e.Metadata.GetMetadata<IAllowAnonymous>() == null);
        hub.ShouldAllBe(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any());
    }

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
        var path = route.Replace(":guid", string.Empty, StringComparison.Ordinal).Replace(":int", string.Empty, StringComparison.Ordinal);

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
