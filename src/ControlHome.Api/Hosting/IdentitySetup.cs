using ControlHome.Api.Features.Auth;
using ControlHome.Api.Http;
using ControlHome.Application.Auth;
using ControlHome.Application.Common;
using ControlHome.Domain.Security;
using ControlHome.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace ControlHome.Api.Hosting;

/// <summary>Authorization policy names (role based).</summary>
public static class AuthorizationPolicies
{
    /// <summary>Role Admin.</summary>
    public const string Admin = "Admin";

    /// <summary>Role Admin or Member.</summary>
    public const string Member = "Member";

    /// <summary>Any role.</summary>
    public const string Viewer = "Viewer";
}

/// <summary>
/// ASP.NET Core Identity with a cookie session (<c>ControlHome.Session</c>, scheme <see cref="IdentityConstants.ApplicationScheme"/>).
/// The user/role model and password/lockout options come from <see cref="IdentityCoreSetup"/> (shared with the
/// <c>reset-admin</c> command); this adds the sign-in manager, the cookie and the authorization policies.
/// </summary>
public static class IdentitySetup
{
    public const string SessionCookieName = "ControlHome.Session";
    public const string TwoFactorRememberMeCookieName = "ControlHome.TwoFactorRememberMe";

    /// <summary>How often a session's security stamp is re-checked (disabled users and role changes take effect).</summary>
    public static readonly TimeSpan SecurityStampValidationInterval = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddControlHomeIdentity(this IServiceCollection services)
    {
        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, options =>
            {
                options.Cookie.Name = SessionCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
                options.SlidingExpiration = true;

                // An API never redirects: unauthenticated → 401, forbidden → 403, both as ProblemDetails.
                options.Events.OnRedirectToLogin = context =>
                    Problems.From(AppError.Unauthorized(ErrorCodes.AuthRequired, "Sign in to continue.")).ExecuteAsync(context.HttpContext);
                options.Events.OnRedirectToAccessDenied = context =>
                    Problems.From(AppError.Forbidden(ErrorCodes.AuthForbidden, "You are not allowed to do this."))
                        .ExecuteAsync(context.HttpContext);
                options.Events.OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync;
            })
            // Never issued (there is no two-factor sign-in yet), but SecurityStampValidator signs this scheme out whenever it
            // rejects a session (disabled user, changed role or password); without it that request would fail with a 500.
            .AddCookie(IdentityConstants.TwoFactorRememberMeScheme, options =>
            {
                options.Cookie.Name = TwoFactorRememberMeCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });

        services.AddControlHomeIdentityCore().AddSignInManager();
        services.AddScoped<ISessionSignIn, SessionSignIn>();

        // Disabled users and role changes (both update the security stamp) take effect within a minute.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = SecurityStampValidationInterval);

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AuthorizationPolicies.Admin, policy => policy.RequireRole(Roles.Admin))
            .AddPolicy(AuthorizationPolicies.Member, policy => policy.RequireRole(Roles.Admin, Roles.Member))
            .AddPolicy(AuthorizationPolicies.Viewer, policy => policy.RequireRole(Roles.All));

        return services;
    }
}
