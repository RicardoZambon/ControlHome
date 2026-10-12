using System.Globalization;
using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Files;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ControlHome.Api.Features.Files;

/// <summary>Uploaded files (floor backgrounds): Admins upload, anyone signed in downloads.</summary>
public static class FileEndpoints
{
    /// <summary>A stored file never changes (a new upload gets a new id), but it is only for signed-in users.</summary>
    public const string CacheControl = "private, max-age=31536000, immutable";

    /// <summary>
    /// The whole multipart request: the file's own limit (<see cref="FileService.MaxUploadBytes"/>, answered with 400
    /// <c>files.too_large</c>) plus room for larger files to reach that check; anything bigger is cut off by the server
    /// with 413 <c>request.too_large</c>.
    /// </summary>
    public const long MaxRequestBytes = 16 * 1024 * 1024;

    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files")
            .WithTags("Files")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("", UploadAsync)
            .WithName("uploadFile")
            .WithSummary("Uploads an image (multipart: file, purpose=floor-background); it is stored re-encoded as WebP.")
            .WithDescription(
                "PNG, JPEG or WebP (recognized by content, not name), at most 10 MB and 40 megapixels; the stored image is " +
                "WebP, at most 4096 px on its longest side, without metadata. 400: files.unsupported_type, files.too_large, " +
                "files.invalid_image, or validation.failed (unknown purpose); 413 request.too_large for requests over 16 MB.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            // Cross-site protection is the Origin + X-ControlHome check every mutation gets (no antiforgery tokens).
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes))
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("getFile")
            .WithSummary("The file's bytes (cacheable for a year: files never change).")
            .RequireAuthorization(AuthorizationPolicies.Viewer)
            .Produces<Stream>(StatusCodes.Status200OK, FileService.WebpContentType)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Results<Created<StoredFileDto>, ProblemHttpResult>> UploadAsync(
        IFormFile file,
        [FromForm] string purpose,
        FileService files,
        CancellationToken cancellationToken)
    {
        await using var content = file.OpenReadStream();
        var result = await files.UploadAsync(new FileUpload(purpose, file.Length, content), cancellationToken);
        if (result.IsFailure)
        {
            return Problems.From(result.Error);
        }

        return TypedResults.Created(
            string.Create(CultureInfo.InvariantCulture, $"/api/files/{result.Value.Id}"),
            result.Value);
    }

    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> GetAsync(
        Guid id,
        FileService files,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await files.GetContentAsync(id, cancellationToken);
        if (result.IsFailure)
        {
            return Problems.From(result.Error);
        }

        response.Headers.CacheControl = CacheControl;
        response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        return TypedResults.File(result.Value.Data, result.Value.ContentType);
    }
}
