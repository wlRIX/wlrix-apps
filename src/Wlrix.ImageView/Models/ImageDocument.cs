// SPDX-License-Identifier: GPL-3.0-or-later

using SkiaSharp;

namespace Wlrix.ImageView.Models;

/// <summary>An open image: its pixels and what the file said about them.</summary>
/// <remarks>
/// The pixels are always BGRA8888 and unpremultiplied, whatever the file held, so the
/// histogram, the save path and the display conversion each deal with one layout. What the
/// file actually stored is kept separately, for Info.
/// </remarks>
public sealed class ImageDocument : IDisposable
{
    public ImageDocument(string path, SKBitmap pixels, ImageFormat format, int channels,
        int bitsPerChannel, bool hasAlpha, int frameCount = 1, bool? compressed = null)
    {
        Path = path;
        Pixels = pixels;
        Format = format;
        Channels = channels;
        BitsPerChannel = bitsPerChannel;
        HasAlpha = hasAlpha;
        FrameCount = frameCount;
        Compressed = compressed;

        var file = new FileInfo(path);
        if (file.Exists)
        {
            FileSize = file.Length;
            Modified = file.LastWriteTime;
        }
    }

    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    /// <summary>BGRA8888, unpremultiplied.</summary>
    public SKBitmap Pixels { get; }

    public int Width => Pixels.Width;
    public int Height => Pixels.Height;

    public ImageFormat Format { get; }

    /// <summary>The channels the file stores: 1 gray, 2 gray and alpha, 3 RGB, 4 RGBA.</summary>
    public int Channels { get; }

    public int BitsPerChannel { get; }
    public bool HasAlpha { get; }

    /// <summary>Frames in the file. An animated GIF shows only its first.</summary>
    public int FrameCount { get; }

    /// <summary>For SGI files, whether the file is run-length encoded. Unknown elsewhere.</summary>
    public bool? Compressed { get; }

    public long FileSize { get; }
    public DateTime Modified { get; }

    public void Dispose() => Pixels.Dispose();
}
