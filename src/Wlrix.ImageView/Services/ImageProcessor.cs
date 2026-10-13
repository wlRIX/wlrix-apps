// SPDX-License-Identifier: GPL-3.0-or-later

using SkiaSharp;
using Wlrix.ImageView.Models;

namespace Wlrix.ImageView.Services;

/// <summary>Applies an <see cref="Enhancement"/> to document pixels.</summary>
/// <remarks>
/// Through Skia's filters rather than a loop over pixels. The color operations are one color
/// matrix, and sharpening is a blur combined arithmetically with the original. Skia does both
/// with SIMD, which keeps a thumbwheel drag on a large photograph close to live. The preview
/// and Save both come through here, so what is saved is what was on screen.
/// </remarks>
public static class ImageProcessor
{
    /// <summary>The blur radius (sigma) the unsharp mask compares against, in pixels.</summary>
    private const float UnsharpSigma = 1;

    /// <summary>
    /// A new bitmap in the document layout with <paramref name="enhancement"/> applied. The
    /// caller owns it, even when the enhancement changes nothing.
    /// </summary>
    public static SKBitmap Enhance(SKBitmap source, Enhancement enhancement)
    {
        if (enhancement.IsIdentity)
            return source.Copy();

        using var surface = new SKBitmap(new SKImageInfo(source.Width, source.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(surface))
        using (var filter = CreateFilter(enhancement))
        using (var paint = new SKPaint { ImageFilter = filter })
        using (var image = SKImage.FromBitmap(source))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(image, 0, 0, paint);
        }

        return ImageCodec.ToDocumentLayout(surface);
    }

    /// <summary>Sharpness first, then the colors, as one image filter.</summary>
    private static SKImageFilter? CreateFilter(Enhancement enhancement)
    {
        SKImageFilter? filter = null;
        var sharpness = (float)enhancement.Sharpness;

        if (sharpness < 0)
        {
            // Clamped at the edges, so a blurred border is not darkened by blurring in the
            // transparent black that lies outside the image.
            filter = SKImageFilter.CreateBlur(-sharpness, -sharpness, SKShaderTileMode.Clamp, null);
        }
        else if (sharpness > 0)
        {
            // The unsharp mask, c + s·(c − blur(c)) = (1 + s)·c − s·blur(c). The arithmetic
            // filter computes k1·fg·bg + k2·fg + k3·bg + k4, with the blur as the foreground and
            // the source (a null input) as the background.
            using var blur = SKImageFilter.CreateBlur(UnsharpSigma, UnsharpSigma, SKShaderTileMode.Clamp, null);
            filter = SKImageFilter.CreateArithmetic(0, -sharpness, 1 + sharpness, 0, true, null, blur);
        }

        if (!enhancement.ChangesColor)
            return filter;

        using var colors = SKColorFilter.CreateColorMatrix(enhancement.ColorMatrix());
        var combined = SKImageFilter.CreateColorFilter(colors, filter);
        filter?.Dispose();
        return combined;
    }
}
