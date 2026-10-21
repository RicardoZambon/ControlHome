using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.History;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Settings;

/// <summary>System settings (Admin): how long history is kept.</summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings")
            .WithTags("Settings")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/retention", GetRetentionAsync)
            .WithName("getRetentionSettings")
            .WithSummary("How long history is kept (the defaults until saved).")
            .RequireAuthorization(AuthorizationPolicies.Admin);

        group.MapPut("/retention", UpdateRetentionAsync)
            .WithName("updateRetentionSettings")
            .WithSummary("Changes how long history is kept; the hourly retention job applies it.")
            .WithDescription(
                "Days: stateLogDays, shortTermStatsDays and deviceEventDays 1–365, auditLogDays 30–3650 (400 validation.failed " +
                "otherwise). Hourly statistics and hourly energy are kept forever; nothing a rollup still needs is purged.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithValidation<UpdateRetentionSettingsRequest>();

        return app;
    }

    private static async Task<Ok<RetentionSettingsDto>> GetRetentionAsync(RetentionSettingsService settings, CancellationToken cancellationToken) =>
        TypedResults.Ok(await settings.GetAsync(cancellationToken));

    private static async Task<Results<Ok<RetentionSettingsDto>, ProblemHttpResult>> UpdateRetentionAsync(
        UpdateRetentionSettingsRequest request,
        RetentionSettingsService settings,
        CancellationToken cancellationToken) =>
        (await settings.UpdateAsync(request, cancellationToken)).ToHttpResult();
}
