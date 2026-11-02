using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Console.Services;

namespace Wlrix.Console.ViewModels;

/// <summary>
/// One tailed log file: its tab <see cref="Title"/> and its most recent <see cref="Lines"/>.
/// The tailer runs whether or not the file exists yet, so a log that only appears once its
/// process starts fills in on its own.
/// </summary>
public sealed class LogViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// How many lines a tab keeps: a few days of a busy session log is well over a million, more
    /// than anyone scrolls through here and more than is worth holding in memory.
    /// </summary>
    public const int MaxLines = 50_000;

    private readonly LogTailer _tailer;
    private readonly Action<Action> _post;
    private readonly Lock _gate = new();
    private List<string> _pending = [];
    private bool _restartPending;
    private bool _flushPosted;

    public LogViewModel(string title, string path)
        : this(title, new LogTailer(path), MaxLines, action => Dispatcher.UIThread.Post(action))
    {
    }

    /// <summary>
    /// For tests: <paramref name="post"/> stands in for posting to the UI thread.
    /// </summary>
    internal LogViewModel(string title, LogTailer tailer, int maxLines, Action<Action> post)
    {
        Title = title;
        Lines = new LogLineBuffer(maxLines);
        _tailer = tailer;
        _post = post;
        _tailer.LinesAppended += OnLinesAppended;
        _tailer.Restarted += OnRestarted;
    }

    /// <summary>The tab label, e.g. "Compositor".</summary>
    public string Title { get; }

    /// <summary>The most recent lines of the log, oldest first, shown in the virtualized view.</summary>
    public LogLineBuffer Lines { get; }

    /// <summary>Whether there is anything to show; the view shows a placeholder until there is.</summary>
    public bool HasLines => Lines.Count > 0;

    /// <summary>Starts reading and watching the log file.</summary>
    public void Start() => _tailer.Start();

    public void Dispose()
    {
        _tailer.LinesAppended -= OnLinesAppended;
        _tailer.Restarted -= OnRestarted;
        _tailer.Dispose();
    }

    // Tailer events arrive on background threads, as often as the log is written to. They are
    // gathered here and handed to the UI thread in one batch per dispatcher turn, so a burst of
    // writes is one update of the view rather than one per write.
    private void OnLinesAppended(IReadOnlyList<string> lines)
    {
        lock (_gate)
        {
            _pending.AddRange(lines);
            // Lines the buffer would drop straight away are not worth carrying to the UI thread.
            if (_pending.Count > Lines.Capacity)
                _pending.RemoveRange(0, _pending.Count - Lines.Capacity);
            PostFlush();
        }
    }

    private void OnRestarted()
    {
        lock (_gate)
        {
            _pending.Clear();
            _restartPending = true;
            PostFlush();
        }
    }

    // Called with _gate held.
    private void PostFlush()
    {
        if (_flushPosted)
            return;
        _flushPosted = true;
        _post(Flush);
    }

    private void Flush()
    {
        List<string> lines;
        bool restart;
        lock (_gate)
        {
            lines = _pending;
            restart = _restartPending;
            _pending = [];
            _restartPending = false;
            _flushPosted = false;
        }

        var had = HasLines;
        if (restart)
            Lines.Clear();
        Lines.AddRange(lines);
        if (HasLines != had)
            this.RaisePropertyChanged(nameof(HasLines));
    }
}
