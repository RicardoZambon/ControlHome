using ControlHome.Api.Features.Audit;
using ControlHome.Api.Features.Auth;
using ControlHome.Api.Features.Home;
using ControlHome.Api.Features.Setup;
using ControlHome.Api.Features.Testing;
using ControlHome.Api.Features.Users;
using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Setup;
using ControlHome.Infrastructure;
using ControlHome.Infrastructure.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;

// `--healthcheck` (the container HEALTHCHECK): probe /health/live on loopback and exit 0/1 without building a host.
if (HealthcheckCommand.IsRequested(args))
{
    return await HealthcheckCommand.RunAsync(args);
}

// Nothing before builder.Build() may open a database or network connection or touch the data folder: `dotnet build` runs
// this file to generate the OpenAPI document, and `dotnet ef` runs it to find the DbContext.
Log.Logger = LoggingSetup.CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddControlHomeLogging();

    builder.Services.AddControlHomeInfrastructure();
    if (!BuildContext.IsOpenApiGeneration)
    {
        builder.Services.AddDatabaseInitializer();
        builder.Services.AddControlHomeDataProtection();
    }

    builder.Services.AddControlHomeJson();
    builder.Services.AddControlHomeProblemDetails();
    builder.Services.AddControlHomeOpenApi();
    builder.Services.AddControlHomeIdentity();
    builder.Services.AddControlHomeRateLimiting();
    builder.Services.AddControlHomeValidation();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentActor, CurrentActor>();
    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseReadinessHealthCheck>("database", tags: [DatabaseReadinessHealthCheck.ReadyTag]);

    // Features (one line each).
    builder.Services.AddSetup();

    var app = builder.Build();

    app.UseControlHomeRequestLogging();
    if (!app.Environment.IsDevelopment())
    {
        // Development uses the developer exception page; elsewhere exceptions become a bare 500 internal.error.
        app.UseExceptionHandler();
    }

    app.UseStatusCodePages();
    // Task 0.2-B: app.UseControlHomeRequestEdge() goes here (origin/header checks, network allowlist, security headers).
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
