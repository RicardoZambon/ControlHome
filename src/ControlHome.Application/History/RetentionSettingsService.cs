using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Domain.History;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.History;

/// <summary>How long history is kept (<see cref="RetentionSettings"/>; the defaults until an Admin saves them).</summary>
public sealed class RetentionSettingsService(IControlHomeDb db, IAuditLog auditLog, TimeProvider timeProvider)
{
    public const string Target = "retention";

    public async Task<RetentionSettingsDto> GetAsync(CancellationToken cancellationToken) =>
        (await db.RetentionSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? new RetentionSettings()).ToDto();

    /// <summary>Saves the settings and audits <c>settings.retention_updated</c> with the changed fields (the next purge applies them).</summary>
    public async Task<Result<RetentionSettingsDto>> UpdateAsync(UpdateRetentionSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await db.RetentionSettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new RetentionSettings();
            db.RetentionSettings.Add(settings);
        }

        var changed = new List<string>();
        Set(settings.StateLogDays, request.StateLogDays, v => settings.StateLogDays = v, "stateLogDays");
        Set(settings.ShortTermStatsDays, request.ShortTermStatsDays, v => settings.ShortTermStatsDays = v, "shortTermStatsDays");
        Set(settings.DeviceEventDays, request.DeviceEventDays, v => settings.DeviceEventDays = v, "deviceEventDays");
        Set(settings.AuditLogDays, request.AuditLogDays, v => settings.AuditLogDays = v, "auditLogDays");

        if (changed.Count > 0)
        {
            settings.UpdatedAtUtc = timeProvider.GetUtcNow();

            // Saves the settings together with the audit entry.
            await auditLog.WriteAsync(new AuditEvent(AuditActions.RetentionSettingsUpdated, Target, null, new { Fields = changed }), cancellationToken);
        }

        return settings.ToDto();

        void Set(int current, int value, Action<int> assign, string field)
        {
            if (current != value)
            {
                assign(value);
                changed.Add(field);
            }
        }
    }
}
