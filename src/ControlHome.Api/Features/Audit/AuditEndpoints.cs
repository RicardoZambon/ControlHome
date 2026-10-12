using ControlHome.Api.Hosting;
using ControlHome.Application.Auditing;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Audit;

/// <summary>The audit log (Admin only). Implemented by Task 0.2-D.</summary>
public static class AuditEndpoints
{
    public const int DefaultPageSize = 50;

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit")
            .WithTags("Audit")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("", ListAsync)
            .WithName("listAudit")
            .WithSummary("Audit entries, newest first; pass nextCursor back as cursor for the next page.");

        return app;
    }

    private static Task<Ok<PagedResult<AuditEntryDto>>> ListAsync(
        long? cursor,
        CancellationToken cancellationToken,
        int take = DefaultPageSize) =>
        throw new NotImplementedException("0.2-D");
}
