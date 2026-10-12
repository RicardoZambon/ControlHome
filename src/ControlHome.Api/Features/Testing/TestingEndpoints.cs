using ControlHome.Api.Http;
using ControlHome.Application.Auth;
using ControlHome.Application.Common;
using ControlHome.Application.Devices;
using ControlHome.Application.Runtime;
using ControlHome.Application.Security;
using ControlHome.Domain.Capabilities;
using ControlHome.Infrastructure.Devices;
using ControlHome.Infrastructure.Persistence;
using ControlHome.Infrastructure.Setup;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Api.Features.Testing;

/// <summary>
/// Test hooks, mapped only when <c>ASPNETCORE_ENVIRONMENT=Testing</c> (integration and E2E tests); absent from the
/// OpenAPI document and from every other environment (404 there). Anonymous, but like every mutation they need the
/// same-origin <c>Origin</c> and <c>X-ControlHome: 1</c> headers.
/// <list type="bullet">
/// <item><c>POST /api/_test/reset</c> → 204: stops every integration runtime, empties the database (roles kept) and resets
/// the setup code, step-up grants/lockouts and command idempotency keys.</item>
/// <item><c>POST /api/_test/devices/{attributeId}/state</c> with <c>{ "value": StateValue, "observedAtUtc"?: string }</c> →
/// 204 once processed: the value goes through the state pipeline exactly as if the attribute's connector had reported it
/// (dedupe, cache, hub <c>StateChanged</c>, write-behind). 404 unknown attribute; 409 when the runtime is not active.</item>
/// <item><c>POST /api/_test/clock</c> with <c>{ "advanceSeconds": number }</c> → 200 <c>{ nowUtc, offsetSeconds }</c>: moves
/// the runtime clock forward (the clock of connector runtimes — Simulated ticks every 5 s of it — of report timestamps and,
/// later, of the scheduler). It runs in real time otherwise; timers that become due fire at once.</item>
/// </list>
/// </summary>
public static class TestingEndpoints
{
    public const string EnvironmentName = "Testing";

    /// <summary>Marker text of <c>GET /api/_test/throw</c>; tests assert it never reaches a response body.</summary>
    public const string ExceptionMarker = "controlhome-test-exception-detail";

    private static readonly TimeSpan _injectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The Testing environment's runtime clock: real time that <c>POST /api/_test/clock</c> can move forward.</summary>
    public static IServiceCollection AddTestingServices(this IServiceCollection services)
    {
        services.TryAddSingleton<AdjustableTimeProvider>();
        services.Replace(ServiceDescriptor.Singleton(sp => new RuntimeClock(sp.GetRequiredService<AdjustableTimeProvider>())));
        return services;
    }

    public static IEndpointRouteBuilder MapTestingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/_test").AllowAnonymous().ExcludeFromDescription();

        group.MapPost("/reset", ResetAsync);
        group.MapGet("/throw", Throw);
        group.MapPost("/devices/{attributeId:int}/state", InjectStateAsync);
        group.MapPost("/clock", AdvanceClock);

        return app;
    }

    /// <summary>Always fails: proves unhandled exceptions become a bare 500 <c>internal.error</c> outside Development.</summary>
    private static IResult Throw() => throw new InvalidOperationException(ExceptionMarker);

    /// <summary>
    /// Stops the device runtime's integrations, deletes all app and Identity rows (roles re-seeded) and puts the setup code
    /// back to its startup value; in-memory step-up grants, lockouts (incl. those counted for unknown user names) and command
    /// idempotency keys are forgotten too.
    /// </summary>
    private static async Task<NoContent> ResetAsync(
        DatabaseReset reset,
        SetupCodeProvider setupCode,
        UnknownUserLockouts unknownUsers,
        DeviceRuntime runtime,
        SafetyGrantStore grants,
        CommandTracker commands,
        CancellationToken cancellationToken)
    {
        await runtime.ResetAsync();
        await reset.ResetAsync(cancellationToken);
        setupCode.Reset();
        unknownUsers.Clear();
        grants.Clear();
        commands.Reset();
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> InjectStateAsync(
        int attributeId,
        TestStateRequest request,
        IRuntimeStatus status,
        DeviceRegistry registry,
        StateProcessor processor)
    {
        if (status.Mode != RuntimeMode.Active)
        {
            return Problems.From(AppError.Conflict(ErrorCodes.CommandsUnavailable, "The device runtime is not active in this process."));
        }

        if (!registry.TryGet(attributeId, out _))
        {
            return Problems.From(AppError.NotFound(ErrorCodes.DevicesNotFound, "No such attribute."));
        }

        processor.InjectState(attributeId, request.Value ?? StateValue.Null, request.ObservedAtUtc);
        await processor.WhenIdleAsync(_injectTimeout);
        return TypedResults.NoContent();
    }

    private static Results<Ok<TestClockDto>, ProblemHttpResult> AdvanceClock(TestClockRequest request, RuntimeClock clock)
    {
        if (clock.Time is not AdjustableTimeProvider adjustable)
        {
            return Problems.From(AppError.Conflict(ErrorCodes.RequestInvalid, "The runtime clock cannot be moved in this host."));
        }

        if (request.AdvanceSeconds is { } seconds)
        {
            if (!double.IsFinite(seconds) || seconds < 0)
            {
                return Problems.From(AppError.Validation(ErrorCodes.ValidationFailed, "advanceSeconds must be zero or more."));
            }

            adjustable.Advance(TimeSpan.FromSeconds(seconds));
        }

        return TypedResults.Ok(new TestClockDto(adjustable.GetUtcNow(), adjustable.Offset.TotalSeconds));
    }

    /// <summary>A value to inject (test hook).</summary>
    /// <param name="ObservedAtUtc">When it was observed; omitted means now (runtime clock).</param>
    public sealed record TestStateRequest(StateValue? Value, DateTimeOffset? ObservedAtUtc);

    /// <summary>How far to move the runtime clock (test hook); omitted just reads it.</summary>
    public sealed record TestClockRequest(double? AdvanceSeconds);

    /// <summary>The runtime clock after the call.</summary>
    public sealed record TestClockDto(DateTimeOffset NowUtc, double OffsetSeconds);
}
