using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Icons;
using Wlrix.Files.Core.Mime;

namespace Wlrix.Files.Services;

/// <summary>Turns a directory entry into the icon that represents it.</summary>
/// <remarks>
/// Caching is by <b>MIME type and size</b>, not by file, and that is what makes this cheap: a
/// directory of ten thousand photographs needs one <c>image-png</c> render, not ten thousand.
/// The first row of each type pays for the render and every later one is a dictionary hit.
///
/// <para>
/// The icon view asks for each type three more times, once per <see cref="IconState"/>: IRIX's
/// rule recolors an icon's body for rest, hover and selection, and doing that per pixel per
/// frame would be absurd when there are three answers per type. A null state is the icon as
/// the theme drew it, which the details view shows.
/// </para>
///
/// <para>
/// Rendering happens off the UI thread — resvg takes about 10 ms for an Adwaita icon, which is
/// most of a frame — and the decoded bitmap is published back through a callback. Rows that
/// find a warm cache get their icon synchronously and never see a placeholder.
/// </para>
/// </remarks>
public sealed class IconService
{
    private readonly SharedMimeDatabase _mime;
    private readonly IconLoader _loader;

    // Keyed by MIME type, so every .png in a directory shares one entry. Concurrent because
    // the render runs on the thread pool while the UI thread reads.
    private readonly ConcurrentDictionary<(string Mime, int Size, IconState? State), Bitmap?> _icons = [];
    private readonly ConcurrentDictionary<(string Mime, int Size, IconState? State), Task<Bitmap?>> _pending = [];

    public IconService(SharedMimeDatabase mime, IconLoader loader)
    {
        _mime = mime;
        _loader = loader;
    }

    /// <summary>The colors an icon's body takes in each state, from the current scheme.</summary>
    public IconStateColors Colors { get; private set; } = IconStateColors.Default;

    /// <summary>
    /// Takes the current scheme's icon colors. True when they changed, in which case every
    /// rendered icon has been dropped and the rows on screen have to ask again.
    /// </summary>
    public bool SetColors(IconStateColors colors)
    {
        if (colors == Colors)
            return false;
        Colors = colors;
        Clear();
        return true;
    }

    /// <summary>The MIME type of an entry.</summary>
    public string MimeTypeOf(FileEntry entry) => _mime.Resolve(entry);

    /// <summary>
    /// The icon for an entry if it is already rendered, else null.
    /// </summary>
    /// <remarks>
    /// Synchronous and allocation-free on the hot path, so a scroll through a directory of one
    /// repeated type never touches the thread pool.
    /// </remarks>
    public Bitmap? TryGet(FileEntry entry, int size, IconState? state = null) =>
        _icons.TryGetValue((MimeTypeOf(entry), size, state), out var bitmap) ? bitmap : null;

    /// <summary>Renders the icon for an entry, reusing an in-flight render for the same type.</summary>
    public Task<Bitmap?> GetAsync(FileEntry entry, int size, IconState? state = null)
    {
        var key = (MimeTypeOf(entry), size, state);
        if (_icons.TryGetValue(key, out var cached))
            return Task.FromResult(cached);

        // One render per type even when fifty rows ask at once: the task is cached, not the
        // result, so the other forty-nine await the first.
        return _pending.GetOrAdd(key, k => RenderAsync(k.Mime, k.Size, k.State));
    }

    private async Task<Bitmap?> RenderAsync(string mimeType, int size, IconState? state)
    {
        var key = (mimeType, size, state);
        try
        {
            var colors = Colors;
            var bytes = await Task.Run(() =>
            {
                // The loader caches the theme's render, so the three states of a type cost one
                // rasterization and three recolors.
                var plain = _loader.Render(_mime.IconNamesFor(mimeType), size);
                return plain is not null && state is { } s ? IconStatePainter.Apply(plain, s, colors) : plain;
            }).ConfigureAwait(false);
            Bitmap? bitmap = null;
            if (bytes is not null)
            {
                using var stream = new MemoryStream(bytes);
                bitmap = new Bitmap(stream);
            }

            // Misses are cached too: a type the theme has no icon for will still have none on
            // the next row, and re-walking the search path per row is the cost this avoids.
            _icons[key] = bitmap;
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            _icons[key] = null;
            return null;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>Asks for an entry's icon and delivers it on the UI thread when it is ready.</summary>
    /// <remarks>
    /// The callback is skipped entirely when the row is no longer showing the same entry —
    /// virtualization recycles containers, so a slow render can finish after its row has been
    /// scrolled past and reused for something else.
    /// </remarks>
    public void Request(FileEntry entry, int size, Action<Bitmap?> deliver, Func<bool> stillWanted) =>
        Request(entry, size, null, deliver, stillWanted);

    /// <summary>The same, for the icon in one <see cref="IconState"/>.</summary>
    public void Request(FileEntry entry, int size, IconState? state, Action<Bitmap?> deliver, Func<bool> stillWanted)
    {
        if (_icons.TryGetValue((MimeTypeOf(entry), size, state), out var cached))
        {
            deliver(cached);
            return;
        }

        _ = GetAsync(entry, size, state).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && stillWanted())
                Dispatcher.UIThread.Post(() => deliver(task.Result));
        }, TaskScheduler.Default);
    }

    /// <summary>Drops every rendered icon. Call when the theme, the scheme or the icon size changes.</summary>
    public void Clear()
    {
        _icons.Clear();
        _loader.Clear();
    }
}
