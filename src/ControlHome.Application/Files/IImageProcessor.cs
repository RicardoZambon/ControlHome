namespace ControlHome.Application.Files;

/// <summary>
/// Decodes an uploaded image and re-encodes it as WebP (Infrastructure: SkiaSharp). Re-encoding drops all metadata
/// (EXIF, text chunks, color profiles beyond sRGB) and anything that is not pixels.
/// </summary>
public interface IImageProcessor
{
    /// <summary>
    /// Decodes <paramref name="source"/> (which must be in <paramref name="format"/>), applies its EXIF orientation,
    /// shrinks it so neither side exceeds <see cref="ImageEncodingOptions.MaxDimensionPx"/> and encodes it as WebP.
    /// Never throws for bad input: undecodable data is <see cref="ImageProcessingStatus.InvalidImage"/>.
    /// </summary>
    ImageProcessingResult ReencodeAsWebp(ReadOnlyMemory<byte> source, ImageFileFormat format, ImageEncodingOptions options);
}

/// <param name="MaxDimensionPx">Neither side of the result is larger (aspect ratio kept).</param>
/// <param name="Quality">WebP quality, 0–100.</param>
/// <param name="MaxSourcePixels">
/// Larger sources (width × height) are refused before decoding (<see cref="ImageProcessingStatus.TooManyPixels"/>), so a
/// small file cannot claim gigapixels and exhaust memory.
/// </param>
public sealed record ImageEncodingOptions(int MaxDimensionPx, int Quality, long MaxSourcePixels);

public enum ImageProcessingStatus
{
    Success,

    /// <summary>The data could not be decoded (corrupt, truncated, or not the expected format).</summary>
    InvalidImage,

    /// <summary>The image declares more pixels than <see cref="ImageEncodingOptions.MaxSourcePixels"/>.</summary>
    TooManyPixels,
}

/// <summary>The outcome of <see cref="IImageProcessor.ReencodeAsWebp"/>; on success, the WebP bytes and their size.</summary>
public sealed record ImageProcessingResult(ImageProcessingStatus Status, byte[] Data, int WidthPx, int HeightPx)
{
    public static ImageProcessingResult Invalid { get; } = new(ImageProcessingStatus.InvalidImage, [], 0, 0);

    public static ImageProcessingResult TooManyPixels { get; } = new(ImageProcessingStatus.TooManyPixels, [], 0, 0);

    public static ImageProcessingResult Success(byte[] data, int widthPx, int heightPx) =>
        new(ImageProcessingStatus.Success, data, widthPx, heightPx);
}
