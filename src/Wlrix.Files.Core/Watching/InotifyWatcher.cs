namespace Wlrix.Files.Core.Watching;

/// <summary>
/// Notices changes on a local directory the moment they happen.
/// </summary>
/// <remarks>
/// The events are used as a <i>trigger</i> to re-read and compare, not as a description of what
/// changed. That is deliberate: inotify drops events under load, a rename arrives as two
/// unrelated halves that have to be stitched back together by cookie, and a queue overflow
/// means starting again anyway. Comparing listings gets all three right without special cases,
/// and it is the same comparison the remote poller does.
///
/// <para>
/// The rules here are the ones <c>Wlrix.Console</c>'s log tailer already paid for: watch the
/// directory rather than the files, swallow the transient <see cref="IOException"/> a
/// disappearing directory throws, and remember that events arrive on a thread pool thread.
/// <b>Also subscribe <c>Error</c></b> — that is the overflow notification, and it is the case
/// that actually bites when something unpacks an archive into the directory you are looking at.
/// </para>
/// </remarks>
public sealed class InotifyWatcher : DirectoryWatcher
{
    /// <summary>
    /// How long events are gathered before the directory is re-read.
    /// </summary>
    /// <remarks>
    /// Unpacking an archive produces thousands of events in a second. Without coalescing that
    /// is thousands of directory reads; with it, five a second at the very worst.
    /// </remarks>
    public static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(200);

    private readonly string _path;
    private readonly TimeSpan _coalesce;
    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _stopping;
    private Timer? _pending;

    public InotifyWatcher(IFileSystem filesystem, Location directory, TimeSpan? coalesce = null)
        : base(filesystem, directory)
    {
        if (!directory.TryGetLocalPath(out var path))
            throw new ArgumentException("inotify only watches local directories", nameof(directory));
        _path = path;
        _coalesce = coalesce is { } given && given > TimeSpan.Zero ? given : Coalesce;
    }

    /// <inheritdoc/>
    public override async Task StartAsync(IReadOnlyList<FileEntry> known, CancellationToken cancellationToken)
    {
        await base.StartAsync(known, cancellationToken).ConfigureAwait(false);
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var watcher = new FileSystemWatcher(_path)
            {
                // Everything a listing shows. Attributes are in because a chmod changes what
                // the details view draws, and size because a file being written to grows.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite | NotifyFilters.Size
                               | NotifyFilters.Attributes,
                IncludeSubdirectories = false
            };

            watcher.Created += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Changed += OnEvent;
            watcher.Renamed += OnEvent;
            // The overflow notification. Without it, a burst that fills the kernel queue
            // silently stops the directory updating for the rest of its life.
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No watch descriptors left, or a directory that vanished between the listing and
            // now. The listing still works; it simply will not notice changes.
            _watcher = null;
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Schedule();

    private void OnError(object sender, ErrorEventArgs e) => Schedule();

    /// <summary>Arms the coalescing timer, restarting it if events are still arriving.</summary>
    private void Schedule()
    {
        lock (_gate)
        {
            if (_stopping is not { } stopping || stopping.IsCancellationRequested)
                return;

            // Restarted rather than left running, so a burst re-reads once at the end of it
            // rather than once in the middle and again at the end.
            _pending ??= new Timer(_ => _ = ScanAsync(stopping.Token), null, Timeout.Infinite, Timeout.Infinite);
            _pending.Change(_coalesce, Timeout.InfiniteTimeSpan);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _pending?.Dispose();
            _pending = null;
        }

        if (_stopping is { } stopping)
        {
            _stopping = null;
            await stopping.CancelAsync().ConfigureAwait(false);
            stopping.Dispose();
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Picks the way a directory should be watched.</summary>
public static class DirectoryWatchers
{
    /// <summary>
    /// A watcher suited to where the directory is.
    /// </summary>
    /// <remarks>
    /// By capability rather than by scheme: a backend that grows change notification later
    /// declares <see cref="FileSystemCapabilities.Watch"/> and is picked up here without this
    /// method learning its name.
    /// </remarks>
    public static IDirectoryWatcher For(IFileSystem filesystem, Location directory) =>
        filesystem.Capabilities.HasFlag(FileSystemCapabilities.Watch) && directory.TryGetLocalPath(out _)
            ? new InotifyWatcher(filesystem, directory)
            : new PollingWatcher(filesystem, directory);
}
