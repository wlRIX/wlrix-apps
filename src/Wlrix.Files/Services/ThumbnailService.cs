using System.Collections.Concurrent;
using System.Threading.Channels;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Thumbnails;

namespace Wlrix.Files.Services;

/// <summary>
/// Thumbnails for the rows that are actually on screen.
/// </summary>
/// <remarks>
/// Unlike an icon, a thumbnail is per <i>file</i> — a directory of ten thousand photographs
/// needs ten thousand of them — so the machinery that makes icons cheap does not apply and
/// this needs machinery of its own.
///
/// <para>
/// Two mechanisms, and both are about giving up. Requests carry a generation number and stale
/// ones are dropped when they reach the front of the queue, so scrolling quickly through a
/// large directory renders what you stopped on rather than everything you passed. The queue is
/// bounded and drops its oldest entry when full, so a fast scroll cannot build a backlog that
/// outlives the scroll.
/// </para>
/// </remarks>
public sealed class ThumbnailService : IDisposable
{
    /// <summary>
    /// How many requests may be waiting.
    /// </summary>
    /// <remarks>
    /// A little more than a screenful. Anything beyond that is work for rows the user has
    /// already scrolled past, and holding it would only delay the rows they are looking at.
    /// </remarks>
    private const int QueueDepth = 64;

    private readonly ThumbnailProvider _provider;
    private readonly IconService _icons;
    private readonly Channel<Pending> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<(string Uri, ThumbnailSize Size), Bitmap?> _ready = [];
    private int _generation;

    public ThumbnailService(ThumbnailProvider provider, IconService icons)
    {
        _provider = provider;
        _icons = icons;
        _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(QueueDepth)
        {
            // Drop the oldest rather than blocking the caller: the caller is the UI thread
            // realizing a container, and the oldest request is the row furthest behind.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        _ = Task.Run(WorkAsync);
    }

    /// <summary>Whether thumbnails are wanted at all.</summary>
    /// <remarks>
    /// Read on every request rather than captured, so turning them off in the View menu stops
    /// the work already queued from being done as well as stopping new work being queued.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Abandons everything queued.
    /// </summary>
    /// <remarks>
    /// Called on navigation. The requests are not removed — reaching into a channel to do that
    /// would need a lock on the hot path — they are marked stale and dropped when the worker
    /// reaches them, which costs one integer comparison each.
    /// </remarks>
    public void Invalidate() => Interlocked.Increment(ref _generation);

    /// <summary>A thumbnail already rendered for this entry, or null.</summary>
    public Bitmap? TryGet(FileEntry entry, ThumbnailSize size) =>
        _ready.TryGetValue((entry.Location.ToUriString(), size), out var bitmap) ? bitmap : null;

    /// <summary>Asks for a thumbnail and delivers it on the UI thread if one can be made.</summary>
    /// <param name="stillWanted">
    /// Checked again when the render finishes. Virtualization recycles containers, so a
    /// thumbnail that took a second can arrive after its row has become a different file.
    /// </param>
    public void Request(FileEntry entry, ThumbnailSize size, Action<Bitmap?> deliver, Func<bool> stillWanted)
    {
        if (!Enabled)
            return;

        var key = (entry.Location.ToUriString(), size);
        if (_ready.TryGetValue(key, out var cached))
        {
            if (cached is not null)
                deliver(cached);
            return;
        }

        // Asked before queueing, because it is the cheap question and it is false for almost
        // everything: every directory, every text file, everything on a share.
        var mimeType = _icons.MimeTypeOf(entry);
        if (!_provider.CanThumbnail(entry, mimeType))
        {
            _ready[key] = null;
            return;
        }

        _queue.Writer.TryWrite(new Pending(entry, mimeType, size, Volatile.Read(ref _generation), deliver, stillWanted));
    }

    private async Task WorkAsync()
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
        {
            // The generation check is here, at the front of the queue, rather than at the
            // point of writing: a request queued while the user was looking at one directory
            // is worthless by the time they are three directories further on.
            if (!Enabled || request.Generation != Volatile.Read(ref _generation))
                continue;

            var key = (request.Entry.Location.ToUriString(), request.Size);
            if (_ready.ContainsKey(key))
                continue;

            Bitmap? bitmap = null;
            try
            {
                var png = await _provider
                    .GetAsync(request.Entry, request.MimeType, request.Size, _shutdown.Token)
                    .ConfigureAwait(false);
                if (png is not null)
                {
                    using var stream = new MemoryStream(png);
                    bitmap = new Bitmap(stream);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                // A file that will not decode is remembered as having no thumbnail, so the
                // next scroll past it costs a dictionary lookup rather than another attempt.
            }

            // Misses are cached too, for the same reason.
            _ready[key] = bitmap;
            if (bitmap is not null && request.StillWanted())
            {
                var deliver = request.Deliver;
                var made = bitmap;
                Dispatcher.UIThread.Post(() => deliver(made));
            }
        }
    }

    /// <summary>Forgets every rendered thumbnail. For when the view size changes.</summary>
    public void Clear()
    {
        _ready.Clear();
        Invalidate();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        _shutdown.Dispose();
    }

    private readonly record struct Pending(
        FileEntry Entry,
        string MimeType,
        ThumbnailSize Size,
        int Generation,
        Action<Bitmap?> Deliver,
        Func<bool> StillWanted);
}
