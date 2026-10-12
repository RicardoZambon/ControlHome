using System.Globalization;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Domain.Homes;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Homes;

/// <summary>The home's settings (created by the first-run setup).</summary>
public sealed class HomeService(IControlHomeDb db, IAuditLog auditLog)
{
    public const string HomeTarget = "home";

    public async Task<Result<HomeDto>> GetAsync(CancellationToken cancellationToken)
    {
        var home = await db.Homes.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return home is null ? NotSetUp() : ToDto(home);
    }

    /// <summary>Applies the changes and audits <c>home.updated</c> with the names of the changed fields.</summary>
    public async Task<Result<HomeDto>> UpdateAsync(UpdateHomeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var home = await db.Homes.SingleOrDefaultAsync(cancellationToken);
        if (home is null)
        {
            return NotSetUp();
        }

        var changed = new List<string>();
        Set(home.Name, request.Name.Trim(), v => home.Name = v, "name");
        Set(home.TimeZoneId, request.TimeZoneId, v => home.TimeZoneId = v, "timeZoneId");
        Set(home.Latitude, request.Latitude, v => home.Latitude = v, "latitude");
        Set(home.Longitude, request.Longitude, v => home.Longitude = v, "longitude");
        Set(home.Locale, request.Locale, v => home.Locale = v, "locale");
        Set(home.Currency, request.Currency, v => home.Currency = v, "currency");
        Set(home.Tariff.PricePerKwh, request.PricePerKwh, v => home.Tariff = new Tariff { Kind = home.Tariff.Kind, PricePerKwh = v }, "pricePerKwh");

        if (changed.Count > 0)
        {
            // Saves the home together with the audit entry.
            await auditLog.WriteAsync(
                new AuditEvent(
                    AuditActions.HomeUpdated,
                    HomeTarget,
                    home.Id.ToString(CultureInfo.InvariantCulture),
                    new { Fields = changed }),
                cancellationToken);
        }

        return ToDto(home);

        void Set<T>(T current, T value, Action<T> assign, string field)
        {
            if (!EqualityComparer<T>.Default.Equals(current, value))
            {
                assign(value);
                changed.Add(field);
            }
        }
    }

    private static AppError NotSetUp() =>
        AppError.NotFound(ErrorCodes.SetupNotCompleted, "The home has not been set up yet.");

    private static HomeDto ToDto(Home home) => new(
        home.Id,
        home.Name,
        home.TimeZoneId,
        home.Latitude,
        home.Longitude,
        home.Locale,
        home.Currency,
        home.Tariff.PricePerKwh,
        home.SetupCompletedAtUtc,
        home.CreatedAtUtc);
}
