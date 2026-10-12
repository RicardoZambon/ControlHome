using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace ControlHome.TestSupport;

/// <summary>Synthetic images for upload tests, drawn with SkiaSharp or assembled byte by byte (no fixture files).</summary>
public static class TestImages
{
    /// <summary>The left half red, the right half blue (so orientation and scaling are visible in the pixels).</summary>
    public static byte[] Png(int width, int height) => Encode(width, height, SKEncodedImageFormat.Png);

    public static byte[] Jpeg(int width, int height) => Encode(width, height, SKEncodedImageFormat.Jpeg);

    public static byte[] Webp(int width, int height) => Encode(width, height, SKEncodedImageFormat.Webp);

    /// <summary>A PNG carrying a <c>tEXt</c> chunk (<c>Comment</c> = <paramref name="text"/>) right after its header.</summary>
    public static byte[] PngWithText(int width, int height, string text)
    {
        var png = Png(width, height);
        var chunk = Chunk("tEXt", [.. "Comment"u8, 0, .. Encoding.Latin1.GetBytes(text)]);
        const int AfterHeader = 8 + 25; // signature + IHDR (length, type, 13 data bytes, CRC)
        return [.. png.AsSpan(0, AfterHeader), .. chunk, .. png.AsSpan(AfterHeader)];
    }

    /// <summary>
    /// A JPEG whose EXIF says "rotate 90° clockwise to display" (orientation 6, Skia's <c>RightTop</c>): stored
    /// <paramref name="width"/> × <paramref name="height"/>, displayed height × width.
    /// </summary>
    public static byte[] JpegRotatedClockwise(int width, int height)
    {
        var jpeg = Jpeg(width, height);
        byte[] tiff =
        [
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, // little-endian TIFF header, IFD at offset 8
            0x01, 0x00, // one entry
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, // Orientation (SHORT) = 6
            0x00, 0x00, 0x00, 0x00, // no next IFD
        ];
        byte[] payload = [.. "Exif"u8, 0, 0, .. tiff];
        var length = (ushort)(payload.Length + 2);
        byte[] app1 = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];
        return [.. jpeg.AsSpan(0, 2), .. app1, .. jpeg.AsSpan(2)]; // right after SOI (FF D8)
    }

    /// <summary>
    /// A small, structurally valid PNG that declares <paramref name="width"/> × <paramref name="height"/> pixels but holds
    /// almost no image data (a "decompression bomb" header).
    /// </summary>
    public static byte[] PngDeclaring(int width, int height)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(new byte[64]);
        }

        return [.. PngSignature, .. Chunk("IHDR", header), .. Chunk("IDAT", compressed.ToArray()), .. Chunk("IEND", [])];
    }

    /// <summary>A valid PNG signature followed by junk: passes the magic-byte check, fails decoding.</summary>
    public static byte[] CorruptPng() => [.. PngSignature, .. Encoding.ASCII.GetBytes("this is not really a png at all")];

    public static byte[] Svg() =>
        Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><script>alert(1)</script></svg>""");

    public static byte[] Html() =>
        Encoding.UTF8.GetBytes("<!doctype html><html><body><script>alert(document.cookie)</script></body></html>");

    /// <summary>Decodes an image (e.g. the stored WebP) for assertions.</summary>
    public static SKBitmap Decode(byte[] data) =>
        SKBitmap.Decode(data) ?? throw new InvalidOperationException("The image could not be decoded.");

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            using var red = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, width / 2f, height, red);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        typeBytes.CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32([.. typeBytes, .. data]));
        return chunk;
    }

    /// <summary>CRC-32 (ISO-HDLC), as PNG chunks use it.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
