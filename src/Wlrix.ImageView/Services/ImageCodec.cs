// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using SkiaSharp;
using Wlrix.Common.Imaging;
using Wlrix.ImageView.Models;

namespace Wlrix.ImageView.Services;

/// <summary>Turns files into <see cref="ImageDocument"/>s and rotated pixels back into files.</summary>
/// <remarks>
/// Static and free of UI: every method here is pure enough to test without a display, and
/// the view model runs them on the thread pool, since a large JPEG takes a noticeable time
/// to decode.
/// </remarks>
public static class ImageCodec
{
    /// <summary>JPEG and WebP quality for Save. High, since a viewer re-saving a photo should not visibly cost it anything.</summary>
    private const int LossyQuality = 92;

    /// <summary>The pixel layout every document holds.</summary>
    public static SKImageInfo DocumentInfo(int width, int height) =>
        new(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);

    public static ImageDocument Load(string path)
    {
        var bytes = File.ReadAllBytes(path);

        // SGI first, by its magic number rather than its extension: `.rgb` is also the name of
        // headerless raw RGB dumps, and those must not be taken for SGI files.
        if (SgiImage.IsSgi(bytes))
        {
            var sgi = SgiImage.Decode(bytes);
            return new ImageDocument(path, FromBgra(sgi.Bgra, sgi.Width, sgi.Height), ImageFormat.Sgi,
                sgi.Channels, sgi.BitsPerChannel, hasAlpha: sgi.Channels is 2 or 4,
                compressed: sgi.Compressed);
        }

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("Not an image this viewer can read.");

        var info = DocumentInfo(codec.Info.Width, codec.Info.Height);
        var decoded = new SKBitmap(info);
        var result = codec.GetPixels(info, decoded.GetPixels());
        // A truncated file still decodes the part that arrived, and showing that beats
        // refusing it: it is what every other viewer does, and it tells the user why.
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            decoded.Dispose();
            throw new InvalidDataException($"The image could not be decoded ({result}).");
        }

        // Photographs record which way up the camera was held rather than turning the pixels.
        // Applied here, so the image appears upright and a save writes it upright.
        var pixels = Orient(decoded, codec.EncodedOrigin);
        if (!ReferenceEquals(pixels, decoded))
            decoded.Dispose();

        var gray = codec.Info.ColorType is SKColorType.Gray8;
        var alpha = codec.Info.AlphaType is not SKAlphaType.Opaque;
        var bits = codec.Info.ColorType is SKColorType.RgbaF16 or SKColorType.Rgba16161616
            or SKColorType.Rg1616 or SKColorType.Alpha16 ? 16 : 8;

        return new ImageDocument(path, pixels, ImageFormats.FromSkia(codec.EncodedFormat),
            (gray ? 1 : 3) + (alpha ? 1 : 0), bits, alpha, Math.Max(1, codec.FrameCount));
    }

    /// <summary>Wraps top-down BGRA bytes as a document bitmap.</summary>
    public static SKBitmap FromBgra(byte[] bgra, int width, int height)
    {
        var bitmap = new SKBitmap(DocumentInfo(width, height));
        var rowBytes = width * 4;
        var target = bitmap.GetPixels();
        for (var y = 0; y < height; y++)
            Marshal.Copy(bgra, y * rowBytes, target + y * bitmap.RowBytes, rowBytes);
        return bitmap;
    }

    /// <summary>A copy of a bitmap's pixels as tightly packed, top-down BGRA bytes.</summary>
    public static byte[] ToBgra(SKBitmap bitmap)
    {
        var rowBytes = bitmap.Width * 4;
        var bytes = new byte[rowBytes * bitmap.Height];
        var source = bitmap.GetPixels();
        for (var y = 0; y < bitmap.Height; y++)
            Marshal.Copy(source + y * bitmap.RowBytes, bytes, y * rowBytes, rowBytes);
        return bytes;
    }

    /// <summary>
    /// Applies an EXIF origin, returning <paramref name="bitmap"/> itself when it is already
    /// upright.
    /// </summary>
    public static SKBitmap Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft)
            return bitmap;

        int w = bitmap.Width, h = bitmap.Height;
        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int outW = swaps ? h : w, outH = swaps ? w : h;

        var source = MemoryMarshal.Cast<byte, uint>(ToBgra(bitmap));
        var target = new uint[outW * outH];

        // For each output pixel, the source pixel it comes from. Worked out from where the
        // source's top-left corner has to end up for each origin.
        for (var y = 0; y < outH; y++)
        {
            for (var x = 0; x < outW; x++)
            {
                var (sx, sy) = origin switch
                {
                    SKEncodedOrigin.TopRight => (w - 1 - x, y),
                    SKEncodedOrigin.BottomRight => (w - 1 - x, h - 1 - y),
                    SKEncodedOrigin.BottomLeft => (x, h - 1 - y),
                    SKEncodedOrigin.LeftTop => (y, x),
                    SKEncodedOrigin.RightTop => (y, h - 1 - x),
                    SKEncodedOrigin.RightBottom => (w - 1 - y, h - 1 - x),
                    SKEncodedOrigin.LeftBottom => (w - 1 - y, x),
                    _ => (x, y),
                };
                target[y * outW + x] = source[sy * w + sx];
            }
        }

        return FromBgra(MemoryMarshal.AsBytes(target.AsSpan()).ToArray(), outW, outH);
    }

    /// <summary>The size of the box a w×h image needs once turned by <paramref name="degrees"/>.</summary>
    /// <remarks>
    /// Rounded up, except where the exact value is a whole number that floating point missed
    /// by a hair: cos 90° is 6e-17, not zero, and that must not add a column.
    /// </remarks>
    public static (int Width, int Height) RotatedSize(int width, int height, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        double c = Math.Abs(Math.Cos(radians)), s = Math.Abs(Math.Sin(radians));
        return (Bound(width * c + height * s), Bound(width * s + height * c));

        static int Bound(double value)
        {
            var nearest = Math.Round(value);
            return Math.Max(1, (int)(Math.Abs(value - nearest) < 1e-6 ? nearest : Math.Ceiling(value)));
        }
    }

    /// <summary>
    /// The pixels turned clockwise by <paramref name="degrees"/>, in a new bitmap the size of
    /// their bounding box.
    /// </summary>
    /// <param name="opaqueBackground">
    /// Fill the corners the turn exposes with black rather than leaving them transparent, for
    /// a format that has no alpha.
    /// </param>
    /// <remarks>
    /// A whole number of quarter turns moves pixels without touching them, so turning a photo
    /// upright and saving it costs nothing beyond the encoder's own loss. Any other angle has
    /// to resample, and uses a cubic filter for it.
    /// </remarks>
    public static SKBitmap Rotate(SKBitmap source, double degrees, bool opaqueBackground)
    {
        var transform = ViewTransform.Identity.WithRotation(degrees);
        if (transform.QuarterTurns is { } turns)
        {
            return turns switch
            {
                1 => Orient(source, SKEncodedOrigin.RightTop),
                2 => Orient(source, SKEncodedOrigin.BottomRight),
                3 => Orient(source, SKEncodedOrigin.LeftBottom),
                _ => source.Copy(),
            };
        }

        var (width, height) = RotatedSize(source.Width, source.Height, transform.Rotation);
        using var surface = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(surface))
        {
            canvas.Clear(opaqueBackground ? SKColors.Black : SKColors.Transparent);
            canvas.Translate(width / 2f, height / 2f);
            canvas.RotateDegrees((float)transform.Rotation);
            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        return ToDocumentLayout(surface);
    }

    /// <summary>The pixels mirrored, in a new bitmap. A copy when neither flag is set.</summary>
    public static SKBitmap Flip(SKBitmap source, bool horizontal, bool vertical) => (horizontal, vertical) switch
    {
        (true, true) => Orient(source, SKEncodedOrigin.BottomRight),
        (true, false) => Orient(source, SKEncodedOrigin.TopRight),
        (false, true) => Orient(source, SKEncodedOrigin.BottomLeft),
        _ => source.Copy(),
    };

    /// <summary>
    /// The pixels as Save writes them for <paramref name="state"/>: enhanced, mirrored, then
    /// turned, which is the order the view draws them in.
    /// </summary>
    /// <param name="opaqueBackground">See <see cref="Rotate"/>.</param>
    public static SKBitmap Render(SKBitmap source, ImageState state, bool opaqueBackground)
    {
        using var enhanced = ImageProcessor.Enhance(source, state.Enhance);
        using var flipped = Flip(enhanced, state.View.FlipHorizontal, state.View.FlipVertical);
        return Rotate(flipped, state.View.Rotation, opaqueBackground);
    }

    /// <summary>Converts any bitmap to the document layout (BGRA8888, unpremultiplied).</summary>
    internal static SKBitmap ToDocumentLayout(SKBitmap bitmap)
    {
        var result = new SKBitmap(DocumentInfo(bitmap.Width, bitmap.Height));
        using var pixmap = bitmap.PeekPixels();
        if (!pixmap.ReadPixels(result.Info, result.GetPixels(), result.RowBytes))
        {
            result.Dispose();
            throw new InvalidOperationException("Could not convert the pixels.");
        }
        return result;
    }

    /// <summary>The pixels flattened onto black, for a format without alpha.</summary>
    private static SKBitmap Flatten(SKBitmap bitmap)
    {
        using var surface = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(surface))
        {
            canvas.Clear(SKColors.Black);
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, 0, 0);
        }
        return ToDocumentLayout(surface);
    }

    /// <summary>Encodes document pixels in <paramref name="format"/>.</summary>
    /// <param name="path">The destination, for the SGI name field and whether <c>.bw</c> asks for gray.</param>
    public static byte[] Encode(SKBitmap pixels, ImageFormat format, string path)
    {
        switch (format)
        {
            case ImageFormat.Sgi:
            {
                var bgra = ToBgra(pixels);
                var channels = SgiImage.ChooseChannels(bgra);
                // `.bw` is the SGI convention for a gray image, so a color image saved under
                // that name is made gray rather than written as an RGB file misnamed.
                if (Path.GetExtension(path).Equals(".bw", StringComparison.OrdinalIgnoreCase))
                    channels = channels is 2 or 4 ? 2 : 1;
                return SgiImage.Encode(bgra, pixels.Width, pixels.Height, channels, compress: true,
                    name: Path.GetFileName(path));
            }
            case ImageFormat.Png:
            case ImageFormat.Jpeg:
            case ImageFormat.WebP:
            {
                var skia = format switch
                {
                    ImageFormat.Png => SKEncodedImageFormat.Png,
                    ImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
                    _ => SKEncodedImageFormat.Webp,
                };

                // JPEG has no alpha, and left to itself the encoder drops the alpha byte and
                // keeps whatever color sat under it, which is often garbage. Flattened onto
                // black, the same color the turned corners get.
                using var flattened = format == ImageFormat.Jpeg ? Flatten(pixels) : null;
                using var image = SKImage.FromBitmap(flattened ?? pixels);
                using var data = image.Encode(skia, format == ImageFormat.Png ? 100 : LossyQuality)
                    ?? throw new InvalidOperationException("The image could not be encoded.");
                return data.ToArray();
            }
            default:
                throw new NotSupportedException($"Cannot write {ImageFormats.DisplayName(format)} files.");
        }
    }

    /// <summary>
    /// Writes <paramref name="document"/> with the edits in <paramref name="state"/> to
    /// <paramref name="path"/> as <paramref name="format"/>. Pan and zoom are not edits and are
    /// ignored.
    /// </summary>
    /// <remarks>
    /// Written to a temporary file beside the destination and moved over it. A failure part
    /// way through, such as a full disk, leaves the original file as it was. Overwriting the
    /// image being viewed is the ordinary case for Save, so losing it would be a real risk.
    /// </remarks>
    public static void Save(ImageDocument document, ImageState state, string path, ImageFormat format)
    {
        if (!ImageFormats.CanWrite(format))
            throw new NotSupportedException($"Cannot write {ImageFormats.DisplayName(format)} files.");

        using var rendered = Render(document.Pixels, state, opaqueBackground: format == ImageFormat.Jpeg);
        var bytes = Encode(rendered, format, path);

        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temporary = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
