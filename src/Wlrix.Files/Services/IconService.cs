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
    private readonly ConcurrentDictionary<(string Mime, int Size), Bitmap?> _icons = [];
    private readonly ConcurrentDictionary<(string Mime, int Size), Task<Bitmap?>> _pending = [];

    public IconService(SharedMimeDatabase mime, IconLoader loader)
    {
        _mime = mime;
        _loader = loader;
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
    public Bitmap? TryGet(FileEntry entry, int size) =>
        _icons.TryGetValue((MimeTypeOf(entry), size), out var bitmap) ? bitmap : null;

    /// <summary>Renders the icon for an entry, reusing an in-flight render for the same type.</summary>
    public Task<Bitmap?> GetAsync(FileEntry entry, int size)
    {
        var key = (MimeTypeOf(entry), size);
        if (_icons.TryGetValue(key, out var cached))
            return Task.FromResult(cached);

        // One render per type even when fifty rows ask at once: the task is cached, not the
        // result, so the other forty-nine await the first.
        return _pending.GetOrAdd(key, k => RenderAsync(k.Mime, k.Size));
    }

    private async Task<Bitmap?> RenderAsync(string mimeType, int size)
    {
        try
        {
            var bytes = await Task.Run(() => _loader.Render(_mime.IconNamesFor(mimeType), size)).ConfigureAwait(false);
            Bitmap? bitmap = null;
            if (bytes is not null)
            {
                using var stream = new MemoryStream(bytes);
                bitmap = new Bitmap(stream);
            }

            // Misses are cached too: a type the theme has no icon for will still have none on
            // the next row, and re-walking the search path per row is the cost this avoids.
            _icons[(mimeType, size)] = bitmap;
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            _icons[(mimeType, size)] = null;
            return null;
        }
        finally
        {
            _pending.TryRemove((mimeType, size), out _);
        }
    }

    /// <summary>Asks for an entry's icon and delivers it on the UI thread when it is ready.</summary>
    /// <remarks>
    /// The callback is skipped entirely when the row is no longer showing the same entry —
    /// virtualization recycles containers, so a slow render can finish after its row has been
    /// scrolled past and reused for something else.
    /// </remarks>
    public void Request(FileEntry entry, int size, Action<Bitmap?> deliver, Func<bool> stillWanted)
    {
        if (_icons.TryGetValue((MimeTypeOf(entry), size), out var cached))
        {
            deliver(cached);
            return;
        }

        _ = GetAsync(entry, size).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && stillWanted())
                Dispatcher.UIThread.Post(() => deliver(task.Result));
        }, TaskScheduler.Default);
    }

    /// <summary>Drops every rendered icon. Call when the theme or the icon size changes.</summary>
    public void Clear()
    {
        _icons.Clear();
        _loader.Clear();
    }
}
