using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Settings.Client;
using Wlrix.Settings.Keyboard.Services;

namespace Wlrix.Settings.Keyboard.ViewModels;

/// <summary>
/// Backs the keyboard settings window.
///
/// Each change is committed shortly after the user stops adjusting, so the Test box reflects it
/// live; slider drags are debounced so a drag writes once, on release. <see cref="Reset"/> puts
/// everything back to how it was when the window opened.
///
/// Everything about the config file goes through <c>wlrix-settings-daemon</c>: this app no
/// longer knows where <c>compositor.toml</c> lives, how to edit TOML without destroying the
/// user's comments, or how to find the compositor to signal it. It also no longer knows what
/// the defaults or the ranges are — those come from the daemon's schema, so there is one place
/// they are written down.
///
/// One commit is one <c>SetMany</c>. That is not only tidier than a write followed by a
/// <c>SIGHUP</c>: those were two steps with a real race between them, where the compositor
/// could be signaled against a file that had been replaced again in the meantime.
/// </summary>
public sealed class KeyboardSettingsViewModel : ViewModelBase, IDisposable
{
    private const string LayoutKey = "compositor.keyboard.layout";
    private const string ModelKey = "compositor.keyboard.model";
    private const string DelayKey = "compositor.keyboard.repeat_delay";
    private const string RateKey = "compositor.keyboard.repeat_rate";

    /// <summary>
    /// Shown until the daemon has been asked, and if it never answers.
    ///
    /// <c>layout</c> and <c>model</c> are the two settings with no default the daemon can name:
    /// absent means "let libxkbcommon decide", which is not a string. So the app supplies
    /// something to display, and only for those two.
    /// </summary>
    private const string FallbackLayout = "us";
    private const string FallbackModel = "pc105";

    private readonly DispatcherTimer _commitTimer;

    private SettingsClient? _client;

    // What the daemon says about each setting: the ranges the sliders use, and the defaults.
    private double _rateDefault = 25;
    private double _delayDefault = 200;

    // The state captured once the settings have loaded, for Reset.
    private bool _openEnabled;
    private double _openRate;
    private double _openDelay;
    private string _openLayout = FallbackLayout;
    private string _openModel = FallbackModel;

    // True while loading, or while applying a change that came from somewhere else, so those do
    // not each schedule a commit of their own.
    private bool _loading;

    private bool _ready;
    private string _status = string.Empty;
    private bool _repeatEnabled = true;
    private double _repeatRate = 25;
    private double _repeatDelay = 200;
    private XkbEntry _selectedLayout;
    private XkbEntry _selectedModel;

    /// <summary>
    /// Builds the window's model without touching the daemon, so the XAML designer and a
    /// session with no settings service both get something to draw.
    /// <see cref="InitializeAsync"/> is what fills it in.
    /// </summary>
    public KeyboardSettingsViewModel()
    {
        var (layouts, models) = XkbCatalog.Load();
        Layouts = EnsureContains(layouts, FallbackLayout);
        Models = EnsureContains(models, FallbackModel);
        _selectedLayout = FindByCode(Layouts, FallbackLayout);
        _selectedModel = FindByCode(Models, FallbackModel);

        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _commitTimer.Tick += OnCommitTick;
    }

    public IReadOnlyList<XkbEntry> Layouts { get; private set; }

    public IReadOnlyList<XkbEntry> Models { get; private set; }

    /// <summary>Whether keys repeat when held. Off writes a repeat rate of 0.</summary>
    public bool RepeatEnabled
    {
        get => _repeatEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _repeatEnabled, value);
            ScheduleCommit();
        }
    }

    /// <summary>Repeats per second.</summary>
    public double RepeatRate
    {
        get => _repeatRate;
        set
        {
            this.RaiseAndSetIfChanged(ref _repeatRate, value);
            this.RaisePropertyChanged(nameof(RepeatRateText));
            ScheduleCommit();
        }
    }

    /// <summary>Milliseconds before a held key starts repeating.</summary>
    public double RepeatDelay
    {
        get => _repeatDelay;
        set
        {
            this.RaiseAndSetIfChanged(ref _repeatDelay, value);
            this.RaisePropertyChanged(nameof(RepeatDelayText));
            ScheduleCommit();
        }
    }

    public XkbEntry SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedLayout, value);
            ScheduleCommit();
        }
    }

    public XkbEntry SelectedModel
    {
        get => _selectedModel;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedModel, value);
            ScheduleCommit();
        }
    }

    /// <summary>Whether the settings have loaded and the controls mean anything yet.</summary>
    public bool IsReady
    {
        get => _ready;
        private set => this.RaiseAndSetIfChanged(ref _ready, value);
    }

    /// <summary>
    /// What happened to the last change, when it is worth saying.
    ///
    /// Empty when everything applied. This is where "the compositor isn't running; this applies
    /// at next login" ends up, instead of the window appearing to have done nothing.
    /// </summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>The current repeat speed, for the slider's tooltip.</summary>
    public string RepeatRateText => ((int)Math.Round(RepeatRate)).ToString();

    /// <summary>The current repeat delay, for the slider's tooltip.</summary>
    public string RepeatDelayText => $"{(int)Math.Round(RepeatDelay)} ms";

    /// <summary>
    /// Ask the daemon what these settings are and what they currently hold, then start
    /// listening for changes from anywhere else.
    ///
    /// Failing here leaves the window drawn but not ready, with the reason in
    /// <see cref="Status"/>. There is deliberately no fallback to editing the file directly:
    /// that is the duplication this whole arrangement exists to remove, and this app has no
    /// TOML parser to do it correctly with.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            _client = await SettingsClient.ConnectAsync().ConfigureAwait(true);
            var described = (await _client.DescribeAsync("compositor").ConfigureAwait(true))
                .ToDictionary(setting => setting.Key);
            var values = await _client.GetAllAsync("compositor").ConfigureAwait(true);

            _rateDefault = AsNumber(described.GetValueOrDefault(RateKey)?.Default, 25);
            _delayDefault = AsNumber(described.GetValueOrDefault(DelayKey)?.Default, 200);

            Apply(values);

            _openEnabled = _repeatEnabled;
            _openRate = _repeatRate;
            _openDelay = _repeatDelay;
            _openLayout = _selectedLayout.Code;
            _openModel = _selectedModel.Code;

            await _client.WatchAsync().ConfigureAwait(true);
            _client.Changed += OnChangedElsewhere;
            _client.FileInvalid += OnFileInvalid;

            IsReady = true;
        }
        catch (Exception e)
        {
            // Bus activation means "not on the bus" is a broken install rather than an idle
            // session, so say so rather than silently doing nothing.
            Status = $"No settings service: {e.Message}";
        }
    }

    /// <summary>Restore the settings to how they were when the window opened, and apply them.</summary>
    public void Reset()
    {
        _loading = true;
        RepeatEnabled = _openEnabled;
        RepeatRate = _openRate;
        RepeatDelay = _openDelay;
        SelectedLayout = FindByCode(Layouts, _openLayout);
        SelectedModel = FindByCode(Models, _openModel);
        _loading = false;
        _ = CommitAsync();
    }

    public void Dispose()
    {
        // Flush a change still waiting out its debounce, so closing right after an adjustment
        // does not drop it. Waited on rather than fired and forgotten: the process is about to
        // exit, and an unawaited write would be lost with it.
        var pending = _commitTimer.IsEnabled;
        _commitTimer.Stop();
        _commitTimer.Tick -= OnCommitTick;
        if (pending)
            CommitAsync().GetAwaiter().GetResult();

        if (_client is not null)
        {
            _client.Changed -= OnChangedElsewhere;
            _client.FileInvalid -= OnFileInvalid;
            _client.Dispose();
            _client = null;
        }
    }

    private void OnCommitTick(object? sender, EventArgs e)
    {
        _commitTimer.Stop();
        _ = CommitAsync();
    }

    // Coalesce a burst of changes (a slider drag) into one write shortly after it settles.
    private void ScheduleCommit()
    {
        if (_loading || !IsReady)
            return;
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private async Task CommitAsync()
    {
        if (_client is null)
            return;

        var values = new Dictionary<string, object>
        {
            [LayoutKey] = _selectedLayout.Code,
            [ModelKey] = _selectedModel.Code,
            [DelayKey] = (long)Math.Round(_repeatDelay),
            [RateKey] = _repeatEnabled ? (long)Math.Round(_repeatRate) : 0L,
        };

        try
        {
            var result = await _client.SetManyAsync(values).ConfigureAwait(true);
            Status = result.Advice ?? string.Empty;
        }
        catch (Exception e)
        {
            // The daemon refusing a value, or the compositor refusing the file, both land here
            // with a message that names the key. Showing it beats the old behavior, which was
            // to swallow the failure and leave the UI claiming a setting that was never applied.
            Status = e.Message.Split('\n')[0];
        }
    }

    /// <summary>
    /// Somebody else changed a setting we are showing — most likely the person editing
    /// <c>compositor.toml</c> in an editor with this window open.
    /// </summary>
    private void OnChangedElsewhere(object? sender, SettingsChangedEventArgs e)
    {
        // The echo of our own write, which the daemon sends to everyone including us. Acting on
        // it would re-arm the debounce timer and commit again, forever.
        if (e.Origin == _client?.UniqueName)
            return;

        // Raised on a bus thread; every property below is bound to a control.
        Dispatcher.UIThread.Post(() =>
        {
            _loading = true;
            Apply(e.Values);
            _loading = false;
        });
    }

    private void OnFileInvalid(object? sender, SettingsFileInvalidEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
            Status = $"{e.Path} is not valid; showing the last good values.");

    /// <summary>
    /// Put whatever of these four settings is present into the controls.
    ///
    /// Used for both the initial load and a change from elsewhere, so a hand-edit that touches
    /// only the layout leaves the sliders where they are.
    /// </summary>
    private void Apply(IReadOnlyDictionary<string, object?> values)
    {
        if (values.TryGetValue(RateKey, out var rate) && rate is not null)
        {
            var reported = AsNumber(rate, _rateDefault);
            // The compositor reads a repeat rate of 0 as "no repeat", so that is the checkbox
            // rather than a slider position -- and the slider keeps its last real value so
            // turning repeat back on does not land on zero.
            RepeatEnabled = reported != 0;
            if (reported != 0)
                RepeatRate = reported;
        }

        if (values.TryGetValue(DelayKey, out var delay) && delay is not null)
            RepeatDelay = AsNumber(delay, _delayDefault);

        if (values.TryGetValue(LayoutKey, out var layout) && layout is string code)
        {
            Layouts = EnsureContains(Layouts, code);
            this.RaisePropertyChanged(nameof(Layouts));
            SelectedLayout = FindByCode(Layouts, code);
        }

        if (values.TryGetValue(ModelKey, out var model) && model is string modelCode)
        {
            Models = EnsureContains(Models, modelCode);
            this.RaisePropertyChanged(nameof(Models));
            SelectedModel = FindByCode(Models, modelCode);
        }
    }

    private static double AsNumber(object? value, double fallback) => value switch
    {
        long number => number,
        int number => number,
        double number => number,
        _ => fallback,
    };

    // Guarantee the current code is selectable even if the catalog does not list it (a hand-set
    // or comma-separated layout like "jp,us"), so opening never silently drops it.
    private static IReadOnlyList<XkbEntry> EnsureContains(IReadOnlyList<XkbEntry> entries, string code)
    {
        if (string.IsNullOrEmpty(code) || entries.Any(entry => entry.Code == code))
            return entries;
        return [new XkbEntry(code, code), .. entries];
    }

    private static XkbEntry FindByCode(IReadOnlyList<XkbEntry> entries, string code) =>
        entries.FirstOrDefault(entry => entry.Code == code) ?? entries[0];
}
