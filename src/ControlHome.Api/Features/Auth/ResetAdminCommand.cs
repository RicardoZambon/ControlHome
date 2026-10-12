using System.Globalization;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auth;
using ControlHome.Infrastructure;
using ControlHome.Infrastructure.Auditing;
using ControlHome.Infrastructure.Identity;
using ControlHome.Infrastructure.Persistence;
using ControlHome.Infrastructure.Security;
using Microsoft.Extensions.Logging.Console;

namespace ControlHome.Api.Features.Auth;

/// <summary>
/// <c>dotnet ControlHome.Api.dll reset-admin &lt;userName&gt;</c>: recovery for a locked-out or forgotten administrator.
/// Prints a single-use URL path (<c>/reset-password?user=…&amp;token=…</c>, valid for 15 minutes) whose token
/// <c>POST /api/auth/reset-password</c> consumes, then exits. Runs a minimal host (configuration, database, Data
/// Protection, Identity) and never starts the web server. Audited as <c>auth.admin_reset</c> (actor System).
/// </summary>
/// <remarks>The path is the only line on standard output; guidance and logs go to standard error.</remarks>
public static class ResetAdminCommand
{
    public const string Name = "reset-admin";

    public const int ExitSuccess = 0;
    public const int ExitFailure = 1;
    public const int ExitUsage = 2;

    /// <summary>The command waits at most this long for the database (instead of the app's 10-minute startup timeout).</summary>
    public static readonly TimeSpan MaxDatabaseWait = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan _settlePollInterval = TimeSpan.FromMilliseconds(100);

    public static bool Matches(IReadOnlyList<string> args) =>
        args is { Count: > 0 } && string.Equals(args[0], Name, StringComparison.Ordinal);

    /// <param name="args">The process arguments: <c>reset-admin &lt;userName&gt; [configuration overrides…]</c>.</param>
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

        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith('-'))
        {
            await error.WriteLineAsync($"Usage: dotnet ControlHome.Api.dll {Name} <userName>");
            return ExitUsage;
        }

        var userName = args[1];
        using var host = BuildHost(args[2..], configure);

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
        var result = await scope.ServiceProvider.GetRequiredService<AdminPasswordResetService>()
            .CreateResetLinkAsync(userName, cancellationToken);
        if (result.IsFailure)
        {
            await error.WriteLineAsync(result.Error.Message);
            return ExitFailure;
        }

        var link = result.Value;
        await error.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Open this path on your ControlHome address within {AdminPasswordResetService.TokenLifetime.TotalMinutes:0} minutes to set a new password for '{link.UserName}' (it works once):"));
        await output.WriteLineAsync(link.Path);
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
        builder.Services.AddControlHomeIdentityCore();
        builder.Services.AddSingleton<ICurrentActor>(SystemActor.Instance);
        builder.Services.AddScoped<AdminPasswordResetService>();

        configure?.Invoke(builder);
        return builder.Build();
    }

    /// <summary>
    /// Connects, migrates if needed and seeds, exactly like the app's startup (so the key ring is reachable). The initializer
    /// keeps running while the database is not configured or failed (it waits for one / retries), so this waits for the
    /// first settled state instead: Ready, Failed (after the capped start-up timeout) or NotConfigured.
    /// </summary>
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
