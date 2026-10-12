using System.Globalization;
using ControlHome.Application.Security;
using ControlHome.Infrastructure.Configuration;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Hosting;

namespace ControlHome.Api.Hosting;

/// <summary>Serilog: console everywhere, plus a daily rolling file under <c>{DataPath}/logs</c> (14 files).</summary>
public static class LoggingSetup
{
    public const string TestingEnvironment = "Testing";

    private const string DevelopmentTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
    private const string CompactTemplate =
        "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>Logs startup failures (before the host's logger exists) to the console.</summary>
    public static ReloadableLogger CreateBootstrapLogger() =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: CompactTemplate, formatProvider: CultureInfo.InvariantCulture)
            .CreateBootstrapLogger();

    public static WebApplicationBuilder AddControlHomeLogging(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<KnownSecrets>();
        builder.Services.TryAddSingleton<KnownSecretsRedactor>();

        // preserveStaticLogger: each host gets its own logger, so several hosts in one process (WebApplicationFactory
        // tests) never fight over the static bootstrap logger. Everything is configured lazily, after Build().
        builder.Host.UseSerilog(
            (context, services, configuration) => Configure(configuration, context.HostingEnvironment, context.Configuration, services),
            preserveStaticLogger: true);
        return builder;
    }

    private static void Configure(
        LoggerConfiguration configuration,
        IHostEnvironment environment,
        IConfiguration appConfiguration,
        IServiceProvider services)
    {
        var redactor = services.GetRequiredService<KnownSecretsRedactor>();
        configuration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .ReadFrom.Configuration(appConfiguration)
            .ReadFrom.Services(services)
            .Destructure.With(redactor)
            .Enrich.FromLogContext()

            // Last enricher: secrets (integration tokens, passwords) never reach a sink.
            .Enrich.With(redactor)
            // Expected while the database starts (e.g. the framework pre-loading the Data Protection key ring); the API
            // answers 503 db.unavailable meanwhile, and the initializer logs the database state itself.
            .Filter.ByExcluding(static e => IsDatabaseNotReady(e.Exception))
            .WriteTo.Console(
                outputTemplate: environment.IsDevelopment() ? DevelopmentTemplate : CompactTemplate,
                formatProvider: CultureInfo.InvariantCulture);

        // No log files while generating the OpenAPI document at build time, nor in integration tests.
        if (!BuildContext.IsOpenApiGeneration && !environment.IsEnvironment(TestingEnvironment))
        {
            var logs = services.GetRequiredService<DataDirectory>().GetPath(DataDirectory.Logs);
            configuration.WriteTo.File(
                Path.Combine(logs, "controlhome-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: CompactTemplate,
                formatProvider: CultureInfo.InvariantCulture);
        }
    }

    /// <summary>One line per request, using the route template (never raw paths with ids, never query strings).</summary>
    public static WebApplication UseControlHomeRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.Logger = app.Services.GetService<Serilog.ILogger>();
            options.MessageTemplate = "HTTP {RequestMethod} {RouteTemplate} responded {StatusCode} in {Elapsed:0.0000} ms";
            options.GetMessageTemplateProperties = static (context, path, elapsedMs, statusCode) =>
            [
                new LogEventProperty("RequestMethod", new ScalarValue(context.Request.Method)),
                new LogEventProperty("RouteTemplate", new ScalarValue(RouteTemplate(context, path))),
                new LogEventProperty("StatusCode", new ScalarValue(statusCode)),
                new LogEventProperty("Elapsed", new ScalarValue(elapsedMs)),
            ];
            options.GetLevel = static (context, _, exception) =>
                exception is not null || context.Response.StatusCode >= StatusCodes.Status500InternalServerError
                    ? LogEventLevel.Error
                    : context.Request.Path.StartsWithSegments("/health")
                        ? LogEventLevel.Debug
                        : LogEventLevel.Information;
        });
        return app;
    }

    private static bool IsDatabaseNotReady(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DatabaseNotReadyException)
            {
                return true;
            }
        }

        return false;
    }

    private static string RouteTemplate(HttpContext context, string path) =>
        context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } template } ? template : path;
}
