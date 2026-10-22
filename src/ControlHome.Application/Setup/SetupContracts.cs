using System.Text.RegularExpressions;
using ControlHome.Application.Common;
using FluentValidation;

namespace ControlHome.Application.Setup;

/// <summary>First-run state. Answered even while the database is not ready (then <paramref name="SetupCompleted"/> is false).</summary>
/// <param name="DatabaseState"><c>NotConfigured</c>, <c>Connecting</c>, <c>Migrating</c>, <c>Ready</c> or <c>Failed</c>.</param>
/// <param name="DatabaseConfigured">Whether a database connection is configured (either source).</param>
/// <param name="DatabaseSource">
/// Where the connection comes from: <c>environment</c> (<c>ConnectionStrings__ControlHome</c>), <c>file</c> (saved by the
/// setup wizard) or <c>none</c> (pre-database setup mode: <c>POST /api/setup/database</c>).
/// </param>
/// <param name="DatabaseError">While <paramref name="DatabaseState"/> is <c>Failed</c>: the error code, e.g. <c>db.connection_failed</c>.</param>
/// <param name="Version">The application's informational version.</param>
public sealed record SetupStatusDto(
    bool SetupCompleted,
    string DatabaseState,
    bool DatabaseConfigured,
    string DatabaseSource,
    string? DatabaseError,
    string Version);

/// <summary>
/// Pre-database setup: the SQL Server connection to test and save. Accepted only while no connection is configured.
/// The connection always uses TLS (<c>Encrypt=True</c>); <paramref name="TrustServerCertificate"/> accepts the server's
/// own (usually self-signed) certificate.
/// </summary>
/// <param name="SetupCode">The one-time setup code (as for <c>POST /api/setup/complete</c>).</param>
/// <param name="Host">Host name or IP address of the SQL Server, as the ControlHome server (container) resolves it.</param>
/// <param name="Database">The application database; created by ControlHome when the login may create databases.</param>
public sealed record DatabaseSetupRequest(
    string SetupCode,
    string Host,
    int Port,
    string Database,
    string UserName,
    string Password,
    bool TrustServerCertificate);

/// <summary>Completes the first-run setup: creates the first Admin and the home, then signs the admin in.</summary>
/// <param name="SetupCode">The one-time setup code from the server log or <c>setup-code.txt</c> (dashes/spaces ignored).</param>
/// <param name="LoadDemoHome">
/// Also load the demo home (two floors, eight rooms, a Simulated integration with about 20 devices placed on the plans),
/// named in the home's language. Skipped when the home already has floors, integrations or devices.
/// </param>
public sealed record CompleteSetupRequest(string SetupCode, AdminAccountInput Admin, HomeInput Home, bool? LoadDemoHome = null);

/// <summary>The first administrator account.</summary>
public sealed record AdminAccountInput(string UserName, string DisplayName, string Password);

/// <summary>The home's basic settings.</summary>
/// <param name="TimeZoneId">IANA time zone id, e.g. <c>America/Sao_Paulo</c>.</param>
/// <param name="Locale"><c>pt-BR</c> or <c>en</c>.</param>
/// <param name="Currency">ISO 4217 code, e.g. <c>BRL</c>.</param>
/// <param name="PricePerKwh">Flat electricity price per kWh, if known.</param>
public sealed record HomeInput(
    string Name,
    string TimeZoneId,
    double? Latitude,
    double? Longitude,
    string Locale,
    string Currency,
    decimal? PricePerKwh);

public sealed class CompleteSetupRequestValidator : AbstractValidator<CompleteSetupRequest>
{
    public CompleteSetupRequestValidator()
    {
        RuleFor(x => x.SetupCode).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Admin).NotNull().SetValidator(new AdminAccountInputValidator());
        RuleFor(x => x.Home).NotNull().SetValidator(new HomeInputValidator());
    }
}

public sealed partial class DatabaseSetupRequestValidator : AbstractValidator<DatabaseSetupRequest>
{
    public const int HostMaxLength = 253;

    /// <summary>A plain identifier, so the name is safe inside <c>[...]</c> in the T-SQL shown for <c>db.create_denied</c>.</summary>
    public const string DatabaseNamePattern = "^[A-Za-z][A-Za-z0-9_]{0,127}$";

    /// <summary>SQL Server's limit for login names and passwords (<c>sysname</c>).</summary>
    public const int SqlNameMaxLength = 128;

    public DatabaseSetupRequestValidator()
    {
        RuleFor(x => x.SetupCode).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Host)
            .NotEmpty()
            .MaximumLength(HostMaxLength)
            .Matches(HostCharacters())
            .WithMessage("'{PropertyName}' must be a host name or an IP address.");
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
        RuleFor(x => x.Database)
            .NotEmpty()
            .Must(IsValidDatabaseName)
            .WithMessage("'{PropertyName}' must start with a letter and use only letters, digits and _ (at most 128).");
        RuleFor(x => x.UserName).NotEmpty().MaximumLength(SqlNameMaxLength);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(SqlNameMaxLength);
    }

    /// <summary>Whether <paramref name="name"/> matches <see cref="DatabaseNamePattern"/> (no trailing newline either).</summary>
    public static bool IsValidDatabaseName(string? name) => name is not null && DatabaseName().IsMatch(name);

    // \z instead of $: '$' would also accept a trailing newline.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseName();

    /// <summary>Host names, IPv4 and IPv6 addresses: nothing that could reach into the connection string's syntax.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._:\-\[\]]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostCharacters();
}

public sealed class AdminAccountInputValidator : AbstractValidator<AdminAccountInput>
{
    public AdminAccountInputValidator()
    {
        RuleFor(x => x.UserName).UserName();
        RuleFor(x => x.DisplayName).DisplayName();
        RuleFor(x => x.Password).NewPassword();
    }
}

public sealed class HomeInputValidator : AbstractValidator<HomeInput>
{
    public HomeInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(ValidationRules.HomeNameMaxLength);
        RuleFor(x => x.TimeZoneId).TimeZoneId();
        RuleFor(x => x.Latitude).Latitude();
        RuleFor(x => x.Longitude).Longitude();
        RuleFor(x => x.Locale).Locale();
        RuleFor(x => x.Currency).Currency();
        RuleFor(x => x.PricePerKwh).PricePerKwh();
    }
}
