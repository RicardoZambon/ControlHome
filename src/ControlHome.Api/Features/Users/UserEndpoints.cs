using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Users;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Users;

/// <summary>Household accounts (Admin only). Implemented by Task 0.2-D.</summary>
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

    private static Task<Ok<UserDto[]>> ListAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException("0.2-D");

    private static Task<Results<Created<UserDto>, ProblemHttpResult>> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException("0.2-D");

    private static Task<Results<Ok<UserDto>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateUserRequest request,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException("0.2-D");

    private static Task<Results<NoContent, ProblemHttpResult>> ResetPasswordAsync(
        Guid id,
        ResetPasswordRequest request,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException("0.2-D");

    private static Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        throw new NotImplementedException("0.2-D");
}
