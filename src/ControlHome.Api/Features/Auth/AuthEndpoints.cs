using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Auth;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Auth;

/// <summary>Login, logout, the signed-in user's profile and password, and the reset-admin token redemption.</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync)
            .WithName("login")
            .WithSummary("Signs in with user name and password (sets the session cookie).")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Login)
            .WithValidation<LoginRequest>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status423Locked)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/logout", LogoutAsync)
            .WithName("logout")
            .WithSummary("Ends the session (always 204).")
            .AllowAnonymous();

        group.MapPost("/reset-password", ResetPasswordWithTokenAsync)
            .WithName("resetPasswordWithToken")
            .WithSummary("Sets a new password with a one-time token from 'reset-admin <user>'.")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Login)
            .WithValidation<ResetPasswordWithTokenRequest>() // 400: validation.failed or auth.reset_invalid
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        var me = group.MapGroup("/me").RequireAuthorization().ProducesProblem(StatusCodes.Status401Unauthorized);

        me.MapGet("", GetMeAsync)
            .WithName("getMe")
            .WithSummary("The signed-in user.");

        me.MapPut("/profile", UpdateProfileAsync)
            .WithName("updateProfile")
            .WithSummary("Changes the signed-in user's display name, language and theme.")
            .WithValidation<UpdateProfileRequest>();

        me.MapPost("/password", ChangePasswordAsync)
            .WithName("changePassword")
            .WithSummary("Changes the signed-in user's password.")
            .WithValidation<ChangePasswordRequest>(); // 400: validation.failed or auth.password_incorrect

        return app;
    }

    private static async Task<Results<Ok<MeDto>, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        AuthService auth,
        CancellationToken cancellationToken) =>
        (await auth.LoginAsync(request, cancellationToken)).ToHttpResult();

    private static async Task<NoContent> LogoutAsync(AuthService auth, CancellationToken cancellationToken)
    {
        await auth.LogoutAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ResetPasswordWithTokenAsync(
        ResetPasswordWithTokenRequest request,
        AuthService auth,
        CancellationToken cancellationToken) =>
        (await auth.ResetPasswordWithTokenAsync(request, cancellationToken)).ToHttpResult();

    private static async Task<Results<Ok<MeDto>, ProblemHttpResult>> GetMeAsync(AuthService auth) =>
        (await auth.GetMeAsync()).ToHttpResult();

    private static async Task<Results<Ok<MeDto>, ProblemHttpResult>> UpdateProfileAsync(
        UpdateProfileRequest request,
        AuthService auth) =>
        (await auth.UpdateProfileAsync(request)).ToHttpResult();

    private static async Task<Results<NoContent, ProblemHttpResult>> ChangePasswordAsync(
        ChangePasswordRequest request,
        AuthService auth,
        CancellationToken cancellationToken) =>
        (await auth.ChangePasswordAsync(request, cancellationToken)).ToHttpResult();
}
