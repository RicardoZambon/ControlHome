using ControlHome.Api.Hosting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = static check => check.Tags.Contains("ready") })
    .AllowAnonymous();

app.UseControlHomeSpa();

await app.RunAsync();

public partial class Program;
