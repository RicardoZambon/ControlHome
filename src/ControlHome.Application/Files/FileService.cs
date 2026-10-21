using System.Globalization;
using System.Security.Cryptography;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Domain.Files;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Files;

/// <summary>
/// Uploaded files, stored in the database. Images are accepted only as PNG, JPEG or WebP (recognized by their bytes,
/// never by name or MIME type), at most <see cref="MaxUploadBytes"/>, and are always decoded and re-encoded as WebP
/// (largest side ≤ <see cref="MaxDimensionPx"/>, quality <see cref="WebpQuality"/>) so nothing but pixels is ever stored
/// or served: SVG, HTML and polyglot files are rejected, metadata is dropped.
/// </summary>
public sealed class FileService(IControlHomeDb db, IImageProcessor images, IAuditLog auditLog, TimeProvider timeProvider)
{
    public const string FileTarget = "file";
    public const long MaxUploadBytes = 10 * 1024 * 1024;
    public const int MaxDimensionPx = 4096;
    public const int WebpQuality = 85;

    /// <summary>Refuse sources above 40 megapixels before decoding (about 160 MB of pixels).</summary>
    public const long MaxSourcePixels = 40_000_000;

    public const string WebpContentType = "image/webp";

    /// <summary>How every stored image is encoded (uploads, and the images of an imported backup).</summary>
    public static ImageEncodingOptions ImageEncoding { get; } = new(MaxDimensionPx, WebpQuality, MaxSourcePixels);

    /// <summary>
    /// Validates, re-encodes and stores the upload; audits <c>files.uploaded</c>. 400: <c>validation.failed</c>
    /// (unknown purpose), <c>files.too_large</c>, <c>files.unsupported_type</c> or <c>files.invalid_image</c>.
    /// </summary>
    public async Task<Result<StoredFileDto>> UploadAsync(FileUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);

        if (!FilePurposes.All.Contains(upload.Purpose, StringComparer.Ordinal))
        {
            return AppError.FieldErrors(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["purpose"] = [$"'purpose' must be one of: {string.Join(", ", FilePurposes.All)}."],
            });
        }

        var data = upload.Length > MaxUploadBytes ? null : await ReadAtMostAsync(upload.Content, MaxUploadBytes, cancellationToken);
        if (data is null)
        {
            return TooLarge();
        }

        if (ImageSignatures.Detect(data) is not { } format)
        {
            return AppError.Validation(ErrorCodes.FilesUnsupportedType, "Only PNG, JPEG and WebP images are accepted.");
        }

        var image = images.ReencodeAsWebp(data, format, ImageEncoding);
        switch (image.Status)
        {
            case ImageProcessingStatus.TooManyPixels:
                return TooLarge();
            case ImageProcessingStatus.InvalidImage:
                return AppError.Validation(ErrorCodes.FilesInvalidImage, "The image could not be read.");
        }

        var file = new StoredFile
        {
            Id = Guid.CreateVersion7(),
            Purpose = upload.Purpose,
            ContentType = WebpContentType,
            Length = image.Data.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(image.Data)),
            WidthPx = image.WidthPx,
            HeightPx = image.HeightPx,
            Data = image.Data,
            CreatedAtUtc = timeProvider.GetUtcNow(),
        };
        db.StoredFiles.Add(file);

        // Saves the file together with the audit entry.
        await auditLog.WriteAsync(
            new AuditEvent(
                AuditActions.FileUploaded,
                FileTarget,
                file.Id.ToString("D", CultureInfo.InvariantCulture),
                new { file.Purpose, file.Length, file.WidthPx, file.HeightPx }),
            cancellationToken);

        return new StoredFileDto(file.Id, file.ContentType, file.Length, file.WidthPx, file.HeightPx);
    }

    /// <summary>The stored bytes, or 404 <c>files.not_found</c>.</summary>
    public async Task<Result<FileContent>> GetContentAsync(Guid id, CancellationToken cancellationToken)
    {
        var content = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.Id == id)
            .Select(f => new FileContent(f.ContentType, f.Data))
            .SingleOrDefaultAsync(cancellationToken);
        return content is null ? AppError.NotFound(ErrorCodes.FilesNotFound, "No such file.") : content;
    }

    private static AppError TooLarge() =>
        AppError.Validation(
            ErrorCodes.FilesTooLarge,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Images may be at most {MaxUploadBytes / (1024 * 1024)} MB and {MaxSourcePixels / 1_000_000} megapixels."));

    /// <summary>The whole stream, or <c>null</c> as soon as it turns out to be longer than <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]?> ReadAtMostAsync(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
