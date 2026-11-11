using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;

namespace DoForYou.API.Services;

public interface IIdentityDocumentImageService
{
    (byte[] Bytes, string ContentType) NormalizeAndStripMetadata(ReadOnlySpan<byte> source, string contentType);
    byte[] ApplyWatermark(ReadOnlySpan<byte> source, string contentType, string watermarkText);
}

/// <summary>
/// Decodes uploaded images, applies EXIF orientation, removes EXIF/IPTC/XMP metadata,
/// and re-encodes the image. Administrator views are separately watermarked in memory.
/// </summary>
public sealed class IdentityDocumentImageService : IIdentityDocumentImageService
{
    private const long MaximumPixels = 40_000_000;

    public (byte[] Bytes, string ContentType) NormalizeAndStripMetadata(ReadOnlySpan<byte> source, string contentType)
    {
        using var image = Image.Load<Rgba32>(source.ToArray());
        ValidateDimensions(image);
        image.Mutate(operation => operation.AutoOrient());
        StripMetadata(image);
        using var output = new MemoryStream();
        if (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            image.SaveAsJpeg(output, new JpegEncoder { Quality = 90 });
            return (output.ToArray(), "image/jpeg");
        }

        if (contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase))
        {
            image.SaveAsPng(output, new PngEncoder());
            return (output.ToArray(), "image/png");
        }

        throw new InvalidDataException("Only JPEG and PNG identity document images are supported.");
    }

    public byte[] ApplyWatermark(ReadOnlySpan<byte> source, string contentType, string watermarkText)
    {
        using var bitmap = SKBitmap.Decode(source.ToArray());
        if (bitmap == null || bitmap.Width < 300 || bitmap.Height < 200 ||
            (long)bitmap.Width * bitmap.Height > MaximumPixels)
            throw new InvalidDataException("The identity document image could not be safely decoded for watermarking.");

        using var canvas = new SKCanvas(bitmap);
        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var paint = new SKPaint
        {
            Color = new SKColor(150, 0, 0, 105),
            TextSize = Math.Clamp(bitmap.Width / 42f, 14f, 34f),
            IsAntialias = true,
            Typeface = typeface
        };
        var lineHeight = Math.Max(paint.TextSize * 2.5f, bitmap.Height / 5f);
        for (var y = paint.TextSize * 1.5f; y < bitmap.Height; y += lineHeight)
            canvas.DrawText(watermarkText, paint.TextSize, y, paint);
        canvas.Flush();

        using var image = SKImage.FromBitmap(bitmap);
        var format = contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
            ? SKEncodedImageFormat.Jpeg
            : contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
                ? SKEncodedImageFormat.Png
                : throw new InvalidDataException("Unsupported identity document image format.");
        using var encoded = image.Encode(format, 90);
        if (encoded == null) throw new InvalidDataException("The identity document watermark could not be encoded.");
        return encoded.ToArray();
    }

    private static void StripMetadata(Image<Rgba32> image)
    {
        image.Metadata.ExifProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;
    }

    private static void ValidateDimensions(Image image)
    {
        if (image.Width < 300 || image.Height < 200 ||
            (long)image.Width * image.Height > MaximumPixels)
            throw new InvalidDataException("The identity document image dimensions are invalid or exceed the safe processing limit.");
    }
}
