namespace Wlrix.Files.Core.Watching;

/// <summary>
/// Notices changes by looking again, on an interval.
/// </summary>
/// <remarks>
/// What a remote share gets. SMB has a change-notification request and SFTP and FTP have
/// nothing at all, so polling is the only mechanism all three can share — and asking a server
/// for a directory listing every few seconds is a great deal cheaper than the alternative of
/// never noticing that somebody else added a file.
///
/// <para>
/// Paused while the window is not being looked at. A file manager left open on a share for a
/// week should not spend that week talking to a server nobody is watching, and the scan on
/// resume catches up in one round trip regardless.
/// </para>
/// </remarks>
public sealed class PollingWatcher(
    IFileSystem filesystem,
    Location directory,
    TimeSpan? interval = null) : DirectoryWatcher(filesystem, directory)
{
    /// <summary>How often a share is re-read when nothing else prompts it.</summary>
    /// <remarks>
    /// Ten seconds. Short enough that a file somebody else added turns up while you are still
    /// looking for it, long enough that a listing of a large share is not most of what the
    /// connection is doing.
    /// </remarks>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _interval = interval is { } given && given > TimeSpan.Zero
        ? given
        : DefaultInterval;

    private CancellationTokenSource? _stopping;
    private volatile bool _paused;

    /// <summary>Whether polling is suspended. Set when the window loses focus.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <inheritdoc/>
    public override async Task StartAsync(IReadOnlyList<FileEntry> known, CancellationToken cancellationToken)
    {
        await base.StartAsync(known, cancellationToken).ConfigureAwait(false);
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => PollAsync(_stopping.Token), CancellationToken.None);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_paused)
                    continue;
                await ScanAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Navigated away, or the window closed.
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_stopping is { } stopping)
        {
            _stopping = null;
            await stopping.CancelAsync().ConfigureAwait(false);
            stopping.Dispose();
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
