namespace Wlrix.Console.Services;

/// <summary>
/// Tails a log file: reads its existing contents and then watches for appended data, raising
/// <see cref="Appended"/> with each new chunk in file order. If the file is absent when
/// <see cref="Start"/> is called, <see cref="FileMissing"/> is raised instead and no watching
/// begins. Events may be raised on background threads, so subscribers are responsible for
/// marshaling to their own thread.
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
    /// Raised once from <see cref="Start"/> when the file does not exist.
    /// </summary>
    public event Action? FileMissing;

    /// <summary>
    /// Begins tailing. Returns <c>false</c> (after raising <see cref="FileMissing"/>) when the
    /// file is absent; otherwise starts watching and reads the current contents on a background
    /// thread, returning <c>true</c>.
    /// </summary>
    public bool Start()
    {
        if (!File.Exists(_path))
        {
            FileMissing?.Invoke();
            return false;
        }

        var directory = Path.GetDirectoryName(_path);
        var name = Path.GetFileName(_path);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name))
            return false;

        _watcher = new FileSystemWatcher(directory, name)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.EnableRaisingEvents = true;

        // Read the existing contents off the UI thread; the watcher is already live, so anything
        // written in the meantime is picked up by a later event (reads are idempotent by position).
        Task.Run(ReadNew);
        return true;
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
