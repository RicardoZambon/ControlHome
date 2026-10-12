using ControlHome.Application.Common;
using FluentValidation;

namespace ControlHome.Application.Homes;

/// <summary>The home's settings.</summary>
/// <param name="TimeZoneId">IANA time zone id, e.g. <c>America/Sao_Paulo</c>.</param>
/// <param name="Locale"><c>pt-BR</c> or <c>en</c>.</param>
/// <param name="Currency">ISO 4217 code, e.g. <c>BRL</c>.</param>
/// <param name="PricePerKwh">Flat electricity price per kWh (tariff kind <c>flat</c>), if known.</param>
public sealed record HomeDto(
    int Id,
    string Name,
    string TimeZoneId,
    double? Latitude,
    double? Longitude,
    string Locale,
    string Currency,
    decimal? PricePerKwh,
    DateTimeOffset? SetupCompletedAtUtc,
    DateTimeOffset CreatedAtUtc);

/// <summary>Changes the home's settings (Admin only).</summary>
public sealed record UpdateHomeRequest(
    string Name,
    string TimeZoneId,
    double? Latitude,
    double? Longitude,
    string Locale,
    string Currency,
    decimal? PricePerKwh);

public sealed class UpdateHomeRequestValidator : AbstractValidator<UpdateHomeRequest>
{
    public UpdateHomeRequestValidator()
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
