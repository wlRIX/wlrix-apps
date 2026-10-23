// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.IconCatalog.Services;

/// <summary>
/// Notices applications being installed and removed, so the catalog's pages follow without a
/// restart.
/// </summary>
/// <remarks>
/// The toolchest's Applications menu never did this: it cached what it found once. The events
/// here are a <i>trigger</i> to rescan, never a description of what changed, for the reasons
/// Wlrix.Files.Core's <c>InotifyWatcher</c> gives: inotify drops events under load, and a
/// package manager writing a few hundred files is load. Scanning every <c>applications</c>
/// directory takes a few milliseconds, so there is nothing to gain by being cleverer.
///
/// <para>
/// Each existing <c>applications</c> directory is watched with its subdirectories, which is
/// where KDE puts some of its own. One that does not exist yet -- <c>~/.local/share/applications</c>
/// on a fresh account, until the first user install makes it -- is caught by watching its
/// parent for that one name, and becomes a watch of its own once it appears.
/// </para>
/// </remarks>
public sealed class ApplicationWatcher : IDisposable
{
    /// <summary>
    /// How long events are gathered before the rescan. A package install writes its desktop
    /// file, then runs <c>update-desktop-database</c> beside it; one rescan at the end is enough.
    /// </summary>
    public static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(500);

    private readonly IReadOnlyList<string> _directories;
    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _pending;
    private string _armedFor = string.Empty;
    private bool _disposed;

    /// <param name="directories">The <c>applications</c> directories to watch.</param>
    public ApplicationWatcher(IEnumerable<string> directories)
    {
        _directories = directories.ToList();
    }

    /// <summary>
    /// Raised once a burst of changes has settled. Arrives on a thread-pool thread.
    /// </summary>
    public event Action? Changed;

    public void Start()
    {
        lock (_gate)
            Arm();
    }

    // Called with the gate held.
    private void Arm()
    {
        // Which directories exist decides every watch, so nothing is redone unless that changed.
        var existing = string.Join('\n', _directories.Select(directory => Directory.Exists(directory) ? directory : "-"));
        if (existing == _armedFor)
            return;
        _armedFor = existing;

        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();

        foreach (var directory in _directories)
        {
            if (Directory.Exists(directory))
                Watch(directory, filter: null, subdirectories: true);
            else if (Path.GetDirectoryName(directory) is { } parent && Directory.Exists(parent))
                Watch(parent, Path.GetFileName(directory), subdirectories: false);
        }
    }

    private void Watch(string path, string? filter, bool subdirectories)
    {
        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = subdirectories,
            };
            if (filter is not null)
                watcher.Filter = filter;

            watcher.Created += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Changed += OnEvent;
            watcher.Renamed += OnEvent;
            // The overflow notification: a burst that fills the kernel queue would otherwise
            // silently stop the catalog updating for the rest of the session.
            watcher.Error += (_, _) => Schedule();
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No watch descriptors left, or a directory that vanished since the check. The
            // catalog still works; it simply will not notice changes there.
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Schedule();

    /// <summary>Arms the coalescing timer, restarting it while events are still arriving.</summary>
    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _pending ??= new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            _pending.Change(Coalesce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            // A directory that has just been created, or removed, needs its watch moved.
            Arm();
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var watcher in _watchers)
                watcher.Dispose();
            _watchers.Clear();
            _pending?.Dispose();
            _pending = null;
        }
    }
}
