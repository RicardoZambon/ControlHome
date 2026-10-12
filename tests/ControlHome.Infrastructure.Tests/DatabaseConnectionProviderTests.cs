using System.Text.Json;
using ControlHome.Infrastructure.Persistence;
using ControlHome.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Infrastructure.Tests;

public sealed class DatabaseConnectionProviderTests : IDisposable
{
    // Synthetic test values, assembled so the secret scanner sees no literal connection-string password.
    private const string EnvironmentSecret = "env-Secret-123";
    private const string WizardSecret = "Wizard-Secret-456";

    private const string EnvironmentConnection =
        "Server=tcp:env.example,1433;Database=ControlHome_Test_env;User ID=env;Password=" + EnvironmentSecret + ";Encrypt=True";

    private const string WizardConnection =
        "Data Source=db.example,1433;Initial Catalog=ControlHome;User ID=wizard;Password=" + WizardSecret + ";Encrypt=True";

    private readonly string _dataPath = Directory.CreateTempSubdirectory("controlhome-dbcfg-").FullName;

    private string ConfigFile => Path.Combine(_dataPath, "config", DatabaseConfigFile.FileName);

    private string BootstrapKeys => Path.Combine(_dataPath, "keys", "bootstrap");

    [Fact]
    public async Task Nothing_configured_is_None_and_touches_no_files()
    {
        await using var services = Build();
        var provider = services.GetRequiredService<IDatabaseConnectionProvider>();

        provider.GetConnectionString().ShouldBeNull();
        provider.Source.ShouldBe(DatabaseConnectionSource.None);
        Directory.EnumerateFileSystemEntries(_dataPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task Configuration_comes_first()
    {
        await using (var wizard = Build())
        {
            wizard.GetRequiredService<IDatabaseConnectionProvider>().TrySave(WizardConnection).ShouldBeTrue();
        }

        await using var services = Build(EnvironmentConnection);
        var provider = services.GetRequiredService<IDatabaseConnectionProvider>();

        provider.GetConnectionString().ShouldBe(EnvironmentConnection);
        provider.Source.ShouldBe(DatabaseConnectionSource.Environment);
    }

    [Fact]
    public async Task Saved_connection_is_encrypted_at_rest_and_read_back_after_a_restart()
    {
        await using (var first = Build())
        {
            var provider = first.GetRequiredService<IDatabaseConnectionProvider>();
            provider.TrySave(WizardConnection).ShouldBeTrue();
            provider.GetConnectionString().ShouldBe(WizardConnection);
            provider.Source.ShouldBe(DatabaseConnectionSource.File);
        }

        var text = await File.ReadAllTextAsync(ConfigFile, TestContext.Current.CancellationToken);
        foreach (var secretOrDetail in new[] { WizardSecret, "wizard", "db.example", "Initial Catalog", "Password" })
        {
            text.ShouldNotContain(secretOrDetail, Case.Insensitive);
        }

        using (var json = JsonDocument.Parse(text))
        {
            json.RootElement.GetProperty("version").GetInt32().ShouldBe(1);
            json.RootElement.GetProperty("protectedConnectionString").GetString().ShouldNotBeNullOrWhiteSpace();
        }

        // A new process (same data folder, same certificate) decrypts it.
        await using var second = Build();
        var restarted = second.GetRequiredService<IDatabaseConnectionProvider>();
        restarted.GetConnectionString().ShouldBe(WizardConnection);
        restarted.Source.ShouldBe(DatabaseConnectionSource.File);
    }

    [Fact]
    public async Task File_is_owner_only_and_written_without_leftovers()
    {
        await using var services = Build();

        services.GetRequiredService<IDatabaseConnectionProvider>().TrySave(WizardConnection).ShouldBeTrue();

        Directory.EnumerateFiles(Path.GetDirectoryName(ConfigFile)!).Select(Path.GetFileName).ShouldBe([DatabaseConfigFile.FileName]);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(ConfigFile).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Bootstrap_key_ring_lives_under_keys_bootstrap_encrypted_with_the_certificate()
    {
        await using var services = Build();

        services.GetRequiredService<IDatabaseConnectionProvider>().TrySave(WizardConnection).ShouldBeTrue();

        File.Exists(Path.Combine(_dataPath, "keys", DataProtectionCertificateStore.FileName)).ShouldBeTrue();
        var keyFile = Directory.EnumerateFiles(BootstrapKeys, "key-*.xml").ShouldHaveSingleItem();
        var keyXml = await File.ReadAllTextAsync(keyFile, TestContext.Current.CancellationToken);
        keyXml.ShouldContain("EncryptedData");
        keyXml.ShouldNotContain("<value>");
        services.GetRequiredService<BootstrapDataProtection>().KeysPath.ShouldBe(BootstrapKeys);
    }

    [Fact]
    public async Task Saving_signals_the_change_token()
    {
        await using var services = Build();
        var provider = services.GetRequiredService<IDatabaseConnectionProvider>();
        var token = provider.GetChangeToken();
        var signalled = false;
        using var registration = token.RegisterChangeCallback(_ => signalled = true, state: null);

        token.HasChanged.ShouldBeFalse();
        provider.TrySave(WizardConnection).ShouldBeTrue();

        token.HasChanged.ShouldBeTrue();
        signalled.ShouldBeTrue();
        provider.GetChangeToken().HasChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task Saving_is_refused_while_a_connection_is_configured()
    {
        await using (var environment = Build(EnvironmentConnection))
        {
            environment.GetRequiredService<IDatabaseConnectionProvider>().TrySave(WizardConnection).ShouldBeFalse();
            File.Exists(ConfigFile).ShouldBeFalse();
        }

        await using var services = Build();
        var provider = services.GetRequiredService<IDatabaseConnectionProvider>();
        provider.TrySave(WizardConnection).ShouldBeTrue();
        var token = provider.GetChangeToken();

        provider.TrySave(EnvironmentConnection).ShouldBeFalse();

        provider.GetConnectionString().ShouldBe(WizardConnection);
        token.HasChanged.ShouldBeFalse();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"version\":1,\"protectedConnectionString\":\"CfDJ8-not-a-real-payload\"}")]
    [InlineData("{\"version\":99,\"protectedConnectionString\":\"x\"}")]
    [InlineData("{}")]
    public async Task Unreadable_file_counts_as_not_configured_and_can_be_replaced(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        await File.WriteAllTextAsync(ConfigFile, content, TestContext.Current.CancellationToken);
        await using var services = Build();
        var provider = services.GetRequiredService<IDatabaseConnectionProvider>();

        provider.GetConnectionString().ShouldBeNull();
        provider.Source.ShouldBe(DatabaseConnectionSource.None);

        provider.TrySave(WizardConnection).ShouldBeTrue();
        provider.GetConnectionString().ShouldBe(WizardConnection);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private ServiceProvider Build(string? connectionString = null) =>
        InfrastructureHost.Build(new Dictionary<string, string?>
        {
            ["ControlHome:DataPath"] = _dataPath,
            ["ConnectionStrings:ControlHome"] = connectionString ?? string.Empty,
        });
}
