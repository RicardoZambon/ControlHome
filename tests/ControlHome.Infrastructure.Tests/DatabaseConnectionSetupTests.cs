using ControlHome.Application.Common;
using ControlHome.Application.Setup;
using ControlHome.Infrastructure.Persistence;
using ControlHome.Infrastructure.Setup;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ControlHome.Infrastructure.Tests;

public sealed class DatabaseConnectionSetupTests
{
    private const string Password = "Pa55word-Never-Echoed";

    private static readonly SqlServerFacts _missingCreatable = new(17, DatabaseExists: false, CompatibilityLevel: null, CanCreateDatabase: true, HasDatabaseAccess: false);

    private readonly FakeConnectionProvider _connections = new();
    private readonly ISqlServerProbe _probe = Substitute.For<ISqlServerProbe>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tests_and_saves_an_encrypted_tls_connection_string_built_from_the_fields()
    {
        string? probed = null;
        _probe.ProbeAsync(Arg.Do<string>(cs => probed = cs), "ControlHome", Arg.Any<CancellationToken>()).Returns(_missingCreatable);
        var token = _connections.GetChangeToken();

        var result = await Setup().ConfigureAsync(Request(trust: false), Ct);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        token.HasChanged.ShouldBeTrue();
        var saved = new SqlConnectionStringBuilder(_connections.GetConnectionString().ShouldNotBeNull());
        saved.DataSource.ShouldBe("192.168.1.20,1433");
        saved.InitialCatalog.ShouldBe("ControlHome");
        saved.UserID.ShouldBe("controlhome");
        saved.Password.ShouldBe(Password);
        saved.Encrypt.ShouldBe(SqlConnectionEncryptOption.Mandatory);
        saved.TrustServerCertificate.ShouldBeFalse();
        saved.ConnectTimeout.ShouldBe(10);
        saved.ApplicationName.ShouldBe("ControlHome");
        saved.IntegratedSecurity.ShouldBeFalse();
        probed.ShouldBe(saved.ConnectionString);
    }

    [Fact]
    public async Task Trust_server_certificate_is_passed_through()
    {
        _probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_missingCreatable);

        (await Setup().ConfigureAsync(Request(trust: true), Ct)).IsSuccess.ShouldBeTrue();

        new SqlConnectionStringBuilder(_connections.GetConnectionString()).TrustServerCertificate.ShouldBeTrue();
    }

    [Fact]
    public async Task Existing_database_the_login_can_open_is_accepted()
    {
        _probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SqlServerFacts(17, true, 170, CanCreateDatabase: false, HasDatabaseAccess: true));

        (await Setup().ConfigureAsync(Request(), Ct)).IsSuccess.ShouldBeTrue();

        _connections.GetConnectionString().ShouldNotBeNull();
    }

    [Fact]
    public async Task Server_older_than_2025_is_400_sql_version_unsupported()
    {
        _probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_missingCreatable with { MajorVersion = 16 });

        var error = await FailAsync();

        error.Code.ShouldBe(ErrorCodes.SqlVersionUnsupported);
        error.Kind.ShouldBe(AppErrorKind.Validation);
    }

    [Fact]
    public async Task Missing_database_the_login_cannot_create_is_409_with_a_script()
    {
        _probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_missingCreatable with { CanCreateDatabase = false });

        var error = await FailAsync(Request(database: "Casa_Main"));

        error.Code.ShouldBe(ErrorCodes.DbCreateDenied);
        error.Kind.ShouldBe(AppErrorKind.Conflict);
        var script = error.Extensions.ShouldNotBeNull()["script"].ShouldBeOfType<string>();
        script.ShouldContain("CREATE DATABASE [Casa_Main];");
        script.ShouldContain("ALTER DATABASE [Casa_Main] SET COMPATIBILITY_LEVEL = 170;");
        script.ShouldContain("ALTER DATABASE [Casa_Main] SET RECOVERY SIMPLE;");
        script.ShouldContain("CREATE LOGIN [ControlHome_App] WITH PASSWORD = N'<choose-a-strong-password>'");
        script.ShouldContain("USE [Casa_Main];");
        script.ShouldContain("CREATE USER [ControlHome_App] FOR LOGIN [ControlHome_App];");
        script.ShouldContain("ALTER ROLE [db_owner] ADD MEMBER [ControlHome_App];");
        script.ShouldNotContain(Password);
        script.ShouldNotContain("DROP", Case.Insensitive);
    }

    [Fact]
    public async Task Existing_database_the_login_cannot_open_is_400_connection_failed()
    {
        _probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SqlServerFacts(17, true, 170, CanCreateDatabase: false, HasDatabaseAccess: false));

        var error = await FailAsync();

        error.Code.ShouldBe(ErrorCodes.DbConnectionFailed);
        error.Extensions.ShouldNotBeNull()["reason"].ShouldBe("no_database_access");
    }

    [Fact]
    public async Task Existing_database_below_compatibility_level_170_is_400()
    {
        _probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SqlServerFacts(17, true, 160, CanCreateDatabase: true, HasDatabaseAccess: true));

        var error = await FailAsync();

        error.Code.ShouldBe(ErrorCodes.SqlCompatibilityUnsupported);
    }

    [Fact]
    public async Task Connection_failure_is_400_with_a_sanitized_message()
    {
        _probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException<SqlServerFacts>(new InvalidOperationException("Server=192.168.1.20;Password=" + Password + " failed")));

        var error = await FailAsync();

        error.Code.ShouldBe(ErrorCodes.DbConnectionFailed);
        error.Kind.ShouldBe(AppErrorKind.Validation);
        error.Message.ShouldNotContain(Password);
        error.Message.ShouldNotContain("Server=");
        error.Extensions.ShouldNotBeNull()["reason"].ShouldBe("other");
    }

    [Fact]
    public async Task Nothing_is_saved_when_a_connection_is_already_configured()
    {
        _connections.Set("Server=elsewhere;Database=ControlHome_Test_x;User ID=a;Password=b");
        _probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_missingCreatable);

        var error = await FailAsync();

        error.Code.ShouldBe(ErrorCodes.DbAlreadyConfigured);
        error.Kind.ShouldBe(AppErrorKind.Conflict);
        _connections.GetConnectionString().ShouldBe("Server=elsewhere;Database=ControlHome_Test_x;User ID=a;Password=b");
    }

    [Theory]
    [InlineData(18456, "Login failed for user 'controlhome'.", false, "login_failed")]
    [InlineData(18470, "Login failed for user 'controlhome'. Reason: The account is disabled.", false, "login_failed")]
    [InlineData(-2, "Connection Timeout Expired. The timeout period elapsed during the post-login phase.", false, "unreachable")]
    [InlineData(0, "A network-related or instance-specific error occurred while establishing a connection to SQL Server.", false, "unreachable")]
    [InlineData(35, "A connection was successfully established with the server, but then an error occurred during the pre-login handshake.", true, "certificate")]
    [InlineData(0, "The certificate chain was issued by an authority that is not trusted.", false, "certificate")]
    [InlineData(2812, "Could not find stored procedure 'x'.", false, "other")]
    public void Sql_failures_are_classified_without_echoing_the_server_message(int number, string message, bool tlsFailure, string reason)
    {
        var failure = DatabaseConnectionSetup.DescribeFailure(number, message, tlsFailure, Request());

        failure.Reason.ShouldBe(reason);
        failure.Message.ShouldNotContain(Password);
        failure.Message.ShouldNotContain(message);
    }

    private DatabaseConnectionSetup Setup() => new(_connections, _probe, NullLogger<DatabaseConnectionSetup>.Instance);

    private async Task<AppError> FailAsync(DatabaseSetupRequest? request = null)
    {
        var result = await Setup().ConfigureAsync(request ?? Request(), Ct);
        result.IsFailure.ShouldBeTrue();
        if (result.Error.Code != ErrorCodes.DbAlreadyConfigured)
        {
            _connections.GetConnectionString().ShouldBeNull();
        }

        return result.Error;
    }

    private static DatabaseSetupRequest Request(string database = "ControlHome", bool trust = true) =>
        new("CODE-0000-0000", "192.168.1.20", 1433, database, "controlhome", Password, trust);
}
