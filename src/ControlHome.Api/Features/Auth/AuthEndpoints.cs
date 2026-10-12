using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Auth;
using ControlHome.Application.Security;
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

        // The current password is checked like a sign-in: same per-IP budget, and wrong ones count towards the lockout.
        me.MapPost("/password", ChangePasswordAsync)
            .WithName("changePassword")
            .WithSummary("Changes the signed-in user's password.")
            .RequireRateLimiting(RateLimitPolicies.Login)
            .WithValidation<ChangePasswordRequest>() // 400: validation.failed or auth.password_incorrect
            .ProducesProblem(StatusCodes.Status423Locked)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        me.MapGet("/pin", GetPinStatusAsync)
            .WithName("getSafetyPin")
            .WithSummary("Whether the signed-in user has a safety PIN, and any lockout.");

        me.MapPut("/pin", SetPinAsync)
            .WithName("setSafetyPin")
            .WithSummary("Sets the safety PIN (4–8 digits) for safety-critical commands, or changes it (currentPin required).")
            .WithDescription(
                "400 validation.failed or safety.pin_incorrect (wrong currentPin; counts as a failed attempt); 423 safety.locked " +
                "(retryAfterSeconds) after 5 wrong PINs within 15 minutes.")
            .WithValidation<SetPinRequest>()
            .ProducesProblem(StatusCodes.Status423Locked);

        group.MapPost("/step-up", StepUpAsync)
            .WithName("stepUp")
            .WithSummary("Confirms the safety PIN: allows one safety-critical command (unlock, open the gate, disarm) within 60 s.")
            .WithDescription(
                "409 safety.pin_not_set; 400 safety.pin_incorrect; 423 safety.locked (retryAfterSeconds) after 5 wrong PINs " +
                "within 15 minutes, which also blocks safety-critical commands for 15 minutes.")
            .RequireAuthorization()
            .WithValidation<StepUpRequest>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status423Locked);

        return app;
    }

    private static async Task<Results<Ok<SafetyPinStatusDto>, ProblemHttpResult>> GetPinStatusAsync(StepUpService safety) =>
        (await safety.GetStatusAsync()).ToHttpResult();

    private static async Task<Results<NoContent, ProblemHttpResult>> SetPinAsync(
        SetPinRequest request,
        StepUpService safety,
        CancellationToken cancellationToken) =>
        (await safety.SetPinAsync(request, cancellationToken)).ToHttpResult();

    private static async Task<Results<NoContent, ProblemHttpResult>> StepUpAsync(
        StepUpRequest request,
        StepUpService safety,
        CancellationToken cancellationToken) =>
        (await safety.StepUpAsync(request, cancellationToken)).ToHttpResult();

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
