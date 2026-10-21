namespace ControlHome.Connectors.LgThinQ;

/// <summary>
/// The countries the ThinQ Connect API serves and their region (<c>https://api-{region}.lgthinq.com</c>), ported from
/// pythinqconnect's <c>country.py</c> (<c>SUPPORTED_COUNTRIES</c>). The country must be the one the LG account was created
/// in, not where the user is now.
/// </summary>
internal static class LgCountries
{
    public const string Default = "BR";

    private static readonly string[] _kic =
    [
        "AU", "BD", "CN", "HK", "ID", "IN", "JP", "KH", "KR", "LA", "LK", "MM", "MY", "NP", "NZ", "PH", "SG", "TH", "TW", "VN",
    ];

    private static readonly string[] _aic =
    [
        "AG", "AR", "AW", "BB", "BO", "BR", "BS", "BZ", "CA", "CL", "CO", "CR", "CU", "DM", "DO", "EC", "GD", "GT", "GY", "HN",
        "HT", "JM", "KN", "LC", "MX", "NI", "PA", "PE", "PR", "PY", "SR", "SV", "TT", "US", "UY", "VC", "VE",
    ];

    private static readonly string[] _eic =
    [
        "AE", "AF", "AL", "AM", "AO", "AT", "AZ", "BA", "BE", "BF", "BG", "BH", "BJ", "BY", "CD", "CF", "CG", "CH", "CI", "CM",
        "CV", "CY", "CZ", "DE", "DJ", "DK", "DZ", "EE", "EG", "ES", "ET", "FI", "FR", "GA", "GB", "GE", "GH", "GM", "GN", "GQ",
        "GR", "HR", "HU", "IE", "IL", "IQ", "IR", "IS", "IT", "JO", "KE", "KG", "KW", "KZ", "LB", "LR", "LT", "LU", "LV", "LY",
        "MA", "MD", "ME", "MK", "ML", "MR", "MT", "MU", "MW", "NE", "NG", "NL", "NO", "OM", "PK", "PL", "PS", "PT", "QA", "RO",
        "RS", "RU", "RW", "SA", "SD", "SE", "SI", "SK", "SL", "SN", "SO", "ST", "SY", "TD", "TG", "TN", "TR", "TZ", "UA", "UG",
        "UZ", "XK", "YE", "ZA", "ZM",
    ];

    private static readonly Dictionary<string, string> _regions = _kic.Select(c => (c, "kic"))
        .Concat(_aic.Select(c => (c, "aic")))
        .Concat(_eic.Select(c => (c, "eic")))
        .ToDictionary(p => p.c, p => p.Item2, StringComparer.Ordinal);

    /// <summary>Every supported country code, alphabetically.</summary>
    public static IReadOnlyList<string> All { get; } = [.. _regions.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The region (<c>kic</c>, <c>aic</c> or <c>eic</c>) of <paramref name="country"/>, or <c>null</c> when unsupported.</summary>
    public static string? RegionOf(string? country) => country is null ? null : _regions.GetValueOrDefault(country.ToUpperInvariant());

    /// <summary>The API base URL for <paramref name="country"/> (e.g. BR → <c>https://api-aic.lgthinq.com/</c>).</summary>
    /// <exception cref="ArgumentException">The country is not supported.</exception>
    public static Uri BaseUrlOf(string country) =>
        RegionOf(country) is { } region
            ? new Uri($"https://api-{region}.lgthinq.com/")
            : throw new ArgumentException($"LG ThinQ does not support the country '{country}'.", nameof(country));

    /// <summary>
    /// A time zone for the energy days of an account in <paramref name="country"/> when none is configured: the country's
    /// most populous zone for a few large countries, else <paramref name="fallback"/>.
    /// </summary>
    public static TimeZoneInfo DefaultTimeZone(string country, TimeZoneInfo fallback)
    {
        var id = country.ToUpperInvariant() switch
        {
            "BR" => "America/Sao_Paulo",
            "AR" => "America/Argentina/Buenos_Aires",
            "MX" => "America/Mexico_City",
            "CL" => "America/Santiago",
            "CO" => "America/Bogota",
            "PE" => "America/Lima",
            "KR" => "Asia/Seoul",
            "IN" => "Asia/Kolkata",
            "DE" => "Europe/Berlin",
            "GB" => "Europe/London",
            "PT" => "Europe/Lisbon",
            "ES" => "Europe/Madrid",
            _ => null,
        };
        return id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : fallback;
    }
}
