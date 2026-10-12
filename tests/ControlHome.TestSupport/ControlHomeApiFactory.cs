using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ControlHome.TestSupport;

/// <summary>
/// The API in environment <c>Testing</c> with its own fresh database (created and migrated by the app on startup), its own
/// temp <c>ControlHome:DataPath</c> and setup code <see cref="SetupCode"/>. <see cref="InitializeAsync"/> waits until
/// <c>/health/ready</c> answers 200 (at most 60 s). Use as a class fixture; create clients with
/// <see cref="ApiFactoryExtensions.CreateApiClient"/>.
/// </summary>
public class ControlHomeApiFactory(SqlServerFixture sql) : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestingEnvironment = "Testing";
    public const string SetupCode = "TEST-SETUP-0001-CODE";
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

    private readonly string _dataPath = Directory.CreateTempSubdirectory("controlhome-test-").FullName;
    private string? _databaseName;

    /// <summary>The app's <c>ControlHome:DataPath</c> (a temp folder, deleted on dispose).</summary>
    public string DataPath => _dataPath;

    /// <summary>The app's connection string, or <c>null</c> when <see cref="UseDatabase"/> is false.</summary>
    public string? ConnectionString { get; private set; }

    protected SqlServerFixture Sql { get; } = sql;

    /// <summary>False: start without a connection string (state NotConfigured).</summary>
    protected virtual bool UseDatabase => true;

    /// <summary>Whether <see cref="InitializeAsync"/> waits for <c>/health/ready</c>.</summary>
    protected virtual bool WaitForReady => UseDatabase;

    protected virtual string EnvironmentName => TestingEnvironment;

    /// <summary>Extra or overriding configuration (e.g. <c>Database:StartupTimeout</c>).</summary>
    protected virtual void ConfigureSettings(IDictionary<string, string?> settings)
    {
    }

    public virtual async ValueTask InitializeAsync()
    {
        if (UseDatabase)
        {
            await Sql.EnsureStartedAsync();
            var suffix = RandomNumberGenerator.GetHexString(8, lowercase: true);
            _databaseName = Sql.DatabaseName(suffix);
            ConnectionString = Sql.CreateDatabaseConnectionString(suffix);
        }

        StartServer();
        if (WaitForReady)
        {
            await this.WaitUntilReadyAsync(ReadyTimeout);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(EnvironmentName);

        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            // Always set (empty = not configured), so nothing from the machine's environment can leak in.
            ["ConnectionStrings:ControlHome"] = ConnectionString ?? string.Empty,
            ["ControlHome:DataPath"] = _dataPath,
            ["Setup:Code"] = SetupCode,
        };
        ConfigureSettings(settings);

        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (_databaseName is not null)
        {
            await Sql.DropDatabaseAsync(_databaseName);
            _databaseName = null;
        }

        try
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        catch (IOException)
        {
            // Best effort (a log or key file may still be open on Windows); it lives under %TEMP%.
        }

        GC.SuppressFinalize(this);
    }
}
