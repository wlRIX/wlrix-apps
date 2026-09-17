namespace Wlrix.Files.Core.Watching;

/// <summary>Tells a listing that the directory under it has moved on.</summary>
/// <remarks>
/// A watcher never touches the listing. It reports what changed and the listing reconciles,
/// which is what lets the same reports drive an incremental update, a test, and eventually the
/// file picker without any of them knowing how the changes were noticed.
/// </remarks>
public interface IDirectoryWatcher : IAsyncDisposable
{
    /// <summary>What is being watched.</summary>
    Location Directory { get; }

    /// <summary>
    /// Raised with a batch of changes. <b>From a background thread.</b>
    /// </summary>
    /// <remarks>
    /// Marshalling is the subscriber's job, the same contract the operation queue's progress
    /// uses: this assembly must not know that a dispatcher exists.
    /// </remarks>
    event Action<IReadOnlyList<DirectoryChange>>? Changed;

    /// <summary>Begins watching, seeded with what the listing already read.</summary>
    Task StartAsync(IReadOnlyList<FileEntry> known, CancellationToken cancellationToken);

    /// <summary>
    /// Looks now rather than waiting for the next event or interval.
    /// </summary>
    /// <remarks>
    /// What an operation calls when it finishes. A paste onto a share would otherwise sit
    /// invisible until the poll came round, which on a ten-second interval is long enough to
    /// look broken.
    /// </remarks>
    Task RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The half of watching that is the same however changes are noticed: read the directory,
/// compare it with what was there before, and report the difference.
/// </summary>
/// <remarks>
/// Subclasses supply only the trigger — a timer, or inotify. Everything about <i>what
/// changed</i> lives here and is shared, so the local and remote paths cannot drift into
/// disagreeing about what an addition is.
/// </remarks>
public abstract class DirectoryWatcher(IFileSystem filesystem, Location directory) : IDirectoryWatcher
{
    private readonly SemaphoreSlim _scanning = new(1, 1);
    private DirectorySnapshot _snapshot = DirectorySnapshot.Empty;
    private bool _disposed;

    /// <inheritdoc/>
    public Location Directory { get; } = directory;

    /// <inheritdoc/>
    public event Action<IReadOnlyList<DirectoryChange>>? Changed;

    /// <summary>How many entries the watcher believes are there. For tests.</summary>
    public int Known => _snapshot.Count;

    /// <inheritdoc/>
    public virtual Task StartAsync(IReadOnlyList<FileEntry> known, CancellationToken cancellationToken)
    {
        // Seeded from what the listing already read, so starting a watcher does not report
        // the entire directory as newly added.
        _snapshot = DirectorySnapshot.Of(known);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RefreshAsync(CancellationToken cancellationToken) => ScanAsync(cancellationToken);

    /// <summary>Re-reads the directory and reports what moved. Safe to call concurrently.</summary>
    protected async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            return;

        // One scan at a time. A burst of events and a timer tick can arrive together, and two
        // scans racing would each diff against the same stale snapshot and report the same
        // change twice.
        if (!await _scanning.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            var entries = new List<FileEntry>();
            await foreach (var entry in filesystem.EnumerateAsync(Directory, cancellationToken).ConfigureAwait(false))
                entries.Add(entry);

            var changes = _snapshot.DiffTo(Directory, entries, out var updated);
            _snapshot = updated;
            if (changes.Count > 0)
                Changed?.Invoke(changes);
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-scan. The next directory has its own watcher.
        }
        catch (FileOperationException)
        {
            // The directory went away, or the share dropped. Neither is worth reporting from
            // here: the listing already shows what it last read, and a navigation or a retry
            // is what resolves it.
        }
        finally
        {
            _scanning.Release();
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        _disposed = true;
        _scanning.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
