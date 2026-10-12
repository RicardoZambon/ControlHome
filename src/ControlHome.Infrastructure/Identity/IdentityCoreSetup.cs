using ControlHome.Application.Abstractions;
using ControlHome.Application.Auth;
using ControlHome.Application.Common;
using ControlHome.Application.Identity;
using ControlHome.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Infrastructure.Identity;

/// <summary>
/// ASP.NET Core Identity's user/role model on <see cref="ControlHomeDbContext"/>, shared by the web host (which adds the
/// sign-in manager and the cookie) and the <c>reset-admin</c> command.
/// </summary>
public static class IdentityCoreSetup
{
    public static IdentityBuilder AddControlHomeIdentityCore(this IServiceCollection services)
    {
        // Password-reset tokens (reset-admin links, admin password resets) expire after 15 minutes.
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = AdminPasswordResetService.TokenLifetime);
        services.TryAddScoped<IExclusiveTransaction, SqlExclusiveTransaction>();

        return services.AddIdentityCore<AppUser>(Configure)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ControlHomeDbContext>()
            .AddDefaultTokenProviders();
    }

    /// <summary>
    /// Passwords: at least 10 characters, no composition rules. Lockout: 5 failures, then 5 minutes, escalated by
    /// <see cref="LockoutPolicy"/> (the sign-in flow in <see cref="AuthService"/> applies it).
    /// </summary>
    public static void Configure(IdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Password.RequiredLength = ValidationRules.PasswordMinLength;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredUniqueChars = 1;
        options.User.RequireUniqueEmail = false;
        options.User.AllowedUserNameCharacters = ValidationRules.UserNameCharacters; // the request validators use the same set
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    }
}
