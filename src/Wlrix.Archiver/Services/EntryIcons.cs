using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wlrix.Archiver.Models;
using Wlrix.Files.Core.Mime;
using Wlrix.Icons;

namespace Wlrix.Archiver.Services;

/// <summary>Turns an archive entry into the icon the file manager would show for it.</summary>
/// <remarks>
/// The same icon theme, the same MIME database and the same lookup order as Files, so a
/// <c>.png</c> inside an archive wears the icon it will have once it is extracted. The theme
/// comes from <c>files.toml</c>, as the file chooser's does: it is the file manager's setting,
/// and an archive listing is a view of files.
///
/// <para>
/// An entry has no stat and no content to sniff, so its type comes from its name alone; a
/// directory is a directory whatever it is called. Caching is by type and size, so an archive
/// of ten thousand sources costs one <c>text-x-csrc</c> render. Rendering runs on the thread
/// pool and is published back on the UI thread.
/// </para>
/// </remarks>
public sealed class EntryIcons(SharedMimeDatabase mime, IconLoader loader)
{
    private readonly ConcurrentDictionary<(string Mime, int Size), Bitmap?> _icons = [];
    private readonly ConcurrentDictionary<(string Mime, int Size), Task<Bitmap?>> _pending = [];

    /// <summary>The MIME type an entry's name implies.</summary>
    public string MimeTypeOf(ArchiveEntry entry) =>
        entry.IsDirectory ? SharedMimeDatabase.Directory
        : entry.LinkTarget is not null ? SharedMimeDatabase.Symlink
        : mime.ResolveByName(entry.Name) ?? SharedMimeDatabase.Default;

    /// <summary>Renders the icon for an entry, reusing an in-flight render for the same type.</summary>
    public Task<Bitmap?> GetAsync(ArchiveEntry entry, int size)
    {
        var key = (MimeTypeOf(entry), size);
        if (_icons.TryGetValue(key, out var cached))
            return Task.FromResult(cached);
        return _pending.GetOrAdd(key, k => RenderAsync(k.Mime, k.Size));
    }

    /// <summary>
    /// Hands over an entry's icon: at once when it is already rendered, otherwise on the UI
    /// thread when it is ready.
    /// </summary>
    public void Request(ArchiveEntry entry, int size, Action<Bitmap?> deliver)
    {
        if (_icons.TryGetValue((MimeTypeOf(entry), size), out var cached))
        {
            deliver(cached);
            return;
        }

        _ = GetAsync(entry, size).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully)
                Dispatcher.UIThread.Post(() => deliver(task.Result));
        }, TaskScheduler.Default);
    }

    private async Task<Bitmap?> RenderAsync(string mimeType, int size)
    {
        var key = (mimeType, size);
        try
        {
            var bytes = await Task.Run(() => loader.Render(mime.IconNamesFor(mimeType), size))
                .ConfigureAwait(false);
            Bitmap? bitmap = null;
            if (bytes is not null)
            {
                using var stream = new MemoryStream(bytes);
                bitmap = new Bitmap(stream);
            }

            // Misses are cached too, so a type the theme has no icon for is looked up once.
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
}
