using System.ComponentModel;
using System.Security.Claims;
using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Common;
using ControlHome.Application.Devices;
using ControlHome.Application.Identity;
using ControlHome.Infrastructure.Cameras;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ControlHome.Api.Features.Cameras;

/// <summary>
/// Camera live view, behind the app's own authorization (anyone signed in may watch): the browser talks only to these two
/// endpoints, the server talks to the go2rtc sidecar on its loopback API. Camera URLs, credentials and go2rtc's own API
/// never reach the browser; stream names are opaque ids the server picks.
/// </summary>
public static class CameraEndpoints
{
    /// <summary>The go2rtc restreamer and the live-session limit.</summary>
    public static IServiceCollection AddCameraLiveView(this IServiceCollection services)
    {
        services.AddCameraRestreamer();
        services.TryAddSingleton<CameraLiveSessions>();
        return services;
    }

    public static IEndpointRouteBuilder MapCameraEndpoints(this IEndpointRouteBuilder app)
    {
        var cameras = app.MapGroup("/api/endpoints")
            .WithTags("Cameras")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        cameras.MapGet("/{id:guid}/camera/stream", StreamAsync)
            .WithName("openCameraStream")
            .WithSummary("Live view over a WebSocket (go2rtc's MSE / WebRTC-signalling protocol), relayed by the app.")
            .WithDescription(
                "Open as a WebSocket from the app's own origin (the session cookie authenticates it). Messages are those of " +
                "go2rtc's video-rtc.js: send {\"type\":\"mse\",\"value\":\"<codecs>\"} (or mp4, webrtc/offer with a receive-only " +
                "SDP, webrtc/candidate) and receive binary fMP4 segments and JSON messages. Anything else sent is dropped; " +
                "binary or larger than 64 KB closes it. quality: main (default) or sub. Errors before the upgrade: 400 " +
                "validation.failed (quality); 404 endpoints.not_found or cameras.no_stream; 400 request.websocket_required " +
                "(not a WebSocket request); 503 cameras.live_disabled, commands.unavailable, cameras.live_unavailable or " +
                "cameras.live_busy.")
            .RequireAuthorization(AuthorizationPolicies.Viewer)
            .Produces(StatusCodes.Status101SwitchingProtocols)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        cameras.MapPost("/{id:guid}/camera/webrtc", WebRtcAsync)
            .WithName("exchangeCameraWebRtc")
            .WithSummary("WebRTC live view: exchanges the browser's receive-only SDP offer for the restreamer's answer.")
            .WithDescription(
                "The answer carries the restreamer's ICE candidates (its LAN address, WebRTC port 8555); media then flows " +
                "directly between the browser and go2rtc. 400 validation.failed (errors.sdp: not an offer, or it would send " +
                "media; errors.quality); 404 endpoints.not_found or cameras.no_stream; 503 cameras.live_disabled, " +
                "commands.unavailable or cameras.live_unavailable.")
            .RequireAuthorization(AuthorizationPolicies.Viewer)
            .WithValidation<CameraWebRtcOfferRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<Results<EmptyHttpResult, ProblemHttpResult>> StreamAsync(
        Guid id,
        [FromQuery, Description("main (default) or sub (the lighter extra stream).")] string? quality,
        HttpContext context,
        ClaimsPrincipal user,
        CameraStreamService cameras,
        ICameraRestreamer restreamer,
        CameraLiveSessions sessions,
        IServiceScopeFactory scopes,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (CameraStreamQualities.Parse(quality) is not { } parsed)
        {
            return Problems.From(AppError.FieldErrors(new Dictionary<string, string[]> { ["quality"] = ["'quality' must be main or sub."] }));
        }

        // Not a camera (or no such endpoint) is answered before anything else, and nothing reaches go2rtc for a plain GET.
        var camera = await cameras.CheckCameraAsync(id, cancellationToken);
        if (camera.IsFailure)
        {
            return Problems.From(camera.Error);
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            return Problems.From(AppError.Validation(ErrorCodes.RequestWebSocketRequired, "Open this endpoint as a WebSocket."));
        }

        var opened = await cameras.OpenAsync(id, parsed, cancellationToken);
        if (opened.IsFailure)
        {
            return Problems.From(opened.Error);
        }

        using var lease = sessions.TryEnter();
        if (lease is null)
        {
            return Problems.From(AppError.Unavailable(ErrorCodes.CamerasLiveBusy, "Too many live views are open right now."));
        }

        // Like the live-update hub: the session is checked against the user row now and every minute while it is open.
        var session = await UserSession.FromAsync(user, scopes, cancellationToken);
        if (session is null || !await session.IsValidAsync(cancellationToken))
        {
            return Problems.From(AppError.Forbidden(ErrorCodes.AuthForbidden, "The session is no longer valid."));
        }

        System.Net.WebSockets.WebSocket upstream;
        try
        {
            upstream = await restreamer.ConnectAsync(opened.Value.StreamName, cancellationToken);
        }
        catch (CameraRestreamerException)
        {
            return Problems.From(AppError.Unavailable(ErrorCodes.CamerasLiveUnavailable, "The live view service is not available right now."));
        }

        using (upstream)
        {
            using var client = await context.WebSockets.AcceptWebSocketAsync();
            await CameraStreamRelay.RunAsync(client, upstream, session.IsValidAsync, IdentitySetup.SecurityStampValidationInterval, time, cancellationToken);
        }

        return TypedResults.Empty;
    }

    private static async Task<Results<Ok<CameraWebRtcAnswerDto>, ProblemHttpResult>> WebRtcAsync(
        Guid id,
        CameraWebRtcOfferRequest request,
        CameraStreamService cameras,
        CancellationToken cancellationToken) =>
        (await cameras.ExchangeWebRtcAsync(id, request, cancellationToken)).ToHttpResult();

    /// <summary>A signed-in user's session as it was when the live view opened (user id and security stamp).</summary>
    private sealed class UserSession(Guid userId, string? stamp, IServiceScopeFactory scopes)
    {
        public static async Task<UserSession?> FromAsync(ClaimsPrincipal user, IServiceScopeFactory scopes, CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            return Guid.TryParse(users.GetUserId(user), out var id)
                ? new UserSession(id, user.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType), scopes)
                : null;
        }

        /// <summary>The user still exists, is enabled and has the same security stamp (no password, role or access change).</summary>
        public async Task<bool> IsValidAsync(CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var row = await users.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.SecurityStamp, u.IsDisabled })
                .SingleOrDefaultAsync(cancellationToken);
            return row is not null && !row.IsDisabled && string.Equals(row.SecurityStamp, stamp, StringComparison.Ordinal);
        }
    }
}

/// <summary>Counts open live views against <see cref="Go2RtcOptions.MaxLiveSessions"/> (each holds a go2rtc consumer).</summary>
public sealed class CameraLiveSessions(IOptions<Go2RtcOptions> options)
{
    private readonly int _max = Math.Max(1, options.Value.MaxLiveSessions);
    private int _open;

    public int Open => Volatile.Read(ref _open);

    /// <summary>A lease to dispose when the view closes, or <c>null</c> when the limit is reached.</summary>
    public IDisposable? TryEnter()
    {
        if (Interlocked.Increment(ref _open) > _max)
        {
            Interlocked.Decrement(ref _open);
            return null;
        }

        return new Lease(this);
    }

    private sealed class Lease(CameraLiveSessions owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._open);
            }
        }
    }
}
