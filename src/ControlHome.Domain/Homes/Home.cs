namespace ControlHome.Domain.Homes;

/// <summary>The single home this installation controls (always <see cref="SingletonId"/>).</summary>
public sealed class Home
{
    /// <summary>The only valid <see cref="Id"/>: an installation manages exactly one home.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public string Name { get; set; } = string.Empty;

    /// <summary>IANA time zone id (e.g. <c>America/Sao_Paulo</c>).</summary>
    public string TimeZoneId { get; set; } = "UTC";

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    /// <summary>Default UI/content locale: <c>pt-BR</c> or <c>en</c>.</summary>
    public string Locale { get; set; } = "en";

    /// <summary>ISO 4217 currency code (e.g. <c>BRL</c>).</summary>
    public string Currency { get; set; } = "BRL";

    public Tariff Tariff { get; set; } = new();

    /// <summary>Set once, when the first-run setup completes; never cleared.</summary>
    public DateTimeOffset? SetupCompletedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
