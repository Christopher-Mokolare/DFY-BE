using SkiaSharp;

namespace DoForYou.API.Services;

public interface IIdentityDocumentImageService
{
    (byte[] Bytes, string ContentType) NormalizeAndStripMetadata(ReadOnlySpan<byte> source, string contentType);
    byte[] ApplyWatermark(ReadOnlySpan<byte> source, string contentType, string watermarkText);
}

/// <summary>
/// Decodes and re-encodes uploaded images to remove embedded EXIF/IPTC/XMP metadata.
/// Applies a server-side, case- and administrator-attributed watermark to review copies.
/// </summary>
public sealed class IdentityDocumentImageService : IIdentityDocumentImageService
{
    private const long MaximumPixels = 40_000_000;

    public (byte[] Bytes, string ContentType) NormalizeAndStripMetadata(ReadOnlySpan<byte> source, string contentType)
    {
        using var codec = SKCodec.Create(source.ToArray());
        if (codec == null) throw new InvalidDataException("The identity document image could not be decoded.");
        if (codec.Origin != SKEncodedOrigin.TopLeft)
            throw new InvalidDataException("Rotate the identity document image upright and save it before uploading.");
        using var bitmap = SKBitmap.Decode(source.ToArray());
        ValidateDimensions(bitmap);
        using var image = SKImage.FromBitmap(bitmap);
        var format = GetFormat(contentType);
        using var encoded = image.Encode(format, 90);
        if (encoded == null) throw new InvalidDataException("The identity document image could not be safely normalized.");
        return (encoded.ToArray(), contentType);
    }

    public byte[] ApplyWatermark(ReadOnlySpan<byte> source, string contentType, string watermarkText)
    {
        using var codec = SKCodec.Create(source.ToArray());
        if (codec == null || codec.Origin != SKEncodedOrigin.TopLeft)
            throw new InvalidDataException("The identity document image could not be safely decoded for watermarking.");
        using var bitmap = SKBitmap.Decode(source.ToArray());
        ValidateDimensions(bitmap);
        using var canvas = new SKCanvas(bitmap);
        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
        using var paint = new SKPaint
        {
            Color = new SKColor(150, 0, 0, 105),
            TextSize = Math.Clamp(bitmap.Width / 42f, 14f, 34f),
            IsAntialias = true,
            Typeface = typeface ?? SKTypeface.Default
        };
        var lineHeight = Math.Max(paint.TextSize * 2.5f, bitmap.Height / 5f);
        for (var y = paint.TextSize * 1.5f; y < bitmap.Height; y += lineHeight)
            canvas.DrawText(watermarkText, paint.TextSize, y, paint);
        canvas.Flush();

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(GetFormat(contentType), 90);
        if (encoded == null) throw new InvalidDataException("The identity document watermark could not be encoded.");
        return encoded.ToArray();
    }

    private static SKEncodedImageFormat GetFormat(string contentType) =>
        contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
            ? SKEncodedImageFormat.Jpeg
            : contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
                ? SKEncodedImageFormat.Png
                : throw new InvalidDataException("Only JPEG and PNG identity document images are supported.");

    private static void ValidateDimensions(SKBitmap? bitmap)
    {
        if (bitmap == null || bitmap.Width < 300 || bitmap.Height < 200 ||
            (long)bitmap.Width * bitmap.Height > MaximumPixels)
            throw new InvalidDataException("The identity document image dimensions are invalid or exceed the safe processing limit.");
    }
}
