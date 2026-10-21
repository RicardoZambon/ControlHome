using ControlHome.Domain.History;
using FluentValidation;

namespace ControlHome.Application.History;

/// <summary>The resolutions of <c>GET /api/attributes/{id}/history</c>.</summary>
public static class HistoryResolutions
{
    /// <summary>Raw up to 6 h, 5-minute statistics up to 7 days, hourly beyond (raw only for attributes without statistics).</summary>
    public const string Auto = "auto";

    /// <summary>Every recorded change (<c>StateLog</c>).</summary>
    public const string Raw = "raw";

    /// <summary>5-minute statistics.</summary>
    public const string FiveMinutes = "5m";

    /// <summary>Hourly statistics.</summary>
    public const string Hourly = "1h";

    public static IReadOnlyList<string> All { get; } = [Auto, Raw, FiveMinutes, Hourly];
}

/// <summary>One point of a history series.</summary>
/// <param name="T">
/// Raw: when the value was observed (the first point may sit at the range's start, carrying the value in effect then).
/// Statistics: the bucket's start.
/// </param>
/// <param name="V">
/// Raw: the number (booleans as 1/0), or <c>null</c> for a string or unknown value. Measurements: the time-weighted mean.
/// Counters: the increase in the bucket (for energy, Wh used).
/// </param>
/// <param name="Min">Measurements (5m/1h): the lowest value in the bucket.</param>
/// <param name="Max">Measurements (5m/1h): the highest value in the bucket.</param>
/// <param name="S">Raw: a string value (enum states such as <c>heat</c>).</param>
public sealed record HistoryPointDto(DateTimeOffset T, double? V, double? Min, double? Max, string? S);

/// <summary>An attribute's history over a range, oldest point first (at most 5 000 points: the most recent ones).</summary>
/// <param name="Resolution">The resolution served: <c>raw</c>, <c>5m</c> or <c>1h</c>.</param>
/// <param name="Unit">The attribute's canonical unit (<c>W</c>, <c>Wh</c>, <c>°C</c>, <c>%</c>, …), if it has one.</param>
public sealed record HistorySeriesDto(int AttributeId, string Resolution, string? Unit, IReadOnlyList<HistoryPointDto> Points);

/// <summary>The query of <c>GET /api/attributes/{id}/history</c>.</summary>
/// <param name="From">Start (inclusive); default <see cref="To"/> minus 24 h.</param>
/// <param name="To">End (exclusive); default now (the runtime clock).</param>
/// <param name="Resolution">One of <see cref="HistoryResolutions.All"/>; default <c>auto</c>.</param>
public sealed record HistoryQuery(DateTimeOffset? From, DateTimeOffset? To, string? Resolution);

/// <summary>The energy one endpoint used in one hour.</summary>
/// <param name="Source"><c>counter</c> (its energy meter) or <c>integratedPower</c> (power readings integrated: an estimate).</param>
/// <param name="Estimated">Integrated from power readings rather than measured by a meter.</param>
public sealed record EnergyHourlyDto(Guid EndpointId, DateTimeOffset HourStartUtc, double Wh, string Source, bool Estimated);

/// <summary>The query of <c>GET /api/energy/hourly</c>.</summary>
/// <param name="From">Hours overlapping <c>[from, to)</c>; default <see cref="To"/> minus 24 h.</param>
/// <param name="To">Default now (the runtime clock).</param>
/// <param name="EndpointId">One endpoint only.</param>
public sealed record EnergyQuery(DateTimeOffset? From, DateTimeOffset? To, Guid? EndpointId);

/// <summary>How long history is kept (hourly statistics and hourly energy are kept forever).</summary>
/// <param name="StateLogDays">Raw history (every change), used for ranges up to 6 h.</param>
/// <param name="ShortTermStatsDays">5-minute statistics, used for ranges up to 7 days.</param>
/// <param name="DeviceEventDays">Momentary events (button presses, doorbell rings).</param>
/// <param name="AuditLogDays">The audit log.</param>
public sealed record RetentionSettingsDto(int StateLogDays, int ShortTermStatsDays, int DeviceEventDays, int AuditLogDays);

/// <summary>Changes how long history is kept (Admin).</summary>
public sealed record UpdateRetentionSettingsRequest(int StateLogDays, int ShortTermStatsDays, int DeviceEventDays, int AuditLogDays);

public sealed class UpdateRetentionSettingsRequestValidator : AbstractValidator<UpdateRetentionSettingsRequest>
{
    public const int MinHistoryDays = 1;
    public const int MaxHistoryDays = 365;
    public const int MinAuditLogDays = 30;
    public const int MaxAuditLogDays = 3650;

    public UpdateRetentionSettingsRequestValidator()
    {
        RuleFor(x => x.StateLogDays).InclusiveBetween(MinHistoryDays, MaxHistoryDays);
        RuleFor(x => x.ShortTermStatsDays).InclusiveBetween(MinHistoryDays, MaxHistoryDays);
        RuleFor(x => x.DeviceEventDays).InclusiveBetween(MinHistoryDays, MaxHistoryDays);
        RuleFor(x => x.AuditLogDays).InclusiveBetween(MinAuditLogDays, MaxAuditLogDays);
    }
}

internal static class RetentionSettingsMapping
{
    public static RetentionSettingsDto ToDto(this RetentionSettings settings) =>
        new(settings.StateLogDays, settings.ShortTermStatsDays, settings.DeviceEventDays, settings.AuditLogDays);
}
