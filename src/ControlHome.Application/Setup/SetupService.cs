using System.Globalization;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Auth;
using ControlHome.Application.Common;
using ControlHome.Application.Identity;
using ControlHome.Domain.Homes;
using ControlHome.Domain.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Setup;

/// <summary>
/// First-run setup lifecycle. Completion is one transaction under the application lock <see cref="LockName"/>, gated by
/// the one-way <see cref="Home.SetupCompletedAtUtc"/> flag: exactly one first Admin is ever created, however many
/// completions race; the others get 409 <c>setup.already_completed</c>.
/// </summary>
public sealed class SetupService(
    IControlHomeDb db,
    IDatabaseStatus databaseStatus,
    ISetupCodeProvider setupCode,
    IExclusiveTransaction exclusive,
    UserManager<AppUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    ISessionSignIn session,
    IAuditLog auditLog,
    TimeProvider timeProvider)
{
    public const string LockName = "ControlHome.Setup";

    /// <summary>Answers even while the database is not ready (then setup counts as not completed).</summary>
    public async Task<SetupStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var completed = databaseStatus.IsReady && await IsCompletedAsync(cancellationToken);
        return CreateStatus(databaseStatus, completed);
    }

    /// <summary>The status DTO for the current database state (<paramref name="setupCompleted"/> as determined by the caller).</summary>
    public static SetupStatusDto CreateStatus(IDatabaseStatus databaseStatus, bool setupCompleted)
    {
        ArgumentNullException.ThrowIfNull(databaseStatus);
        var state = databaseStatus.State;
        return new SetupStatusDto(
            setupCompleted,
            state,
            databaseStatus.IsConfigured,
            databaseStatus.Source,
            state == "Failed" ? databaseStatus.LastError : null,
            AppVersion.Informational);
    }

    /// <summary>
    /// Creates the first Admin and the home, marks setup completed and signs the admin in.
    /// 409 <c>setup.already_completed</c> once done; 400 <c>setup.invalid_code</c> for a wrong code.
    /// </summary>
    public async Task<Result<MeDto>> CompleteAsync(CompleteSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await IsCompletedAsync(cancellationToken))
        {
            return AlreadyCompleted();
        }

        if (!setupCode.Verify(request.SetupCode))
        {
            return AppError.Validation(ErrorCodes.SetupInvalidCode, "The setup code is not valid.");
        }

        // Chosen before the unit runs, so a commit the client saw fail can be recognized: if this admin row exists, this
        // completion won (re-running would answer its own success with 409 setup.already_completed).
        var adminId = Guid.CreateVersion7();
        var created = await exclusive.RunAsync(
            LockName,
            token => CreateAdminAndHomeAsync(adminId, request, token),
            token => userManager.ExistsAsync(adminId, token),
            cancellationToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        setupCode.OnSetupCompleted();
        await session.SignInAsync(created.Value, persistent: false);
        return await userManager.ToMeDtoAsync(created.Value);
    }

    private static AppError AlreadyCompleted() =>
        AppError.Conflict(ErrorCodes.SetupAlreadyCompleted, "Setup has already been completed.");

    private Task<bool> IsCompletedAsync(CancellationToken cancellationToken) =>
        db.Homes.AsNoTracking().AnyAsync(h => h.SetupCompletedAtUtc != null, cancellationToken);

    /// <summary>Runs inside the setup lock and transaction.</summary>
    private async Task<Result<AppUser>> CreateAdminAndHomeAsync(Guid adminId, CompleteSetupRequest request, CancellationToken cancellationToken)
    {
        // Re-checked under the lock: a concurrent completion may have finished while this one waited.
        if (await IsCompletedAsync(cancellationToken))
        {
            return AlreadyCompleted();
        }

        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                var roleResult = await roleManager.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.CreateVersion7() });
                if (!roleResult.Succeeded)
                {
                    return roleResult.ToAppError("admin.role");
                }
            }
        }

        var now = timeProvider.GetUtcNow();
        var admin = new AppUser
        {
            Id = adminId,
            UserName = request.Admin.UserName,
            DisplayName = request.Admin.DisplayName.Trim(),
            Locale = request.Home.Locale,
            Theme = Themes.System,
            CreatedAtUtc = now,
        };

        var result = await userManager.CreateAsync(admin, request.Admin.Password);
        if (!result.Succeeded)
        {
            return result.ToAppError("admin.password");
        }

        result = await userManager.AddToRoleAsync(admin, Roles.Admin);
        if (!result.Succeeded)
        {
            return result.ToAppError("admin.userName");
        }

        var home = await db.Homes.SingleOrDefaultAsync(cancellationToken);
        if (home is null)
        {
            home = new Home { Id = Home.SingletonId, CreatedAtUtc = now };
            db.Homes.Add(home);
        }

        home.Name = request.Home.Name.Trim();
        home.TimeZoneId = request.Home.TimeZoneId;
        home.Latitude = request.Home.Latitude;
        home.Longitude = request.Home.Longitude;
        home.Locale = request.Home.Locale;
        home.Currency = request.Home.Currency;
        home.Tariff = new Tariff { Kind = Tariff.FlatKind, PricePerKwh = request.Home.PricePerKwh };
        home.SetupCompletedAtUtc = now;

        // Saves the home together with the audit entry.
        await auditLog.WriteAsync(
            new AuditEvent(
                AuditActions.SetupCompleted,
                AuthService.UserTarget,
                admin.Id.ToString("D", CultureInfo.InvariantCulture),
                new { AdminUserName = admin.UserName, HomeName = home.Name }),
            cancellationToken);

        return admin;
    }
}
