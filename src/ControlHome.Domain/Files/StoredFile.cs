namespace ControlHome.Domain.Files;

/// <summary>
/// An uploaded file kept in the database (e.g. a floor's background image). Images are stored only after being decoded
/// and re-encoded by the server, never as uploaded. Content never changes: a new upload is a new file with a new id.
/// </summary>
public sealed class StoredFile
{
    public Guid Id { get; set; }

    /// <summary>What the file is for (one of <see cref="FilePurposes.All"/>).</summary>
    public string Purpose { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    /// <summary>Size of <see cref="Data"/> in bytes.</summary>
    public long Length { get; set; }

    /// <summary>Lower-case hex SHA-256 of <see cref="Data"/>.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public int WidthPx { get; set; }

    public int HeightPx { get; set; }

    public byte[] Data { get; set; } = [];

    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>The purposes a <see cref="StoredFile"/> can be uploaded for.</summary>
public static class FilePurposes
{
    /// <summary>A floor plan's background image (PNG, JPEG or WebP in; WebP stored).</summary>
    public const string FloorBackground = "floor-background";

    public static IReadOnlyList<string> All { get; } = [FloorBackground];
}
