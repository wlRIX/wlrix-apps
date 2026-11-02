using System.Text;

namespace Wlrix.Console.Services;

/// <summary>
/// Tails a log file: reads the end of its existing contents and then watches for appended data,
/// raising <see cref="LinesAppended"/> with each batch of new complete lines in file order. The
/// file need not exist when <see cref="Start"/> is called — the directory is watched, so a log
/// that appears later (or is truncated and rewritten each time its process restarts) is picked
/// up. Events may be raised on background threads, so subscribers are responsible for marshaling
/// to their own thread.
/// </summary>
/// <remarks>
/// A session that has run for a week leaves a log of hundreds of megabytes, nearly all of it
/// older than anyone opening the console wants to see. So no read takes in more than the last
/// <c>backlogBytes</c> of the file: the first read starts that far from the end, and so does a
/// later one that finds more than that written since the last (the earlier part is skipped, the
/// same as if it had scrolled out of view). Reading only whole lines means a read that lands in
/// the middle of one drops that partial line, and a line still being written is left for the
/// next read instead of being shown in two halves.
/// </remarks>
public sealed class LogTailer : IDisposable
{
    /// <summary>
    /// How much of the end of the file the console reads at most: enough for the tens of
    /// thousands of lines the view keeps, a fraction of a week's log.
    /// </summary>
    public const int DefaultBacklogBytes = 8 * 1024 * 1024;

    private readonly string _path;
    private readonly int _backlogBytes;
    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private long _position;
    // _position is inside a line whose start was skipped; the next read drops up to its end.
    private bool _midLine;
    private bool _disposed;

    public LogTailer(string path, int backlogBytes = DefaultBacklogBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(backlogBytes, 1);
        _path = path;
        _backlogBytes = backlogBytes;
    }

    /// <summary>
    /// Raised with newly read complete lines (without their line endings), in file order. May
    /// fire on a background thread.
    /// </summary>
    public event Action<IReadOnlyList<string>>? LinesAppended;

    /// <summary>
    /// Raised when the file shrank — it was truncated or replaced, as when its process
    /// restarts — before the lines read from its new contents. May fire on a background thread.
    /// </summary>
    public event Action? Restarted;

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

    /// <summary>
    /// Reads whatever has been appended since the last read. Called by the watcher; internal so
    /// tests can drive it without waiting on file system notifications.
    /// </summary>
    internal void ReadNew()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                var length = stream.Length;

                // The file shrank since we last read it (truncated or rotated) — start over.
                if (length < _position)
                {
                    _position = 0;
                    _midLine = false;
                    Restarted?.Invoke();
                }

                if (length == _position)
                    return;

                // Too much to show since the last read: start a backlog's worth from the end, one
                // byte early so a line that starts exactly there is not mistaken for a partial one.
                var start = _position;
                var dropFirst = _midLine;
                var backlogStart = length - _backlogBytes - 1;
                if (backlogStart > start)
                {
                    start = backlogStart;
                    dropFirst = true;
                }

                var buffer = new byte[length - start];
                stream.Seek(start, SeekOrigin.Begin);
                stream.ReadExactly(buffer);
                var span = buffer.AsSpan();

                // Whole lines only: from the first line start (past the partial line the read
                // began in, when it began in one) through the last newline.
                var first = 0;
                if (dropFirst)
                {
                    var newline = span.IndexOf((byte)'\n');
                    if (newline < 0)
                    {
                        // Still inside a line longer than the whole backlog. It will never be
                        // shown, so move past it rather than rereading it forever.
                        _position = length;
                        _midLine = true;
                        return;
                    }
                    first = newline + 1;
                }

                // A line still being written is left for the next read.
                var last = span.LastIndexOf((byte)'\n');
                if (last < 0)
                    return;

                _position = start + last + 1;
                _midLine = false;
                if (last < first)
                    return;

                var lines = SplitLines(span[first..last]);
                if (lines.Count > 0)
                    LinesAppended?.Invoke(lines);
            }
            catch (IOException)
            {
                // The writer holds the file briefly; a later change event picks the data back up.
            }
        }
    }

    /// <summary>
    /// Decodes newline-separated UTF-8 into lines, dropping a trailing CR from each. The input
    /// excludes the final newline, so it holds exactly one line more than it has newlines.
    /// </summary>
    internal static List<string> SplitLines(ReadOnlySpan<byte> text)
    {
        var lines = new List<string>(text.Count((byte)'\n') + 1);
        while (true)
        {
            var end = text.IndexOf((byte)'\n');
            var line = end < 0 ? text : text[..end];
            if (line.Length > 0 && line[^1] == (byte)'\r')
                line = line[..^1];
            lines.Add(Encoding.UTF8.GetString(line));

            if (end < 0)
                return lines;
            text = text[(end + 1)..];
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
