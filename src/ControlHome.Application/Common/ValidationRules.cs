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
    public const int TimeZoneIdMaxLength = 64;

    /// <summary>
    /// The characters a user name may contain (<c>[A-Za-z0-9._@-]</c>). Identity's <c>User.AllowedUserNameCharacters</c>
    /// is set to this same string, so validation and Identity can never disagree about a name.
    /// </summary>
    public const string UserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._@-";

    /// <summary>3–64 characters from <see cref="UserNameCharacters"/>.</summary>
    public static IRuleBuilderOptions<T, string> UserName<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .Length(UserNameMinLength, UserNameMaxLength)
            .Must(value => value is null || value.All(c => UserNameCharacters.Contains(c, StringComparison.Ordinal))) // null: NotEmpty
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

    /// <summary>A canonical IANA time zone id (exact case, e.g. <c>America/Sao_Paulo</c>); see <see cref="IsIanaTimeZoneId"/>.</summary>
    public static IRuleBuilderOptions<T, string> TimeZoneId<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .Must(value => string.IsNullOrWhiteSpace(value) || IsIanaTimeZoneId(value)) // a missing value is NotEmpty's error
            .WithMessage("'{PropertyName}' must be an IANA time zone id, e.g. America/Sao_Paulo.");

    /// <summary>
    /// True for a canonical IANA id that resolves on every OS the app runs on: it is known to the runtime, it is an IANA id
    /// (not a Windows id such as <c>E. South America Standard Time</c>), it is spelled exactly as the runtime names it
    /// (<c>america/sao_paulo</c> is not) and it maps to a Windows zone (which rules out tzdata file names such as
    /// <c>Factory</c> or <c>right/America/Sao_Paulo</c>). Browsers (<c>Intl</c>) accept every id that passes.
    /// </summary>
    public static bool IsIanaTimeZoneId(string? value) =>
        value is { Length: > 0 and <= TimeZoneIdMaxLength }
        && TimeZoneInfo.TryFindSystemTimeZoneById(value, out var zone)
        && zone.HasIanaId
        && string.Equals(zone.Id, value, StringComparison.Ordinal)
        && TimeZoneInfo.TryConvertIanaIdToWindowsId(value, out _);

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
