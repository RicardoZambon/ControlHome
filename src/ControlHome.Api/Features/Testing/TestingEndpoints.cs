using ControlHome.Infrastructure.Persistence;
using ControlHome.Infrastructure.Setup;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Testing;

/// <summary>
/// Test hooks, mapped only when <c>ASPNETCORE_ENVIRONMENT=Testing</c> (integration and E2E tests); absent from the
/// OpenAPI document and from every other environment.
/// </summary>
public static class TestingEndpoints
{
    public const string EnvironmentName = "Testing";

    /// <summary>Marker text of <c>GET /api/_test/throw</c>; tests assert it never reaches a response body.</summary>
    public const string ExceptionMarker = "controlhome-test-exception-detail";

    public static IEndpointRouteBuilder MapTestingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/_test").AllowAnonymous().ExcludeFromDescription();

        group.MapPost("/reset", ResetAsync);
        group.MapGet("/throw", Throw);

        return app;
    }

    /// <summary>Always fails: proves unhandled exceptions become a bare 500 <c>internal.error</c> outside Development.</summary>
    private static IResult Throw() => throw new InvalidOperationException(ExceptionMarker);

    /// <summary>
    /// Deletes all app and Identity rows (roles re-seeded) and puts the setup code back to its startup value (the configured
    /// <c>Setup:Code</c>, or a fresh random one, with no failed attempts), returning to the post-initialization state.
    /// Sign-in lockouts live in the user rows, so they go with them.
    /// </summary>
    private static async Task<NoContent> ResetAsync(DatabaseReset reset, SetupCodeProvider setupCode, CancellationToken cancellationToken)
    {
        await reset.ResetAsync(cancellationToken);
        setupCode.Reset();
        return TypedResults.NoContent();
    }
}
