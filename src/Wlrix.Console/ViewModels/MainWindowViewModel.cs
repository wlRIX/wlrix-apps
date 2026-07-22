using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Console.Services;

namespace Wlrix.Console.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly LogTailer _tailer;
    private string _logText = string.Empty;

    public MainWindowViewModel()
    {
        var path = Path.Combine(Path.GetTempPath(), "wlrix.log");
        _tailer = new LogTailer(path);
        _tailer.Appended += OnAppended;
        _tailer.FileMissing += OnFileMissing;
    }

    /// <summary>
    /// Raised on the UI thread when the log file is missing at startup.
    /// </summary>
    public event Action? LogFileMissing;

    /// <summary>
    /// The accumulated log text shown in the read-only view.
    /// </summary>
    public string LogText
    {
        get => _logText;
        private set => this.RaiseAndSetIfChanged(ref _logText, value);
    }

    /// <summary>
    /// Starts reading and watching the log file. Call once the window is shown.
    /// </summary>
    public void Start() => _tailer.Start();

    public void Dispose()
    {
        _tailer.Appended -= OnAppended;
        _tailer.FileMissing -= OnFileMissing;
        _tailer.Dispose();
    }

    // Tailer events arrive on background threads, so hop back to the UI thread before touching
    // bound state.
    private void OnAppended(string text) => Dispatcher.UIThread.Post(() => LogText += text);

    private void OnFileMissing() => Dispatcher.UIThread.Post(() => LogFileMissing?.Invoke());
}
