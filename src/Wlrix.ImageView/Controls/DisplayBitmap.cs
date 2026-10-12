// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Wlrix.ImageView.Controls;

/// <summary>Turns document pixels into a bitmap Avalonia can draw.</summary>
internal static class DisplayBitmap
{
    /// <summary>
    /// Copies <paramref name="pixels"/> into a new premultiplied bitmap at 96 DPI, so one
    /// image pixel is one view pixel at 100%.
    /// </summary>
    /// <remarks>
    /// Done once per document rather than per frame. The copy is premultiplied because that
    /// is what the compositor blends, and handing it unpremultiplied pixels would make it
    /// convert them on every redraw of a drag.
    /// </remarks>
    public static WriteableBitmap From(SKBitmap pixels)
    {
        var bitmap = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        using var source = pixels.PeekPixels();
        var info = new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (!source.ReadPixels(info, buffer.Address, buffer.RowBytes))
            throw new InvalidOperationException("Could not prepare the image for display.");
        return bitmap;
    }
}
