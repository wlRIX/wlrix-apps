using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Icons;
using Wlrix.Files.Core.Mime;

namespace Wlrix.FilePicker.Services;

/// <summary>Turns a directory entry into the icon beside its name.</summary>
/// <remarks>
/// <para>
/// Caching is by <b>MIME type and size</b>, not by file: a directory of ten thousand
/// photographs needs one <c>image-png</c> render, not ten thousand. The first row of each type
/// pays for it and every later one is a dictionary hit.
/// </para>
/// <para>
/// Smaller than the file manager's service of the same name, and deliberately not shared with
/// it. There are no thumbnails here — a chooser is a list of names, and rendering the contents
/// of files somebody has not asked for yet is work a dialog open for four seconds never earns
/// back — and no theme-change invalidation, because this process does not outlive one question.
/// </para>
/// </remarks>
public sealed class IconService
{
    private readonly SharedMimeDatabase _mime;
    private readonly IconLoader _loader;

    // Concurrent because the render runs on the thread pool while the UI thread reads.
    private readonly ConcurrentDictionary<(string Mime, int Size), Bitmap?> _icons = [];
    private readonly ConcurrentDictionary<(string Mime, int Size), Task<Bitmap?>> _pending = [];

    public IconService(SharedMimeDatabase mime, IconLoader loader)
    {
        _mime = mime;
        _loader = loader;
    }

    /// <summary>The MIME type of an entry, by name and metadata only.</summary>
    /// <remarks>
    /// Never by content. Sniffing reads the head of a file, and a listing that did it once per
    /// row is exactly the cost the enumeration pipeline exists to avoid — see
    /// <c>ContentSniffer</c>, which is for one file at a time.
    /// </remarks>
    public string MimeTypeOf(FileEntry entry) => _mime.Resolve(entry);

    /// <summary>The icon for an entry if it is already rendered, else null.</summary>
    public Bitmap? TryGet(FileEntry entry, int size) =>
        _icons.TryGetValue((MimeTypeOf(entry), size), out var bitmap) ? bitmap : null;

    /// <summary>Asks for an entry's icon and delivers it on the UI thread when it is ready.</summary>
    /// <param name="stillWanted">
    /// Whether the row still wants it. Virtualization recycles containers, so a render can
    /// finish after its row has been scrolled past and reused for something else.
    /// </param>
    public void Request(FileEntry entry, int size, Action<Bitmap?> deliver, Func<bool> stillWanted)
    {
        var key = (MimeTypeOf(entry), size);
        if (_icons.TryGetValue(key, out var cached))
        {
            deliver(cached);
            return;
        }

        // One render per type even when fifty rows ask at once: the task is cached, not the
        // result, so the other forty-nine await the first.
        _ = _pending.GetOrAdd(key, k => RenderAsync(k.Mime, k.Size)).ContinueWith(
            task =>
            {
                if (task.IsCompletedSuccessfully && stillWanted())
                    Dispatcher.UIThread.Post(() => deliver(task.Result));
            },
            TaskScheduler.Default);
    }

    private async Task<Bitmap?> RenderAsync(string mimeType, int size)
    {
        try
        {
            var bytes = await Task.Run(() => _loader.Render(_mime.IconNamesFor(mimeType), size))
                .ConfigureAwait(false);

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
}
