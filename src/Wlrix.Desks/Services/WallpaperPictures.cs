using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Wlrix.Common.Imaging;

namespace Wlrix.Desks.Services;

/// <summary>A decoded wallpaper, and the size it is at full scale.</summary>
/// <param name="Bitmap">The picture, possibly decoded smaller than the file.</param>
/// <param name="NativeSize">The file's own size in pixels, which a tiled or centered wallpaper
/// is laid out at whatever size <paramref name="Bitmap"/> was decoded to.</param>
public sealed record WallpaperPicture(Bitmap Bitmap, PixelSize NativeSize);

/// <summary>
/// The pictures the snapshots draw desks' backgrounds with, decoded once each and off the UI
/// thread.
/// </summary>
/// <remarks>
/// <see cref="Get"/> answers at once: the picture if it is ready, otherwise null -- the desk is
/// drawn in its color meanwhile -- and starts decoding it, raising <see cref="Decoded"/> when it
/// is. A file that cannot be decoded is remembered as such, so it is not retried on every
/// snapshot.
///
/// <para>
/// SGI images are recognized by their magic number and decoded by <see cref="SgiImage"/>, as in
/// Background Settings; everything else goes through Skia. XPM, XBM and PCX, which only
/// <c>wlrix-bg</c> reads, come back null and leave the desk its color.
/// </para>
/// </remarks>
public sealed class WallpaperPictures
{
    // A snapshot is at most 18% of the screen layout, so nothing wider than this is ever drawn
    // at more than one pixel per pixel. Tiles are smaller than this already and keep every pixel.
    private const int MaxWidth = 1024;

    private readonly ConcurrentDictionary<string, Task<WallpaperPicture?>> _pictures = new(StringComparer.Ordinal);

    /// <summary>Raised, on a thread-pool thread, when a picture has finished decoding.</summary>
    public event Action? Decoded;

    /// <summary>The picture at <paramref name="path"/> if it has been decoded; null otherwise.</summary>
    public WallpaperPicture? Get(string path)
    {
        var task = _pictures.GetOrAdd(path, Start);
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    /// <summary>Forgets every picture, for when the files may have changed under the same names.</summary>
    public void Clear() => _pictures.Clear();

    private Task<WallpaperPicture?> Start(string path) => Task.Run(() =>
    {
        var picture = Decode(path);
        if (picture is not null)
            Decoded?.Invoke();
        return picture;
    });

    internal static WallpaperPicture? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[2];
            if (stream.Read(magic) == 2 && SgiImage.IsSgi(magic))
            {
                stream.Position = 0;
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return FromSgi(SgiImage.Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
            }

            stream.Position = 0;
            var full = new Bitmap(stream);
            if (full.PixelSize.Width <= MaxWidth)
                return new WallpaperPicture(full, full.PixelSize);

            var native = full.PixelSize;
            using (full)
                return new WallpaperPicture(Shrink(full, native), native);
        }
        catch (Exception)
        {
            // Unreadable, or a format only wlrix-bg knows. The desk keeps its color.
            return null;
        }
    }

    private static WallpaperPicture FromSgi(SgiDecoded sgi)
    {
        var full = new WriteableBitmap(
            new PixelSize(sgi.Width, sgi.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var pixels = full.Lock())
        {
            for (var y = 0; y < sgi.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    sgi.Bgra, y * sgi.Width * 4, pixels.Address + y * pixels.RowBytes, sgi.Width * 4);
            }
        }

        var native = full.PixelSize;
        if (native.Width <= MaxWidth)
            return new WallpaperPicture(full, native);
        using (full)
            return new WallpaperPicture(Shrink(full, native), native);
    }

    private static Bitmap Shrink(Bitmap full, PixelSize native)
    {
        var height = Math.Max(1, (int)Math.Round((double)native.Height * MaxWidth / native.Width));
        return full.CreateScaledBitmap(new PixelSize(MaxWidth, height), BitmapInterpolationMode.MediumQuality);
    }
}
