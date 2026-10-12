using System.Globalization;
using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Users;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Users;

/// <summary>Household accounts (Admin only).</summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users")
            .WithTags("Users")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("", ListAsync)
            .WithName("listUsers")
            .WithSummary("All accounts.");

        group.MapPost("", CreateAsync)
            .WithName("createUser")
            .WithSummary("Creates an account.")
            .WithValidation<CreateUserRequest>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}", UpdateAsync)
            .WithName("updateUser")
            .WithSummary("Changes display name, role or disabled state (the last active Admin is protected).")
            .WithValidation<UpdateUserRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/password", ResetPasswordAsync)
            .WithName("resetUserPassword")
            .WithSummary("Sets a new password for the account.")
            .WithValidation<ResetPasswordRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("deleteUser")
            .WithSummary("Deletes the account (never yourself, never the last active Admin).")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<Ok<UserDto[]>> ListAsync(UserAdminService users, CancellationToken cancellationToken) =>
        TypedResults.Ok(await users.ListAsync(cancellationToken));

    private static async Task<Results<Created<UserDto>, ProblemHttpResult>> CreateAsync(
        CreateUserRequest request,
        UserAdminService users,
        CancellationToken cancellationToken)
    {
        var result = await users.CreateAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return Problems.From(result.Error);
        }

        return TypedResults.Created(
            string.Create(CultureInfo.InvariantCulture, $"/api/users/{result.Value.Id}"),
            result.Value);
    }

    private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateUserRequest request,
        UserAdminService users,
        CancellationToken cancellationToken) =>
        (await users.UpdateAsync(id, request, cancellationToken)).ToHttpResult();

    private static async Task<Results<NoContent, ProblemHttpResult>> ResetPasswordAsync(
        Guid id,
        ResetPasswordRequest request,
        UserAdminService users,
        CancellationToken cancellationToken) =>
        (await users.ResetPasswordAsync(id, request, cancellationToken)).ToHttpResult();

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        UserAdminService users,
        CancellationToken cancellationToken) =>
        (await users.DeleteAsync(id, cancellationToken)).ToHttpResult();
}
