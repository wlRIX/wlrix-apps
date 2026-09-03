using ReactiveUI;
using Wlrix.Console.Localization;

namespace Wlrix.Console.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    // The per-user runtime directory each wlRIX process truncates its log into. Matches the
    // Rust side's log_dir(): $XDG_RUNTIME_DIR when it is an absolute path, else the temp dir.
    private static readonly string LogDirectory = ResolveLogDirectory();

    private LogViewModel _selectedLog;

    public MainWindowViewModel()
    {
        Logs =
        [
            new LogViewModel(Strings.TabCompositor, Path.Combine(LogDirectory, "wlrix-compositor.log")),
            new LogViewModel(Strings.TabSession, Path.Combine(LogDirectory, "wlrix-session.log")),
        ];
        _selectedLog = Logs[0];
    }

    /// <summary>The tailed logs, one per bottom tab.</summary>
    public IReadOnlyList<LogViewModel> Logs { get; }

    /// <summary>The tab currently shown.</summary>
    public LogViewModel SelectedLog
    {
        get => _selectedLog;
        set => this.RaiseAndSetIfChanged(ref _selectedLog, value);
    }

    /// <summary>Starts tailing every log. Call once the window is shown.</summary>
    public void Start()
    {
        foreach (var log in Logs)
            log.Start();
    }

    public void Dispose()
    {
        foreach (var log in Logs)
            log.Dispose();
    }

    private static string ResolveLogDirectory()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtime) && Path.IsPathRooted(runtime))
            return runtime;
        return Path.GetTempPath();
    }
}
