using System.Globalization;
using ControlHome.Api.Hosting;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Common;
using ControlHome.Application.Demo;
using ControlHome.Application.Devices;
using ControlHome.Application.Integrations;
using ControlHome.Infrastructure;
using ControlHome.Infrastructure.Auditing;
using ControlHome.Infrastructure.Devices;
using ControlHome.Infrastructure.Persistence;
using ControlHome.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Console;

namespace ControlHome.Api.Features.Demo;

/// <summary>
/// <c>dotnet ControlHome.Api.dll --seed-demo [en|pt-BR]</c>: loads the demo home (two floors, eight rooms, a Simulated
/// integration with about 20 devices placed on the plans) into an empty home, then exits; the app starts the demo
/// integration the next time it runs. The language defaults to the home's (en before the first-run setup). Runs a minimal
/// host (configuration, database, Data Protection, the device inventory) and never starts the web server or a connector.
/// Audited as <c>demo.loaded</c> (actor System).
/// </summary>
public static class SeedDemoCommand
{
    public const string Name = "--seed-demo";

    public const int ExitSuccess = 0;
    public const int ExitFailure = 1;
    public const int ExitUsage = 2;

    /// <summary>The home already has floors, integrations or devices: nothing was changed.</summary>
    public const int ExitNotEmpty = 3;

    /// <summary>The command waits at most this long for the database (instead of the app's 10-minute startup timeout).</summary>
    public static readonly TimeSpan MaxDatabaseWait = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan _settlePollInterval = TimeSpan.FromMilliseconds(100);

    public static bool Matches(IReadOnlyList<string> args) =>
        args is { Count: > 0 } && string.Equals(args[0], Name, StringComparison.Ordinal);

    /// <param name="args">The process arguments: <c>--seed-demo [en|pt-BR] [configuration overrides…]</c>.</param>
    /// <param name="configure">Extra host configuration (tests point it at their database and data folder).</param>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Action<HostApplicationBuilder>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string? locale = null;
        var rest = args[1..];
        if (rest.Length > 0 && !rest[0].StartsWith('-'))
        {
            locale = rest[0];
            rest = rest[1..];
            if (locale is not ("en" or "pt-BR"))
            {
                await error.WriteLineAsync($"Usage: dotnet ControlHome.Api.dll {Name} [en|pt-BR]");
                return ExitUsage;
            }
        }

        using var host = BuildHost(rest, configure);
        var readiness = host.Services.GetRequiredService<DatabaseReadiness>();
        await InitializeDatabaseAsync(host.Services, cancellationToken);
        if (!readiness.IsReady)
        {
            await error.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"The database is not available (state {readiness.State}, {readiness.LastError ?? "no error code"}); check ConnectionStrings:ControlHome or the database saved by the setup wizard."));
            return ExitFailure;
        }

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();
        locale ??= await db.Homes.AsNoTracking().Select(h => h.Locale).SingleOrDefaultAsync(cancellationToken) ?? "en";
        var result = await scope.ServiceProvider.GetRequiredService<DemoHomeService>().LoadAsync(locale, cancellationToken);
        if (result.IsFailure)
        {
            await error.WriteLineAsync(result.Error.Code == ErrorCodes.DemoNotEmpty
                ? "The home already has floors, integrations or devices; the demo home is loaded only into an empty home. Nothing was changed."
                : result.Error.Message);
            return result.Error.Code == ErrorCodes.DemoNotEmpty ? ExitNotEmpty : ExitFailure;
        }

        var summary = result.Value;
        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Demo home loaded ({locale}): {summary.Floors} floors, {summary.Rooms} rooms, {summary.Devices} devices, {summary.Placements} placements."));
        return ExitSuccess;
    }

    private static IHost BuildHost(string[] args, Action<HostApplicationBuilder>? configure)
    {
        // Same configuration sources and environment as the web app (appsettings, user secrets in Development, env vars).
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? Environments.Production,
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddControlHomeInfrastructure();
        builder.Services.AddSingleton<DatabaseInitializer>();
        builder.Services.PostConfigure<DatabaseOptions>(options =>
            options.StartupTimeout = options.StartupTimeout < MaxDatabaseWait ? options.StartupTimeout : MaxDatabaseWait);
        builder.Services.AddControlHomeDataProtection();
        builder.Services.AddSingleton<ICurrentActor>(SystemActor.Instance);

        // The device inventory without its host: nothing runs, devices are only imported.
        builder.Services.AddDeviceRuntime();
        builder.Services.AddControlHomeConnectors();
        builder.Services.AddDevices();
        builder.Services.AddIntegrations();
        builder.Services.AddDemoHome();

        configure?.Invoke(builder);
        return builder.Build();
    }

    /// <summary>Connects, migrates if needed and seeds the roles, exactly like the app's startup (see <c>reset-admin</c>).</summary>
    private static async Task InitializeDatabaseAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var initializer = services.GetRequiredService<DatabaseInitializer>();
        var readiness = services.GetRequiredService<DatabaseReadiness>();
        await initializer.StartAsync(cancellationToken);
        try
        {
            while (readiness.State is not (DatabaseState.Ready or DatabaseState.Failed or DatabaseState.NotConfigured)
                && initializer.ExecuteTask is not { IsCompleted: true })
            {
                await Task.Delay(_settlePollInterval, cancellationToken);
            }
        }
        finally
        {
            await initializer.StopAsync(CancellationToken.None);
        }
    }
}
