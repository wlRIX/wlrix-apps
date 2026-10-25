// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Wlrix.Icons;

namespace Wlrix.Settings.Utilities.Services;

/// <summary>Application and type icons, rendered off the UI thread and kept.</summary>
/// <remarks>
/// Rendered at twice the size they are shown, so they stay sharp on a scaled output; a
/// 16px row icon is 32px of pixels, which is what the theme's 32px directory has anyway.
/// One render per name list however many rows ask, because the task is what is cached.
/// </remarks>
internal sealed class IconCache(IconLoader loader)
{
    public const int Size = 16;

    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _icons = new(StringComparer.Ordinal);

    public static IconCache ForSession() => new(new IconLoader(new XdgIconTheme()));

    /// <summary>The first of several names that renders, or null.</summary>
    public Task<Bitmap?> Get(IReadOnlyList<string> names) =>
        names.Count == 0
            ? Task.FromResult<Bitmap?>(null)
            : _icons.GetOrAdd(string.Join('\n', names), _ => Task.Run(() => Render(names)));

    private Bitmap? Render(IReadOnlyList<string> names)
    {
        try
        {
            return loader.Render(names, Size * 2) is { } png ? new Bitmap(new MemoryStream(png)) : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
