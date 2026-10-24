using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Wlrix.Common.Imaging;

namespace Wlrix.Settings.Background.Services;

/// <summary>A picture the panel offers, and whether it is one the user added.</summary>
/// <param name="Path">The absolute path, as it is written to <c>background.image</c>.</param>
/// <param name="IsOwn">In the user's own wallpaper folder, so Remove may delete it.</param>
public sealed record Wallpaper(string Path, bool IsOwn);

/// <summary>
/// The wallpapers: the ones wlRIX ships, the ones the user added, adding and removing, and the
/// small pictures the list shows.
/// </summary>
/// <remarks>
/// <para>
/// Everything lives under <c>wlrix/wallpapers</c> on the XDG data path, which is where
/// <c>wlrix-assets</c> installs the defaults. A picture the user adds is <em>copied</em> into
/// their own <c>$XDG_DATA_HOME/wlrix/wallpapers</c> rather than referred to where it was: the
/// desktop names its picture by absolute path, and a photo later moved out of Downloads would
/// otherwise leave a gray desktop with no clue why. It also means the list is a directory scan
/// with nothing of the panel's own to keep in step, the same arrangement as account pictures.
/// </para>
/// <para>
/// The copy keeps its format. <c>wlrix-bg</c> reads SGI and XPM directly, and converting an IRIX
/// background to PNG on the way in would throw away the one reason to have the original.
/// </para>
/// </remarks>
internal static class Wallpapers
{
    /// <summary>
    /// What <c>wlrix-bg</c> decodes, by extension: <c>image</c>'s formats and the
    /// <c>image-extras</c> ones (SGI, XPM, XBM, PCX). It sniffs content first, but an extension
    /// is what a folder listing and a file picker have to go on.
    /// </summary>
    public static readonly IReadOnlyList<string> Extensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp",
        ".rgb", ".rgba", ".bw", ".sgi", ".xpm", ".xbm", ".pcx",
    ];

    /// <summary><see cref="Extensions"/> as file picker patterns.</summary>
    public static IReadOnlyList<string> Patterns { get; } = [.. Extensions.Select(extension => "*" + extension)];

    /// <summary>Whether <paramref name="path"/> names a kind of file the desktop can show.</summary>
    public static bool IsSupported(string path) =>
        Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The user's own wallpaper folder, where added pictures are copied.</summary>
    public static string UserDirectory() => System.IO.Path.Combine(DataHome(), "wlrix", "wallpapers");

    /// <summary>
    /// Every wallpaper, the user's own folder first and then each system one, each file name
    /// once. The first wins, as on the rest of the XDG data path, so a user's copy named like a
    /// stock picture stands in for it.
    /// </summary>
    public static IReadOnlyList<Wallpaper> All()
    {
        var own = UserDirectory();
        var dirs = new List<string> { own };
        dirs.AddRange(DataDirs().Select(dir => System.IO.Path.Combine(dir, "wlrix", "wallpapers")));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<Wallpaper>();
        foreach (var dir in dirs.Distinct(StringComparer.Ordinal).Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir).Where(IsSupported).Order(StringComparer.Ordinal))
            {
                if (seen.Add(System.IO.Path.GetFileName(file)))
                    found.Add(new Wallpaper(file, IsOwn: dir == own));
            }
        }
        return found;
    }

    /// <summary>
    /// Copy <paramref name="source"/> into the user's folder and return where it went. A name
    /// already taken gets " (2)", " (3)" and so on, rather than replacing a picture that may be
    /// the one on the desktop right now.
    /// </summary>
    public static Wallpaper Add(string source)
    {
        var dir = UserDirectory();
        Directory.CreateDirectory(dir);

        var stem = System.IO.Path.GetFileNameWithoutExtension(source);
        var extension = System.IO.Path.GetExtension(source);
        var target = System.IO.Path.Combine(dir, stem + extension);
        for (var n = 2; File.Exists(target); n++)
            target = System.IO.Path.Combine(dir, $"{stem} ({n}){extension}");

        File.Copy(source, target);
        return new Wallpaper(target, IsOwn: true);
    }

    /// <summary>
    /// Delete one of the user's own pictures. Anything outside their folder is refused: a stock
    /// wallpaper is not theirs to delete, and a path from a hand-edited config could be anything.
    /// </summary>
    public static void Remove(Wallpaper wallpaper)
    {
        if (!wallpaper.IsOwn || !IsInUserDirectory(wallpaper.Path))
            throw new InvalidOperationException($"{wallpaper.Path} is not one of your own pictures.");
        File.Delete(wallpaper.Path);
    }

    /// <summary>Whether <paramref name="path"/> is directly in the user's wallpaper folder.</summary>
    public static bool IsInUserDirectory(string path) =>
        string.Equals(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)),
            System.IO.Path.GetFullPath(UserDirectory()),
            StringComparison.Ordinal);

    /// <summary>
    /// A picture <paramref name="width"/> pixels wide for the list, or null when there is none
    /// to show: an XPM, XBM or PCX file, which only <c>wlrix-bg</c> reads, or a file that will not
    /// decode. Slow; call it off the UI thread.
    /// </summary>
    public static Bitmap? Thumbnail(string path, int width)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[2];

            // SGI first, by its magic number rather than its extension, as Image View does:
            // `.rgb` is also the name of headerless raw dumps, and Skia knows neither.
            if (stream.Read(magic) == 2 && SgiImage.IsSgi(magic))
            {
                stream.Position = 0;
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return FromSgi(SgiImage.Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)), width);
            }

            stream.Position = 0;
            return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception)
        {
            // Unreadable or a format Skia does not know. The tile shows its name instead, and
            // the desktop -- which decodes with its own, wider set -- may well show it fine.
            return null;
        }
    }

    private static Bitmap FromSgi(SgiDecoded sgi, int width)
    {
        using var full = new WriteableBitmap(
            new PixelSize(sgi.Width, sgi.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var pixels = full.Lock())
        {
            for (var y = 0; y < sgi.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    sgi.Bgra, y * sgi.Width * 4, pixels.Address + y * pixels.RowBytes, sgi.Width * 4);
            }
        }
        var height = Math.Max(1, (int)Math.Round((double)sgi.Height * width / sgi.Width));
        return full.CreateScaledBitmap(new PixelSize(width, height), BitmapInterpolationMode.MediumQuality);
    }

    private static string DataHome() =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome
            ? dataHome
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    private static IEnumerable<string> DataDirs() =>
        (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dataDirs
            ? dataDirs
            : "/usr/local/share:/usr/share")
        .Split(':', StringSplitOptions.RemoveEmptyEntries);
}
