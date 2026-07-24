namespace Wlrix.Console.Services;

/// <summary>
/// Tails a log file: reads its existing contents and then watches for appended data, raising
/// <see cref="Appended"/> with each new chunk in file order. The file need not exist when
/// <see cref="Start"/> is called — the directory is watched, so a log that appears later (or is
/// truncated and rewritten each time its process restarts) is picked up. Events may be raised on
/// background threads, so subscribers are responsible for marshaling to their own thread.
/// </summary>
public sealed class LogTailer : IDisposable
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private long _position;
    private bool _disposed;

    public LogTailer(string path) => _path = path;

    /// <summary>
    /// Raised with newly read text, in file order. May fire on a background thread.
    /// </summary>
    public event Action<string>? Appended;

    /// <summary>
    /// Begins tailing. Watches the file's directory (so a not-yet-written log is caught when it
    /// appears) and reads whatever already exists on a background thread. A no-op when the
    /// directory itself is absent.
    /// </summary>
    public void Start()
    {
        var directory = Path.GetDirectoryName(_path);
        var name = Path.GetFileName(_path);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name) || !Directory.Exists(directory))
            return;

        // Watching the directory (not requiring the file up front) means the log can show up
        // after the console does — opening the console before the session has written anything,
        // or a fresh run truncating the file, both recover through Changed/Created.
        _watcher = new FileSystemWatcher(directory, name)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.EnableRaisingEvents = true;

        // Read the existing contents off the UI thread; the watcher is already live, so anything
        // written in the meantime is picked up by a later event (reads are idempotent by position).
        // ReadNew tolerates the file not being there yet.
        Task.Run(ReadNew);
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => ReadNew();

    private void ReadNew()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                // The file shrank since we last read it (truncated or rotated) — start over.
                if (stream.Length < _position)
                    _position = 0;

                if (stream.Length == _position)
                    return;

                stream.Seek(_position, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                _position = stream.Length;

                if (text.Length > 0)
                    Appended?.Invoke(text);
            }
            catch (IOException)
            {
                // The writer holds the file briefly; a later change event picks the data back up.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        if (_watcher is { } watcher)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnChanged;
            watcher.Created -= OnChanged;
            watcher.Dispose();
        }
    }
}
