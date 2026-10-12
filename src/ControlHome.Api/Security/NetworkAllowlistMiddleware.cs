using System.Net;
using ControlHome.Api.Http;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;

namespace ControlHome.Api.Security;

/// <summary>
/// Requests from outside <see cref="NetworkOptions.AllowedClientNetworks"/> (client address after forwarded headers) get
/// 403 ProblemDetails <c>request.network_denied</c>. Each source is logged and audited (<c>security.network_denied</c>) at
/// most once per <see cref="NetworkDeniedAuditThrottle.Window"/>; nothing is audited while the database is not ready.
/// </summary>
internal sealed class NetworkAllowlistMiddleware(
    RequestDelegate next,
    ClientNetworkPolicy policy,
    NetworkDeniedAuditThrottle throttle,
    IDatabaseStatus database,
    ILogger<NetworkAllowlistMiddleware> logger)
{
    /// <summary>A denied request never waits longer than this for its audit row.</summary>
    private static readonly TimeSpan _auditTimeout = TimeSpan.FromSeconds(5);

    private const int MaxAuditedPathLength = 200;

    public async Task InvokeAsync(HttpContext context)
    {
        var address = ClientNetworkPolicy.Normalize(context.Connection.RemoteIpAddress);
        if (address is null || policy.IsAllowed(address))
        {
            await next(context);
            return;
        }

        if (throttle.TryAcquire(address))
        {
            await ReportAsync(context, address);
        }

        await Problems.From(AppError.Forbidden(ErrorCodes.RequestNetworkDenied, "Requests from this network are not allowed."))
            .ExecuteAsync(context);
    }

    private async Task ReportAsync(HttpContext context, IPAddress address)
    {
        if (!database.IsReady)
        {
            logger.LogWarning(
                "Denied a request from {RemoteIp}: outside Network:AllowedClientNetworks (not audited, database {DatabaseState})",
                address,
                database.State);
            return;
        }

        logger.LogWarning("Denied a request from {RemoteIp}: outside Network:AllowedClientNetworks", address);
        try
        {
            using var timeout = new CancellationTokenSource(_auditTimeout);
            var path = context.Request.Path.Value ?? string.Empty;
            var details = new
            {
                method = context.Request.Method,
                path = path.Length > MaxAuditedPathLength ? path[..MaxAuditedPathLength] : path,
            };
            await context.RequestServices.GetRequiredService<IAuditLog>()
                .WriteAsync(new AuditEvent(AuditActions.NetworkDenied, Details: details), timeout.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The 403 matters more than its audit row.
            logger.LogWarning(ex, "Could not audit the denied request from {RemoteIp}", address);
        }
    }
}
