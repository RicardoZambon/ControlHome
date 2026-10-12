using ControlHome.Api.Http;
using ControlHome.Application.Common;
using ControlHome.Application.Identity;
using ControlHome.Domain.Security;
using ControlHome.Infrastructure.Persistence;
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
/// ASP.NET Core Identity with a cookie session (<c>ControlHome.Session</c>). Foundation wiring from Task 0.2-A/E;
/// Task 0.2-D refines password/lockout behaviour and adds the auth endpoints.
/// </summary>
public static class IdentitySetup
{
    public const string SessionCookieName = "ControlHome.Session";

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
            });

        services.AddIdentityCore<AppUser>(options =>
            {
                options.Password.RequiredLength = ValidationRules.PasswordMinLength;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;
                options.User.RequireUniqueEmail = false;
                options.User.AllowedUserNameCharacters =
                    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._@-";
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ControlHomeDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        // Disabled users and role changes take effect within a minute.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AuthorizationPolicies.Admin, policy => policy.RequireRole(Roles.Admin))
            .AddPolicy(AuthorizationPolicies.Member, policy => policy.RequireRole(Roles.Admin, Roles.Member))
            .AddPolicy(AuthorizationPolicies.Viewer, policy => policy.RequireRole(Roles.All));

        return services;
    }
}
