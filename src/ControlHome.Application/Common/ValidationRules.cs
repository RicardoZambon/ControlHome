using ControlHome.Domain.Security;
using FluentValidation;

namespace ControlHome.Application.Common;

/// <summary>Shared FluentValidation rules, so every contract validates the same field the same way.</summary>
public static class ValidationRules
{
    public const int PasswordMinLength = 10;
    public const int PasswordMaxLength = 256;
    public const int UserNameMinLength = 3;
    public const int UserNameMaxLength = 64;
    public const int DisplayNameMaxLength = 80;
    public const int HomeNameMaxLength = 80;

    /// <summary>3–64 characters from <c>[A-Za-z0-9._@-]</c>.</summary>
    public static IRuleBuilderOptions<T, string> UserName<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .Length(UserNameMinLength, UserNameMaxLength)
            .Matches("^[A-Za-z0-9._@-]+$")
            .WithMessage("'{PropertyName}' may only contain letters, digits and . _ @ -");

    /// <summary>At least 10 characters, no composition rules (NIST 800-63B style).</summary>
    public static IRuleBuilderOptions<T, string> NewPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Length(PasswordMinLength, PasswordMaxLength);

    public static IRuleBuilderOptions<T, string> DisplayName<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(DisplayNameMaxLength);

    public static IRuleBuilderOptions<T, string> Locale<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .Must(value => Locales.All.Contains(value, StringComparer.Ordinal))
            .WithMessage($"'{{PropertyName}}' must be one of: {string.Join(", ", Locales.All)}.");

    public static IRuleBuilderOptions<T, string> Theme<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .Must(value => Themes.All.Contains(value, StringComparer.Ordinal))
            .WithMessage($"'{{PropertyName}}' must be one of: {string.Join(", ", Themes.All)}.");

    public static IRuleBuilderOptions<T, string> Role<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .Must(value => Roles.All.Contains(value, StringComparer.Ordinal))
            .WithMessage($"'{{PropertyName}}' must be one of: {string.Join(", ", Roles.All)}.");

    /// <summary>An IANA time zone id that this machine can resolve.</summary>
    public static IRuleBuilderOptions<T, string> TimeZoneId<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .Must(value => TimeZoneInfo.TryFindSystemTimeZoneById(value, out _))
            .WithMessage("'{PropertyName}' is not a known time zone.");

    /// <summary>A three-letter ISO 4217 code in upper case.</summary>
    public static IRuleBuilderOptions<T, string> Currency<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Matches("^[A-Z]{3}$").WithMessage("'{PropertyName}' must be a three-letter ISO 4217 code.");

    public static IRuleBuilderOptions<T, double?> Latitude<T>(this IRuleBuilder<T, double?> rule) =>
        rule.InclusiveBetween(-90, 90);

    public static IRuleBuilderOptions<T, double?> Longitude<T>(this IRuleBuilder<T, double?> rule) =>
        rule.InclusiveBetween(-180, 180);

    public static IRuleBuilderOptions<T, decimal?> PricePerKwh<T>(this IRuleBuilder<T, decimal?> rule) =>
        rule.GreaterThanOrEqualTo(0);
}
