// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wlrix.IconCatalog.Models;
using Wlrix.IconCatalog.Rendering;
using Wlrix.Icons;

namespace Wlrix.IconCatalog.Services;

/// <summary>The four renders an icon's cell switches between: at rest, under the pointer, and selected with and without the keyboard.</summary>
public sealed record IconBitmaps(Bitmap? Rest, Bitmap? Hover, Bitmap? Selected, Bitmap? SelectedInactive);

/// <summary>Turns an icon into the bitmaps its cell shows, off the UI thread, and remembers them.</summary>
/// <remarks>
/// Cached by what the drawing depends on rather than by icon: Steam on the Games page and on
/// the Internet page is one render, and so is every launcher with no <c>Icon=</c> at all.
/// </remarks>
public sealed class IconImages(CarpetRenderer renderer)
{
    /// <summary>The size of an icon's square, in pixels: the desktop's.</summary>
    public const int Size = 64;

    private readonly ConcurrentDictionary<string, Task<IconBitmaps>> _cache = new(StringComparer.Ordinal);

    /// <summary>The colors an icon's body takes in each state, from the current scheme.</summary>
    public IconStateColors Colors { get; private set; } = IconStateColors.Default;

    /// <summary>Takes the scheme's icon colors. True when they changed, and every icon has to be drawn again.</summary>
    public bool SetColors(IconStateColors colors)
    {
        if (colors == Colors)
            return false;
        Colors = colors;
        _cache.Clear();
        return true;
    }

    /// <summary>Drops every render. Call when the icon theme's files may have changed, as after an install.</summary>
    public void Clear() => _cache.Clear();

    /// <summary>Delivers <paramref name="icon"/>'s bitmaps on the UI thread once they are drawn.</summary>
    public void Request(CatalogIcon icon, bool running, Action<IconBitmaps> deliver)
    {
        var colors = Colors;
        var task = _cache.GetOrAdd(KeyOf(icon, running), _ => Task.Run(() => Draw(icon, running, colors)));
        if (task.IsCompletedSuccessfully)
        {
            deliver(task.Result);
            return;
        }

        _ = task.ContinueWith(done =>
        {
            if (done.IsCompletedSuccessfully)
                Dispatcher.UIThread.Post(() => deliver(done.Result));
        }, TaskScheduler.Default);
    }

    private IconBitmaps Draw(CatalogIcon icon, bool running, IconStateColors colors) => new(
        Decode(renderer.Render(icon, running, IconState.Normal, colors, Size)),
        Decode(renderer.Render(icon, running, IconState.Hover, colors, Size)),
        Decode(renderer.Render(icon, running, IconState.Selected, colors, Size)),
        Decode(renderer.Render(icon, running, IconState.SelectedInactive, colors, Size)));

    private static Bitmap? Decode(byte[]? png)
    {
        if (png is null)
            return null;
        try
        {
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    /// <summary>Everything a render depends on, as one string.</summary>
    public static string KeyOf(CatalogIcon icon, bool running) => string.Join('\u001f',
        icon.Entry is null ? "file" : icon.IsControlPanel ? "panel" : "app",
        running && icon.Entry is not null ? "open" : "closed",
        icon.RunningIcon ?? string.Empty,
        string.Join('\u001e', icon.IconNames));
}
