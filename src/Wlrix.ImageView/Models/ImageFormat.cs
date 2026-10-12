// SPDX-License-Identifier: GPL-3.0-or-later

using SkiaSharp;

namespace Wlrix.ImageView.Models;

/// <summary>The file formats the viewer knows by name.</summary>
public enum ImageFormat
{
    Unknown,
    Png,
    Jpeg,
    WebP,
    Gif,
    Bmp,
    Ico,

    /// <summary>The SGI image format, IRIX's own: <c>.rgb</c>, <c>.rgba</c>, <c>.bw</c>, <c>.sgi</c>.</summary>
    Sgi,
}

/// <summary>What each format is called, which ones can be written, and how a name maps to one.</summary>
public static class ImageFormats
{
    /// <summary>The formats Save can write, in the order Save As offers them.</summary>
    public static IReadOnlyList<ImageFormat> Writable { get; } =
        [ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.WebP, ImageFormat.Sgi];

    /// <summary>Everything Open shows by default, as file-picker patterns.</summary>
    public static IReadOnlyList<string> ReadablePatterns { get; } =
    [
        "*.png", "*.jpg", "*.jpeg", "*.jpe", "*.webp", "*.gif", "*.bmp", "*.ico",
        "*.rgb", "*.rgba", "*.bw", "*.sgi", "*.int", "*.inta",
    ];

    public static bool CanWrite(ImageFormat format) => Writable.Contains(format);

    /// <summary>The format a file name's extension asks for, or <see cref="ImageFormat.Unknown"/>.</summary>
    public static ImageFormat FromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ImageFormat.Png,
            ".jpg" or ".jpeg" or ".jpe" => ImageFormat.Jpeg,
            ".webp" => ImageFormat.WebP,
            ".gif" => ImageFormat.Gif,
            ".bmp" => ImageFormat.Bmp,
            ".ico" => ImageFormat.Ico,
            ".rgb" or ".rgba" or ".bw" or ".sgi" or ".int" or ".inta" => ImageFormat.Sgi,
            _ => ImageFormat.Unknown,
        };

    internal static ImageFormat FromSkia(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => ImageFormat.Png,
        SKEncodedImageFormat.Jpeg => ImageFormat.Jpeg,
        SKEncodedImageFormat.Webp => ImageFormat.WebP,
        SKEncodedImageFormat.Gif => ImageFormat.Gif,
        SKEncodedImageFormat.Bmp => ImageFormat.Bmp,
        SKEncodedImageFormat.Ico => ImageFormat.Ico,
        _ => ImageFormat.Unknown,
    };

    /// <summary>The name Info shows. Proper nouns, so not localized.</summary>
    public static string DisplayName(ImageFormat format) => format switch
    {
        ImageFormat.Png => "PNG",
        ImageFormat.Jpeg => "JPEG",
        ImageFormat.WebP => "WebP",
        ImageFormat.Gif => "GIF",
        ImageFormat.Bmp => "BMP",
        ImageFormat.Ico => "ICO",
        ImageFormat.Sgi => "SGI",
        _ => "?",
    };

    /// <summary>The extension Save As appends for a format, without the dot.</summary>
    public static string DefaultExtension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "jpg",
        ImageFormat.WebP => "webp",
        ImageFormat.Sgi => "rgb",
        _ => "png",
    };

    /// <summary>The patterns Save As offers for a writable format.</summary>
    public static IReadOnlyList<string> Patterns(ImageFormat format) => format switch
    {
        ImageFormat.Png => ["*.png"],
        ImageFormat.Jpeg => ["*.jpg", "*.jpeg"],
        ImageFormat.WebP => ["*.webp"],
        ImageFormat.Sgi => ["*.rgb", "*.rgba", "*.bw", "*.sgi"],
        _ => ["*"],
    };
}
