// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.Writer.Services;

/// <summary>
/// Tells the editor when a file it has open is touched by something else.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="FileSystemWatcher"/> per directory, not per file. Editors, version control and
/// this program's own saves all replace a file by renaming a new one over it, and a watch on
/// the old inode would go quiet at the first such save.
/// </para>
/// <para>
/// Events are coalesced per file over <see cref="Settle"/>: a single save is a create, several
/// writes and a rename, and the document should be asked about once. What arrives is only a
/// hint. The editor compares the file's stamp against what it last read or wrote, so its own
/// saves, and touches that change nothing, come to nothing.
/// </para>
/// <para>
/// <see cref="Changed"/> is raised on a thread-pool thread.
/// </para>
/// </remarks>
public sealed class FileWatcher : IDisposable
{
    /// <summary>How long a file has to stay quiet before it is reported.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, DirectoryWatch> _directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Timer> _pending = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>A watched file may have changed. The argument is its full path as watched.</summary>
    public event Action<string>? Changed;

    /// <summary>Starts watching <paramref name="path"/>. Every call needs its own <see cref="Unwatch"/>.</summary>
    public void Watch(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (directory is null)
            return;

        lock (_lock)
        {
            if (_disposed)
                return;

            if (!_directories.TryGetValue(directory, out var watch))
            {
                if (!Directory.Exists(directory))
                    return;
                try
                {
                    watch = new DirectoryWatch(directory, this);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // Out of inotify watches, or a directory that cannot be watched. The file is
                    // still editable; it just will not be noticed changing.
                    return;
                }

                _directories[directory] = watch;
            }

            watch.Files[full] = watch.Files.GetValueOrDefault(full) + 1;
        }
    }

    /// <summary>Stops one <see cref="Watch"/> of <paramref name="path"/>.</summary>
    public void Unwatch(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (directory is null)
            return;

        lock (_lock)
        {
            if (!_directories.TryGetValue(directory, out var watch) || !watch.Files.TryGetValue(full, out var count))
                return;

            if (count > 1)
            {
                watch.Files[full] = count - 1;
                return;
            }

            watch.Files.Remove(full);
            if (_pending.Remove(full, out var timer))
                timer.Dispose();
            if (watch.Files.Count == 0)
            {
                _directories.Remove(directory);
                watch.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var watch in _directories.Values)
                watch.Dispose();
            _directories.Clear();
            foreach (var timer in _pending.Values)
                timer.Dispose();
            _pending.Clear();
        }
    }

    private void OnEvent(DirectoryWatch watch, string? path)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            if (path is null)
            {
                // The kernel's queue overflowed and events were lost: any of the files may have
                // changed, so every one of them gets checked.
                foreach (var file in watch.Files.Keys)
                    Schedule(file);
                return;
            }

            if (watch.Files.ContainsKey(path))
                Schedule(path);
        }
    }

    private void Schedule(string path)
    {
        if (_pending.TryGetValue(path, out var timer))
        {
            timer.Change(Settle, Timeout.InfiniteTimeSpan);
            return;
        }

        _pending[path] = new Timer(_ =>
        {
            lock (_lock)
            {
                if (_pending.Remove(path, out var fired))
                    fired.Dispose();
                if (_disposed)
                    return;
            }

            Changed?.Invoke(path);
        }, null, Settle, Timeout.InfiniteTimeSpan);
    }

    private sealed class DirectoryWatch : IDisposable
    {
        private readonly FileSystemWatcher _watcher;

        public DirectoryWatch(string directory, FileWatcher owner)
        {
            _watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                               | NotifyFilters.Attributes | NotifyFilters.CreationTime,
            };

            _watcher.Changed += (_, e) => owner.OnEvent(this, e.FullPath);
            _watcher.Created += (_, e) => owner.OnEvent(this, e.FullPath);
            _watcher.Deleted += (_, e) => owner.OnEvent(this, e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                // Both ends: renamed away is a deletion, renamed into place is a replacement.
                owner.OnEvent(this, e.OldFullPath);
                owner.OnEvent(this, e.FullPath);
            };
            _watcher.Error += (_, _) => owner.OnEvent(this, null);
            _watcher.EnableRaisingEvents = true;
        }

        /// <summary>The watched files in this directory, with how many watches each has.</summary>
        public Dictionary<string, int> Files { get; } = new(StringComparer.Ordinal);

        public void Dispose() => _watcher.Dispose();
    }
}
