using ResvgSharp;
using SkiaSharp;

namespace Wlrix.Files.Core.Icons;

/// <summary>
/// Resolves an icon name and renders it, at a size, as PNG bytes.
/// </summary>
/// <remarks>
/// PNG bytes rather than a decoded bitmap, so the public surface stays free of both SkiaSharp
/// and Avalonia: the app wraps the result in an <c>Avalonia.Media.Imaging.Bitmap</c>, and the
/// same bytes are what a disk cache would store.
///
/// <para>
/// SVG goes through <b>resvg</b>, which is what <c>wlrix-ui</c> already uses on the Rust side,
/// so an icon renders identically whichever half of the workspace draws it. That matters here
/// because Adwaita is very nearly all SVG.
/// </para>
///
/// <para>
/// Renders are cached in memory by name and size. The cache is bounded and evicts wholesale
/// when full — a file manager touches a few dozen distinct icons and then stops, so a real LRU
/// would be machinery for a problem this does not have.
/// </para>
///
/// <para>
/// <b>Thread-safe.</b> One instance is shared by the whole application and its renders run on
/// the thread pool, several at once when a directory of mixed types is first shown. The lock
/// is around the cache rather than the render, so a slow rasterization does not block a
/// lookup that is about to hit.
/// </para>
/// </remarks>
public sealed class IconLoader(XdgIconTheme theme)
{
    /// <summary>How many rendered icons to keep before dropping the lot.</summary>
    /// <remarks>
    /// Generous next to what a listing needs, and small next to what a 64px RGBA icon costs
    /// (16 KiB decoded), so the ceiling is a few megabytes.
    /// </remarks>
    private const int CacheLimit = 512;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Name, int Size), byte[]?> _cache = [];

    /// <summary>The theme this draws from. Changing it clears the rendered cache too.</summary>
    public XdgIconTheme Theme { get; } = theme;

    /// <summary>Renders the first of several candidate names that resolves.</summary>
    public byte[]? Render(IReadOnlyList<string> names, int size)
    {
        foreach (var name in names)
        {
            if (Render(name, size) is { } bytes)
                return bytes;
        }
        return null;
    }

    /// <summary>Renders one icon name at a size, or null if it does not resolve.</summary>
    public byte[]? Render(string name, int size)
    {
        if (string.IsNullOrEmpty(name) || size <= 0)
            return null;

        var key = (name, size);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        // Rendered outside the lock: resvg takes about 10 ms, and holding the gate for that
        // would serialize every other row's cache hit behind it. Two threads racing the same
        // name simply both render it and the second overwrites an identical value.
        var rendered = Theme.Lookup(name, size) is { } path ? RenderFile(path, size) : null;

        lock (_gate)
        {
            // Misses are cached alongside hits: a name that is not in the theme will not be
            // in it on the next row either, and re-walking the search path per row is the
            // cost this avoids.
            if (_cache.Count >= CacheLimit)
                _cache.Clear();
            _cache[key] = rendered;
        }

        return rendered;
    }

    /// <summary>Forgets every render. Call after changing the theme.</summary>
    public void Clear()
    {
        lock (_gate)
            _cache.Clear();
        Theme.Clear();
    }

    /// <summary>Renders one file to PNG at the requested size.</summary>
    internal static byte[]? RenderFile(string path, int size)
    {
        try
        {
            return Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase)
                ? RenderSvg(path, size)
                : RenderRaster(path, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A theme with a broken or unreadable file should cost that one icon, not the
            // listing it appears in.
            return null;
        }
        catch (ResvgSharp.Exceptions.ResvgException)
        {
            return null;
        }
    }

    private static byte[]? RenderSvg(string path, int size)
    {
        var svg = File.ReadAllText(path);
        // resvg returns PNG bytes at exactly the size asked for, which is already the shape
        // this method promises -- no decode and re-encode in between.
        return Resvg.RenderToPng(svg, new ResvgOptions { Width = size, Height = size });
    }

    private static byte[]? RenderRaster(string path, int size)
    {
        using var input = File.OpenRead(path);
        using var bitmap = SKBitmap.Decode(input);
        if (bitmap is null)
            return null;

        // Already the right size: hand back the file's own bytes rather than round-tripping
        // through a decode and re-encode that could only lose quality.
        if (bitmap.Width == size && bitmap.Height == size
            && Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllBytes(path);

        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var scaled = bitmap.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (scaled is null)
            return null;

        using var image = SKImage.FromBitmap(scaled);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }
}
