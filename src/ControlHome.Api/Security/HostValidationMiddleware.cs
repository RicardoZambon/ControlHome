using System.Net;
using ControlHome.Api.Http;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;

namespace ControlHome.Api.Security;

/// <summary>
/// Requests whose <c>Host</c> (after forwarded headers) is not one of the app's names (<see cref="HostNamePolicy"/>) get
/// 403 ProblemDetails <c>request.host_denied</c>, whatever the method or path: a DNS rebinding page could otherwise use the
/// anonymous endpoints from a household member's browser (and read GET responses). Each source is logged and audited
/// (<c>security.host_denied</c>) at most once per <see cref="NetworkDeniedAuditThrottle.Window"/>.
/// </summary>
internal sealed class HostValidationMiddleware(
    RequestDelegate next,
    HostNamePolicy policy,
    TimeProvider timeProvider,
    IDatabaseStatus database,
    ILogger<HostValidationMiddleware> logger)
{
    /// <summary>A denied request never waits longer than this for its audit row.</summary>
    private static readonly TimeSpan _auditTimeout = TimeSpan.FromSeconds(5);

    private const int MaxAuditedLength = 200;

    // Its own throttle: host denials must not use up (or be hidden by) the network denials' budget.
    private readonly NetworkDeniedAuditThrottle _throttle = new(timeProvider);

    public async Task InvokeAsync(HttpContext context)
    {
        if (policy.IsAllowed(context.Request.Host))
        {
            await next(context);
            return;
        }

        var address = ClientNetworkPolicy.Normalize(context.Connection.RemoteIpAddress);
        if (address is not null && _throttle.TryAcquire(address))
        {
            await ReportAsync(context, address);
        }

        await Problems.From(AppError.Forbidden(ErrorCodes.RequestHostDenied, "This host name is not one of this app's addresses."))
            .ExecuteAsync(context);
    }

    private async Task ReportAsync(HttpContext context, IPAddress address)
    {
        var host = Truncate(context.Request.Host.Value ?? string.Empty);
        if (!database.IsReady)
        {
            logger.LogWarning(
                "Denied a request from {RemoteIp} for host {Host}: not an allowed host name (not audited, database {DatabaseState})",
                address,
                host,
                database.State);
            return;
        }

        logger.LogWarning("Denied a request from {RemoteIp} for host {Host}: not an allowed host name", address, host);
        try
        {
            using var timeout = new CancellationTokenSource(_auditTimeout);
            var details = new
            {
                host,
                method = context.Request.Method,
                path = Truncate(context.Request.Path.Value ?? string.Empty),
            };
            await context.RequestServices.GetRequiredService<IAuditLog>()
                .WriteAsync(new AuditEvent(AuditActions.HostDenied, Details: details), timeout.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The 403 matters more than its audit row.
            logger.LogWarning(ex, "Could not audit the denied request from {RemoteIp}", address);
        }
    }

    private static string Truncate(string value) => value.Length > MaxAuditedLength ? value[..MaxAuditedLength] : value;
}
