using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Common;
using ControlHome.Application.HomeBackup;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace ControlHome.Api.Features.HomeBackup;

/// <summary>Backup export and import (Admins only).</summary>
public static class BackupEndpoints
{
    /// <summary>Largest import accepted (floor images are at most 10 MB each).</summary>
    public const long MaxImportBytes = 200L * 1024 * 1024;

    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var backup = app.MapGroup("/api/backup")
            .WithTags("Backup")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        backup.MapGet("/export", ExportAsync)
            .WithName("exportBackup")
            .WithSummary("Downloads a backup of the home configuration (zip) without the integrations' secrets.")
            .WithDescription(
                "Zip: manifest.json (schemaVersion), home.json, floors.json, files.json + files/<id>.<ext>, " +
                "integrations.json, devices.json, endpoints.json, placements.json, users.json (no password hashes or " +
                "PINs). History is not included. For a backup with secrets use POST /api/backup/export with a passphrase " +
                "(kept out of URLs).")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .Produces<Stream>(StatusCodes.Status200OK, BackupService.ContentType);

        backup.MapPost("/export", ExportWithSecretsAsync)
            .WithName("exportBackupWithSecrets")
            .WithSummary("Downloads a backup (zip); with a passphrase it includes the integrations' secrets, encrypted.")
            .WithDescription(
                "Secrets are encrypted with AES-256-GCM, the key derived from the passphrase with PBKDF2-SHA256 (600 000 " +
                "iterations); the same passphrase is needed to import them. 400 validation.failed (passphrase of 12–256 " +
                "characters).")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithValidation<ExportBackupRequest>()
            .Produces<Stream>(StatusCodes.Status200OK, BackupService.ContentType);

        backup.MapPost("/import", ImportAsync)
            .WithName("importBackup")
            .WithSummary("Restores a backup (multipart: file, optional passphrase) into an empty home.")
            .WithDescription(
                "Only when the home has no floors, integrations or devices (409 backup.not_empty). Floors, rooms, files, " +
                "integrations (started right away), devices, endpoints, placements and missing user accounts (without " +
                "passwords: reset them) are restored in one transaction. 400 backup.invalid, backup.unsupported_version, " +
                "backup.passphrase_required, backup.passphrase_incorrect; 409 runtime.standby; 413 request.too_large over " +
                "200 MB; 503 runtime.starting.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .DisableAntiforgery() // cross-site protection is the Origin + X-ControlHome check every mutation gets
            .WithMetadata(new RequestSizeLimitAttribute(MaxImportBytes))
            .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxImportBytes })
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        backup.MapGet("/scheduled", (IScheduledBackups scheduled) => TypedResults.Ok(scheduled.Describe()))
            .WithName("listScheduledBackups")
            .WithSummary("The automatic backups: the schedule, whether the last attempt failed, and the files kept.")
            .WithDescription(
                "The server writes a backup without credentials to its data folder on a schedule (daily, keeping seven by " +
                "default). LastFailedAtUtc is set while the last attempt failed and none succeeded since.")
            .RequireAuthorization(AuthorizationPolicies.Admin);

        backup.MapPost("/scheduled", RunScheduledAsync)
            .WithName("runScheduledBackup")
            .WithSummary("Writes an automatic backup now (whatever the schedule) and answers with it.")
            .WithDescription("Older backups beyond the number kept are deleted. 503 backup.write_failed when the data folder cannot be written.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        backup.MapGet("/scheduled/{name}", GetScheduledAsync)
            .WithName("getScheduledBackup")
            .WithSummary("Downloads one automatic backup (zip) by its name.")
            .WithDescription("Only the names listed by GET /api/backup/scheduled; any other name is 404 backup.not_found.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .Produces<Stream>(StatusCodes.Status200OK, BackupService.ContentType)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Results<Ok<ScheduledBackupDto>, ProblemHttpResult>> RunScheduledAsync(
        IScheduledBackups scheduled,
        CancellationToken cancellationToken) =>
        (await scheduled.RunNowAsync(cancellationToken)).ToHttpResult();

    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> GetScheduledAsync(
        string name,
        IScheduledBackups scheduled,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (await scheduled.ReadAsync(name, cancellationToken) is not { } content)
        {
            return Problems.From(AppError.NotFound(ErrorCodes.BackupNotFound, "No automatic backup has this name."));
        }

        response.Headers.CacheControl = "no-store";
        return TypedResults.File(content, BackupService.ContentType, name);
    }

    private static async Task<FileContentHttpResult> ExportAsync(BackupService backups, HttpResponse response, CancellationToken cancellationToken) =>
        File(await backups.ExportAsync(passphrase: null, audit: true, cancellationToken), response);

    private static async Task<FileContentHttpResult> ExportWithSecretsAsync(
        ExportBackupRequest request,
        BackupService backups,
        HttpResponse response,
        CancellationToken cancellationToken) =>
        File(await backups.ExportAsync(string.IsNullOrEmpty(request.Passphrase) ? null : request.Passphrase, audit: true, cancellationToken), response);

    private static async Task<Results<Ok<BackupImportResultDto>, ProblemHttpResult>> ImportAsync(
        IFormFile file,
        [FromForm] string? passphrase,
        BackupService backups,
        CancellationToken cancellationToken)
    {
        await using var content = file.OpenReadStream();
        return (await backups.ImportAsync(content, string.IsNullOrEmpty(passphrase) ? null : passphrase, cancellationToken)).ToHttpResult();
    }

    private static FileContentHttpResult File(BackupArchive archive, HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        return TypedResults.File(archive.Content, BackupService.ContentType, archive.FileName);
    }
}
