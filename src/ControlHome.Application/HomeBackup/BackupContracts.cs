using FluentValidation;

namespace ControlHome.Application.HomeBackup;

/// <summary>Exports the home with its integrations' secrets (<c>POST /api/backup/export</c>, Admin).</summary>
/// <param name="Passphrase">
/// 12–256 characters: the integrations' secrets are included, encrypted with it (AES-256-GCM, key from PBKDF2-SHA256 with
/// 600 000 iterations). Without one the backup has no secrets (integrations need their credentials again after import).
/// Kept out of URLs on purpose.
/// </param>
public sealed record ExportBackupRequest(string? Passphrase);

/// <summary>What <c>POST /api/backup/import</c> restored.</summary>
/// <param name="SecretsRestored">The integrations' secrets were in the backup and the passphrase opened them.</param>
/// <param name="UsersCreated">
/// Accounts created from the backup (it holds no passwords: an admin sets one for each with a password reset); accounts
/// whose user name already exists are left as they are.
/// </param>
public sealed record BackupImportResultDto(
    int Floors,
    int Rooms,
    int Files,
    int Integrations,
    int Devices,
    int Placements,
    int UsersCreated,
    bool SecretsRestored);

/// <summary><c>manifest.json</c> of a backup.</summary>
/// <param name="SchemaVersion">The backup format's version (imports accept this version and older).</param>
/// <param name="Encryption">How the secrets are encrypted; <c>null</c> when the backup has none.</param>
public sealed record BackupManifest(
    int SchemaVersion,
    string App,
    string AppVersion,
    DateTimeOffset ExportedAtUtc,
    bool IncludesSecrets,
    bool IncludesHistory,
    BackupEncryption? Encryption,
    BackupCounts Counts);

/// <summary>The secrets' encryption parameters (see <see cref="BackupCipher"/>).</summary>
/// <param name="Salt">Base64.</param>
/// <param name="Check">A known text encrypted with the key: recognizes a wrong passphrase.</param>
public sealed record BackupEncryption(string Kdf, int Iterations, string Salt, string Cipher, string Check);

/// <summary>What a backup holds.</summary>
public sealed record BackupCounts(int Floors, int Rooms, int Files, int Integrations, int Devices, int Endpoints, int Placements, int Users);

/// <summary>An exported backup: a zip archive.</summary>
public sealed record BackupArchive(string FileName, byte[] Content, BackupManifest Manifest);

public sealed class ExportBackupRequestValidator : AbstractValidator<ExportBackupRequest>
{
    public ExportBackupRequestValidator()
    {
        RuleFor(x => x.Passphrase)
            .MinimumLength(BackupCipher.MinPassphraseLength)
            .MaximumLength(BackupCipher.MaxPassphraseLength)
            .When(x => x.Passphrase is not null);
    }
}
