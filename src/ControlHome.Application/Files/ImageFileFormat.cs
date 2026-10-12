namespace ControlHome.Application.Files;

/// <summary>The image formats accepted for upload.</summary>
public enum ImageFileFormat
{
    Png,
    Jpeg,
    Webp,
}

/// <summary>Recognizes an accepted image format by its leading bytes ("magic numbers"), ignoring file name and MIME type.</summary>
public static class ImageSignatures
{
    private static ReadOnlySpan<byte> Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> Jpeg => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> Riff => "RIFF"u8;

    private static ReadOnlySpan<byte> Webp => "WEBP"u8;

    /// <summary>PNG, JPEG or WebP, or <c>null</c> for anything else (SVG, HTML, GIF, empty, …).</summary>
    public static ImageFileFormat? Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(Png))
        {
            return ImageFileFormat.Png;
        }

        if (data.StartsWith(Jpeg))
        {
            return ImageFileFormat.Jpeg;
        }

        // RIFF <4-byte little-endian size> WEBP
        if (data.Length >= 12 && data.StartsWith(Riff) && data[8..12].SequenceEqual(Webp))
        {
            return ImageFileFormat.Webp;
        }

        return null;
    }
}
