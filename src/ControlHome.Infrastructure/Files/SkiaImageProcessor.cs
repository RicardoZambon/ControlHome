using ControlHome.Application.Files;
using SkiaSharp;

namespace ControlHome.Infrastructure.Files;

/// <summary>
/// <see cref="IImageProcessor"/> with SkiaSharp (native assets: <c>SkiaSharp.NativeAssets.Linux.NoDependencies</c> in the
/// container, Win32/macOS packages elsewhere). Decodes into sRGB, applies the EXIF orientation, downscales with mipmapped
/// linear sampling and encodes lossy WebP: the output holds pixels only, no metadata from the upload.
/// </summary>
/// <remarks>
/// A missing or unloadable native library is deliberately not caught: it surfaces as a 500 and an error in the log, never
/// as <c>files.invalid_image</c>.
/// </remarks>
public sealed class SkiaImageProcessor : IImageProcessor
{
    private static readonly SKSamplingOptions _sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public ImageProcessingResult ReencodeAsWebp(ReadOnlyMemory<byte> source, ImageFileFormat format, ImageEncodingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var data = SKData.CreateCopy(source.Span);
        using var codec = SKCodec.Create(data);
        if (codec is null || !Matches(codec.EncodedFormat, format))
        {
            return ImageProcessingResult.Invalid;
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            return ImageProcessingResult.Invalid;
        }

        if ((long)info.Width * info.Height > options.MaxSourcePixels)
        {
            return ImageProcessingResult.TooManyPixels;
        }

        var alphaType = info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, alphaType, SKColorSpace.CreateSrgb());
        using var decoded = new SKBitmap();
        if (!decoded.TryAllocPixels(decodeInfo) || codec.GetPixels(decodeInfo, decoded.GetPixels()) != SKCodecResult.Success)
        {
            return ImageProcessingResult.Invalid;
        }

        decoded.SetImmutable(); // lets SKImage share the pixels instead of copying them
        using var image = SKImage.FromBitmap(decoded);

        var origin = codec.EncodedOrigin;
        var (orientedWidth, orientedHeight) = SwapsAxes(origin) ? (info.Height, info.Width) : (info.Width, info.Height);
        var scale = Math.Min(1d, (double)options.MaxDimensionPx / Math.Max(orientedWidth, orientedHeight));
        var width = Math.Clamp((int)Math.Round(orientedWidth * scale), 1, options.MaxDimensionPx);
        var height = Math.Clamp((int)Math.Round(orientedHeight * scale), 1, options.MaxDimensionPx);

        using var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, alphaType, SKColorSpace.CreateSrgb()));
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)width / orientedWidth, (float)height / orientedHeight);
            ApplyOrientation(canvas, origin, info.Width, info.Height);
            canvas.DrawImage(image, 0, 0, _sampling);
            canvas.Flush();
        }

        using var pixmap = target.PeekPixels();
        using var webp = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, options.Quality));
        return webp is null ? ImageProcessingResult.Invalid : ImageProcessingResult.Success(webp.ToArray(), width, height);
    }

    private static bool Matches(SKEncodedImageFormat encoded, ImageFileFormat expected) => (encoded, expected) switch
    {
        (SKEncodedImageFormat.Png, ImageFileFormat.Png) => true,
        (SKEncodedImageFormat.Jpeg, ImageFileFormat.Jpeg) => true,
        (SKEncodedImageFormat.Webp, ImageFileFormat.Webp) => true,
        _ => false,
    };

    /// <summary>EXIF orientations 5–8 turn the image by a quarter: width and height swap.</summary>
    private static bool SwapsAxes(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>
    /// Maps the stored pixels (width × height) to their upright position: canvas operations apply to the drawn image in
    /// reverse order, e.g. RightTop (rotate 90° clockwise) sends (x, y) to (height − y, x).
    /// </summary>
    private static void ApplyOrientation(SKCanvas canvas, SKEncodedOrigin origin, int width, int height)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: // mirrored horizontally
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight: // rotated 180°
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft: // mirrored vertically
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop: // transposed
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop: // rotated 90° clockwise
                canvas.Translate(height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom: // transversed
                canvas.Translate(height, width);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom: // rotated 90° counter-clockwise
                canvas.Translate(0, width);
                canvas.RotateDegrees(270);
                break;
            default: // TopLeft: already upright
                break;
        }
    }
}
