using System.Text;
using ControlHome.Application.Files;
using ControlHome.Infrastructure.Files;
using ControlHome.TestSupport;
using SkiaSharp;

namespace ControlHome.Infrastructure.Tests;

public sealed class SkiaImageProcessorTests
{
    private static readonly ImageEncodingOptions _options = new(MaxDimensionPx: 4096, Quality: 85, MaxSourcePixels: 40_000_000);

    private readonly SkiaImageProcessor _processor = new();

    [Theory]
    [InlineData(ImageFileFormat.Png)]
    [InlineData(ImageFileFormat.Jpeg)]
    [InlineData(ImageFileFormat.Webp)]
    public void Reencodes_an_accepted_format_as_webp_keeping_its_size(ImageFileFormat format)
    {
        var source = format switch
        {
            ImageFileFormat.Png => TestImages.Png(320, 200),
            ImageFileFormat.Jpeg => TestImages.Jpeg(320, 200),
            _ => TestImages.Webp(320, 200),
        };

        var result = _processor.ReencodeAsWebp(source, format, _options);

        result.Status.ShouldBe(ImageProcessingStatus.Success);
        (result.WidthPx, result.HeightPx).ShouldBe((320, 200));
        ImageSignatures.Detect(result.Data).ShouldBe(ImageFileFormat.Webp);
        using var decoded = TestImages.Decode(result.Data);
        (decoded.Width, decoded.Height).ShouldBe((320, 200));
        ShouldBeRed(decoded.GetPixel(40, 100));
        ShouldBeBlue(decoded.GetPixel(280, 100));
    }

    [Fact]
    public void Shrinks_the_longest_side_to_the_maximum_keeping_the_aspect_ratio()
    {
        var result = _processor.ReencodeAsWebp(TestImages.Png(5000, 200), ImageFileFormat.Png, _options);

        result.Status.ShouldBe(ImageProcessingStatus.Success);
        (result.WidthPx, result.HeightPx).ShouldBe((4096, 164));
        using var decoded = TestImages.Decode(result.Data);
        (decoded.Width, decoded.Height).ShouldBe((4096, 164));
        ShouldBeRed(decoded.GetPixel(1000, 80));
        ShouldBeBlue(decoded.GetPixel(3000, 80));
    }

    [Fact]
    public void Applies_the_exif_orientation()
    {
        // Stored 200 × 100, red on the left; displayed rotated 90° clockwise: 100 × 200, red on top.
        var result = _processor.ReencodeAsWebp(TestImages.JpegRotatedClockwise(200, 100), ImageFileFormat.Jpeg, _options);

        result.Status.ShouldBe(ImageProcessingStatus.Success);
        (result.WidthPx, result.HeightPx).ShouldBe((100, 200));
        using var decoded = TestImages.Decode(result.Data);
        ShouldBeRed(decoded.GetPixel(50, 40));
        ShouldBeBlue(decoded.GetPixel(50, 160));
    }

    [Fact]
    public void Drops_metadata()
    {
        const string Marker = "controlhome-private-metadata-marker";
        var source = TestImages.PngWithText(64, 64, Marker);
        Encoding.Latin1.GetString(source).ShouldContain(Marker);

        var result = _processor.ReencodeAsWebp(source, ImageFileFormat.Png, _options);

        result.Status.ShouldBe(ImageProcessingStatus.Success);
        Encoding.Latin1.GetString(result.Data).ShouldNotContain(Marker);
    }

    [Fact]
    public void Refuses_more_pixels_than_allowed_before_decoding()
    {
        var result = _processor.ReencodeAsWebp(TestImages.PngDeclaring(20_000, 20_000), ImageFileFormat.Png, _options);

        result.Status.ShouldBe(ImageProcessingStatus.TooManyPixels);
    }

    [Fact]
    public void Corrupt_data_is_an_invalid_image()
    {
        _processor.ReencodeAsWebp(TestImages.CorruptPng(), ImageFileFormat.Png, _options).Status.ShouldBe(ImageProcessingStatus.InvalidImage);
        _processor.ReencodeAsWebp(TestImages.Svg(), ImageFileFormat.Png, _options).Status.ShouldBe(ImageProcessingStatus.InvalidImage);
        _processor.ReencodeAsWebp(ReadOnlyMemory<byte>.Empty, ImageFileFormat.Png, _options).Status.ShouldBe(ImageProcessingStatus.InvalidImage);
    }

    [Fact]
    public void Truncated_image_is_invalid()
    {
        var png = TestImages.Png(300, 300);

        var result = _processor.ReencodeAsWebp(png.AsMemory(0, png.Length / 2), ImageFileFormat.Png, _options);

        result.Status.ShouldBe(ImageProcessingStatus.InvalidImage);
    }

    [Fact]
    public void Data_in_another_format_than_expected_is_invalid()
    {
        var result = _processor.ReencodeAsWebp(TestImages.Jpeg(32, 32), ImageFileFormat.Png, _options);

        result.Status.ShouldBe(ImageProcessingStatus.InvalidImage);
    }

    private static void ShouldBeRed(SKColor color)
    {
        color.Red.ShouldBeGreaterThan((byte)200, color.ToString());
        color.Blue.ShouldBeLessThan((byte)60, color.ToString());
    }

    private static void ShouldBeBlue(SKColor color)
    {
        color.Blue.ShouldBeGreaterThan((byte)200, color.ToString());
        color.Red.ShouldBeLessThan((byte)60, color.ToString());
    }
}
