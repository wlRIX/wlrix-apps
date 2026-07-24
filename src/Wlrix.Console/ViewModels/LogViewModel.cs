using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Console.Services;

namespace Wlrix.Console.ViewModels;

/// <summary>
/// One tailed log file: its tab <see cref="Title"/> and the accumulated <see cref="LogText"/>.
/// The tailer runs whether or not the file exists yet, so a log that only appears once its
/// process starts fills in on its own.
/// </summary>
public sealed class LogViewModel : ViewModelBase, IDisposable
{
    private readonly LogTailer _tailer;
    private string _logText = string.Empty;

    public LogViewModel(string title, string path)
    {
        Title = title;
        _tailer = new LogTailer(path);
        _tailer.Appended += OnAppended;
    }

    /// <summary>The tab label, e.g. "Compositor".</summary>
    public string Title { get; }

    /// <summary>The accumulated log text shown in the read-only view.</summary>
    public string LogText
    {
        get => _logText;
        private set => this.RaiseAndSetIfChanged(ref _logText, value);
    }

    /// <summary>Starts reading and watching the log file.</summary>
    public void Start() => _tailer.Start();

    public void Dispose()
    {
        _tailer.Appended -= OnAppended;
        _tailer.Dispose();
    }

    // Tailer events arrive on background threads, so hop back to the UI thread before touching
    // bound state.
    private void OnAppended(string text) => Dispatcher.UIThread.Post(() => LogText += text);
}
