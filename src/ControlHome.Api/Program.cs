using ControlHome.Api.Features.Audit;
using ControlHome.Api.Features.Auth;
using ControlHome.Api.Features.Capabilities;
using ControlHome.Api.Features.Devices;
using ControlHome.Api.Features.Files;
using ControlHome.Api.Features.Floors;
using ControlHome.Api.Features.History;
using ControlHome.Api.Features.Home;
using ControlHome.Api.Features.Integrations;
using ControlHome.Api.Features.Settings;
using ControlHome.Api.Features.Setup;
using ControlHome.Api.Features.SystemStatus;
using ControlHome.Api.Features.Testing;
using ControlHome.Api.Features.Users;
using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Api.Realtime;
using ControlHome.Api.Security;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Auth;
using ControlHome.Application.Devices;
using ControlHome.Application.Files;
using ControlHome.Application.Floors;
using ControlHome.Application.History;
using ControlHome.Application.Homes;
using ControlHome.Application.Integrations;
using ControlHome.Application.Security;
using ControlHome.Application.Setup;
using ControlHome.Application.Users;
using ControlHome.Infrastructure;
using ControlHome.Infrastructure.Devices;
using ControlHome.Infrastructure.Security;
using ControlHome.Infrastructure.Setup;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;

// `--healthcheck` (the container HEALTHCHECK): probe /health/live on loopback and exit 0/1 without building a host.
if (HealthcheckCommand.IsRequested(args))
{
    return await HealthcheckCommand.RunAsync(args);
}

// CLI commands run a minimal host of their own and exit before the web server is ever built.
if (ResetAdminCommand.Matches(args))
{
    return await ResetAdminCommand.RunAsync(args, Console.Out, Console.Error);
}

// Nothing before builder.Build() may open a database or network connection or touch the data folder: `dotnet build` runs
// this file to generate the OpenAPI document, and `dotnet ef` runs it to find the DbContext.
Log.Logger = LoggingSetup.CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddControlHomeLogging();

    builder.Services.AddControlHomeInfrastructure();
    builder.Services.AddSetupInfrastructure();
    builder.Services.AddDeviceRuntime();
    builder.Services.AddControlHomeConnectors();
    builder.Services.AddControlHomeRealtime();
    if (!BuildContext.IsOpenApiGeneration)
    {
        builder.Services.AddDatabaseInitializer();
        builder.Services.AddControlHomeDataProtection();
        builder.Services.AddSetupCodeAnnouncer();
        builder.Services.AddDeviceRuntimeHost();
    }

    if (builder.Environment.IsEnvironment(TestingEndpoints.EnvironmentName))
    {
        builder.Services.AddTestingServices();
    }

    builder.Services.AddControlHomeJson();
    builder.Services.AddControlHomeProblemDetails();
    builder.Services.AddControlHomeOpenApi();
    builder.Services.AddControlHomeIdentity();
    builder.Services.AddControlHomeRateLimiting();
    builder.Services.AddControlHomeValidation();
    builder.Services.AddControlHomeRequestEdge();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentActor, CurrentActor>();
    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseReadinessHealthCheck>("database", tags: [DatabaseReadinessHealthCheck.ReadyTag]);

    // Features (one line each).
    builder.Services.AddSetup();
    builder.Services.AddAuth();
    builder.Services.AddUsers();
    builder.Services.AddHomes();
    builder.Services.AddAuditing();
    builder.Services.AddFloors();
    builder.Services.AddFiles();
    builder.Services.AddDevices();
    builder.Services.AddIntegrations();
    builder.Services.AddSafety();
    builder.Services.AddHistory();

    var app = builder.Build();

    app.UseControlHomeRequestLogging();
    if (!app.Environment.IsDevelopment())
    {
        // Development uses the developer exception page; elsewhere exceptions become a bare 500 internal.error.
        app.UseExceptionHandler();
    }

    app.UseStatusCodePages();
    app.UseControlHomeRequestEdge(); // forwarded headers, security headers, network allowlist, origin/header checks
    app.UseControlHomeSpaStaticFiles();
    app.UseRouting();
    app.UseMiddleware<DatabaseGateMiddleware>();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false }).AllowAnonymous();
    app.MapHealthChecks(
            "/health/ready",
            new HealthCheckOptions { Predicate = static check => check.Tags.Contains(DatabaseReadinessHealthCheck.ReadyTag) })
        .AllowAnonymous();
    app.MapControlHomeOpenApi();

    app.MapSetupEndpoints();
    app.MapAuthEndpoints();
    app.MapUserEndpoints();
    app.MapHomeEndpoints();
    app.MapAuditEndpoints();
    app.MapFloorEndpoints();
    app.MapFileEndpoints();
    app.MapCapabilityEndpoints();
    app.MapDeviceEndpoints();
    app.MapIntegrationEndpoints();
    app.MapSystemEndpoints();
    app.MapHistoryEndpoints();
    app.MapSettingsEndpoints();
    app.MapHomeHub();
    if (app.Environment.IsEnvironment(TestingEndpoints.EnvironmentName))
    {
        app.MapTestingEndpoints();
    }

    app.MapControlHomeSpaFallback();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "ControlHome terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

public partial class Program;
