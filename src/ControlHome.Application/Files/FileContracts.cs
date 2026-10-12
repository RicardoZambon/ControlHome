namespace ControlHome.Application.Files;

/// <summary>A stored file. Its bytes are at <c>GET /api/files/{id}</c> (immutable: a new upload gets a new id).</summary>
/// <param name="ContentType">Always <c>image/webp</c> for images (they are re-encoded on upload).</param>
/// <param name="Length">Size of the stored bytes.</param>
public sealed record StoredFileDto(Guid Id, string ContentType, long Length, int WidthPx, int HeightPx);

/// <summary>An upload as the Api received it (not a wire contract: the HTTP form is <c>file</c> + <c>purpose</c>).</summary>
/// <param name="Purpose">One of the <c>FilePurposes</c> (e.g. <c>floor-background</c>).</param>
/// <param name="Length">Size of <paramref name="Content"/> in bytes, as received.</param>
public sealed record FileUpload(string Purpose, long Length, Stream Content);

/// <summary>A stored file's bytes and media type.</summary>
public sealed record FileContent(string ContentType, byte[] Data);
