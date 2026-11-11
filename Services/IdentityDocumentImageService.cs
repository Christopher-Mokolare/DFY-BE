using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;

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
        using var image = Image.Load<Rgba32>(source.ToArray());
        ValidateDimensions(image);
        image.Mutate(operation => operation.AutoOrient());
        var fontSize = Math.Clamp(image.Width / 42f, 14f, 34f);
        var font = SystemFonts.CreateFont("DejaVu Sans", fontSize, FontStyle.Bold);
        var watermarkColor = Color.FromRgba(150, 0, 0, 105);
        var lineHeight = Math.Max(fontSize * 2.5f, image.Height / 5f);
        image.Mutate(operation =>
        {
            for (var y = fontSize; y < image.Height; y += lineHeight)
                operation.DrawText(watermarkText, font, watermarkColor, new PointF(fontSize, y));
        });

        using var output = new MemoryStream();
        if (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase))
            image.SaveAsJpeg(output, new JpegEncoder { Quality = 90 });
        else if (contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase))
            image.SaveAsPng(output, new PngEncoder());
        else
            throw new InvalidDataException("Unsupported identity document image format.");
        return output.ToArray();
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
