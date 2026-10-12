using ControlHome.Application.Common;
using FluentValidation;

namespace ControlHome.Application.Setup;

/// <summary>First-run state. Answered even while the database is not ready (then <paramref name="SetupCompleted"/> is false).</summary>
/// <param name="DatabaseState"><c>NotConfigured</c>, <c>Connecting</c>, <c>Migrating</c>, <c>Ready</c> or <c>Failed</c>.</param>
/// <param name="DatabaseConfigured">Whether a database connection string is configured.</param>
/// <param name="Version">The application's informational version.</param>
public sealed record SetupStatusDto(bool SetupCompleted, string DatabaseState, bool DatabaseConfigured, string Version);

/// <summary>Completes the first-run setup: creates the first Admin and the home, then signs the admin in.</summary>
/// <param name="SetupCode">The one-time setup code from the server log or <c>setup-code.txt</c> (dashes/spaces ignored).</param>
public sealed record CompleteSetupRequest(string SetupCode, AdminAccountInput Admin, HomeInput Home);

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
