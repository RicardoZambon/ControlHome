using ControlHome.Application.Abstractions;
using ControlHome.Application.Common;

namespace ControlHome.Application.Setup;

/// <summary>
/// Pre-database setup mode: while no database connection is configured, the setup wizard enters one. Gated by the
/// setup code exactly like the setup completion; the connection is tested before it is saved (see
/// <see cref="IDatabaseConnectionSetup"/>), and saving it starts the database initialization in the background.
/// </summary>
public sealed class DatabaseSetupService(
    IDatabaseStatus databaseStatus,
    ISetupCodeProvider setupCode,
    IDatabaseConnectionSetup connectionSetup)
{
    /// <summary>
    /// 409 <c>db.already_configured</c> once a connection exists (either source); 400 <c>setup.invalid_code</c> for a wrong
    /// code; otherwise the outcome of <see cref="IDatabaseConnectionSetup.ConfigureAsync"/>. On success: the status, with the
    /// database now configured (and initializing).
    /// </summary>
    public async Task<Result<SetupStatusDto>> ConfigureAsync(DatabaseSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (databaseStatus.IsConfigured)
        {
            return AlreadyConfigured();
        }

        if (!setupCode.Verify(request.SetupCode))
        {
            return AppError.Validation(ErrorCodes.SetupInvalidCode, "The setup code is not valid.");
        }

        var saved = await connectionSetup.ConfigureAsync(request, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        // The database was not configured a moment ago, so it cannot be ready (or set up) yet.
        return SetupService.CreateStatus(databaseStatus, setupCompleted: false);
    }

    /// <summary>409 <c>db.already_configured</c>.</summary>
    public static AppError AlreadyConfigured() =>
        AppError.Conflict(ErrorCodes.DbAlreadyConfigured, "A database connection is already configured.");
}
