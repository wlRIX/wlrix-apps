using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Avalonia;
using Wlrix.Settings.Client;
using Wlrix.Settings.Keyboard.Services;
using Wlrix.Settings.Login.Core;
using Wlrix.Settings.Login.Localization;
using Wlrix.Settings.Login.Services;
using Wlrix.Settings.Outputs;

namespace Wlrix.Settings.Login.ViewModels;

/// <summary>
/// Backs the Login Settings window: the login screen's keyboard, color scheme and displays.
///
/// Unlike the session's panels, nothing applies as it is changed. The settings are the
/// machine's, written as root by <c>wlrix-login-helper</c>, so a change waits for Apply and its
/// password prompt; and the login screen is not on screen to watch them take effect anyway.
///
/// What the window opens with is what the login screen uses now: <c>/etc/wlrix/greeter.toml</c>
/// when there is one, else the system <c>compositor.toml</c> it falls back to. Both are
/// world-readable, so reading needs no password. The Copy menu fills the form in from the
/// person using it -- the keyboard and scheme from the settings daemon, the displays from the
/// compositor -- and, like everything else here, waits for Apply.
/// </summary>
internal sealed class LoginSettingsViewModel : ViewModelBase, IDisposable
{
    private const int DefaultRepeatDelay = 200;
    private const int DefaultRepeatRate = 25;

    /// <summary>The settings daemon keys the Copy menu reads.</summary>
    private const string PaletteKey = "appearance.palette";

    private const string KeyboardPrefix = "compositor.keyboard.";

    private readonly IOutputFeed _feed;
    private readonly GreeterHelperRunner _runner = new();

    // The xkb values the window has no control for, kept so Apply writes them back unchanged.
    private string? _rules;
    private string? _variant;
    private string? _options;

    private OutputSnapshot? _snapshot;
    private bool _busy;
    private string _status = string.Empty;
    private string _source = string.Empty;
    private XkbEntry _selectedLayout;
    private XkbEntry _selectedModel;
    private bool _repeatEnabled = true;
    private decimal _repeatDelay = DefaultRepeatDelay;
    private decimal _repeatRate = DefaultRepeatRate;
    private WlrixScheme _selectedScheme = WlrixSchemes.Default;

    /// <summary>For the XAML designer: made-up displays, no file read.</summary>
    public LoginSettingsViewModel() : this(new SampleOutputFeed())
    {
    }

    public LoginSettingsViewModel(IOutputFeed feed)
    {
        _feed = feed;
        var (layouts, models) = XkbCatalog.Load();
        var unset = new XkbEntry(string.Empty, Strings.SystemDefault);
        Layouts = [unset, .. layouts];
        Models = [unset, .. models];
        _selectedLayout = unset;
        _selectedModel = unset;
        Install = Strings.InstallStatus(GreeterStatus.Install());
    }

    public IReadOnlyList<XkbEntry> Layouts { get; private set; }

    public IReadOnlyList<XkbEntry> Models { get; private set; }

    public IReadOnlyList<WlrixScheme> Schemes { get; } = WlrixSchemes.All;

    public ObservableCollection<OutputRowViewModel> Outputs { get; } = [];

    /// <summary>Whether the greeter is installed and is the display manager, when it is worth saying.</summary>
    public string Install { get; }

    /// <summary>Where the values shown came from: the login screen's own file, or the system's.</summary>
    public string Source
    {
        get => _source;
        private set => this.RaiseAndSetIfChanged(ref _source, value);
    }

    /// <summary>What the last action came to, or why it could not be done.</summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>True while the helper runs, so Apply cannot be pressed twice.</summary>
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _busy, value);
            this.RaisePropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !_busy;

    public XkbEntry SelectedLayout
    {
        get => _selectedLayout;
        set => this.RaiseAndSetIfChanged(ref _selectedLayout, value);
    }

    public XkbEntry SelectedModel
    {
        get => _selectedModel;
        set => this.RaiseAndSetIfChanged(ref _selectedModel, value);
    }

    /// <summary>Whether held keys repeat. Off writes a repeat rate of 0, as the Keyboard panel does.</summary>
    public bool RepeatEnabled
    {
        get => _repeatEnabled;
        set => this.RaiseAndSetIfChanged(ref _repeatEnabled, value);
    }

    public decimal RepeatDelay
    {
        get => _repeatDelay;
        set => this.RaiseAndSetIfChanged(ref _repeatDelay, value);
    }

    public decimal RepeatRate
    {
        get => _repeatRate;
        set => this.RaiseAndSetIfChanged(ref _repeatRate, value);
    }

    public WlrixScheme SelectedScheme
    {
        get => _selectedScheme;
        set => this.RaiseAndSetIfChanged(ref _selectedScheme, value);
    }

    /// <summary>Reads the current settings and starts listening for the connected displays.</summary>
    public void Start()
    {
        Reload();
        _feed.SnapshotReceived += OnSnapshot;
        _feed.Unavailable += OnUnavailable;
        _feed.Start();
    }

    /// <summary>Shows what the login screen uses now, discarding anything not yet applied.</summary>
    public void Reload()
    {
        var own = GreeterStatus.HasOwnSettings;
        var path = own ? GreeterConfigFile.GreeterPath : GreeterConfigFile.SystemPath;
        Source = own ? Strings.SourceOwn(path) : Strings.SourceSystem(path);

        string text;
        try
        {
            text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = Strings.ReadFailed(path, ex.Message);
            return;
        }

        if (!GreeterConfigFile.TryParse(text, out var config, out var problem))
        {
            Status = Strings.ReadFailed(path, problem ?? string.Empty);
            return;
        }

        Load(config);
    }

    /// <summary>Fills the form in from <paramref name="config"/>: keyboard, scheme and displays.</summary>
    internal void Load(GreeterConfig config)
    {
        LoadKeyboard(config);
        SelectedScheme = WlrixSchemes.ById(config.Palette) ?? WlrixSchemes.Default;
        LoadOutputs(config.Outputs);
    }

    private void LoadKeyboard(GreeterConfig config)
    {
        _rules = config.Rules;
        _variant = config.Variant;
        _options = config.Options;
        SelectedLayout = Select(Layouts, config.Layout, list => Layouts = list, nameof(Layouts));
        SelectedModel = Select(Models, config.Model, list => Models = list, nameof(Models));
        RepeatDelay = config.RepeatDelay ?? DefaultRepeatDelay;
        RepeatEnabled = config.RepeatRate is not 0;
        RepeatRate = config.RepeatRate is null or 0 ? DefaultRepeatRate : config.RepeatRate.Value;
    }

    private void LoadOutputs(IReadOnlyList<GreeterOutput> outputs)
    {
        Outputs.Clear();
        foreach (var output in outputs)
        {
            var row = new OutputRowViewModel(output.Name);
            row.Load(output);
            Outputs.Add(row);
        }

        if (_snapshot is not null)
            Merge(_snapshot);
    }

    /// <summary>
    /// The settings as the form shows them. Displays that are not set are left out, and so are
    /// keyboard values left at "System default".
    /// </summary>
    internal GreeterConfig ToConfig() => new()
    {
        Palette = SelectedScheme.Id,
        Rules = _rules,
        Model = Code(SelectedModel),
        Layout = Code(SelectedLayout),
        Variant = _variant,
        Options = _options,
        RepeatDelay = (int)RepeatDelay,
        RepeatRate = RepeatEnabled ? (int)RepeatRate : 0,
        Outputs = Outputs.Select(row => row.ToOutput()).OfType<GreeterOutput>().ToList(),
    };

    /// <summary>Asks for the password and writes the form into greeter.toml.</summary>
    public async Task ApplyAsync()
    {
        if (!GreeterRequest.TryCreate(ToConfig(), out var request, out var error))
        {
            Status = Strings.Invalid(error);
            return;
        }

        if (await RunAsync(request).ConfigureAwait(true))
        {
            Reload();
            Status = Strings.Applied;
        }
    }

    /// <summary>Removes greeter.toml, so the login screen goes back to the system's settings.</summary>
    public async Task UseSystemDefaultsAsync()
    {
        if (await RunAsync(GreeterRequest.Reset()).ConfigureAwait(true))
        {
            Reload();
            Status = Strings.ResetDone;
        }
    }

    private async Task<bool> RunAsync(GreeterRequest request)
    {
        IsBusy = true;
        Status = string.Empty;
        try
        {
            var result = await _runner.RunAsync(request).ConfigureAwait(true);
            Status = result.Outcome switch
            {
                HelperOutcome.Succeeded => string.Empty,
                HelperOutcome.NotAuthorized => Strings.NotAuthorized,
                HelperOutcome.Unavailable => Strings.HelperUnavailable(result.Message ?? string.Empty),
                _ => Strings.HelperFailed(result.Message ?? string.Empty),
            };
            return result.Outcome == HelperOutcome.Succeeded;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The person's keyboard layout, model and repeat, from the settings daemon.</summary>
    public async Task CopyKeyboardAsync()
    {
        try
        {
            using var client = await SettingsClient.ConnectAsync().ConfigureAwait(true);
            var values = await client.GetAllAsync("compositor").ConfigureAwait(true);
            string? Text(string key) =>
                values.TryGetValue(KeyboardPrefix + key, out var value) && value is string { Length: > 0 } text
                    ? text
                    : null;
            int? Number(string key) =>
                values.TryGetValue(KeyboardPrefix + key, out var value) && value is long or int
                    ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
                    : null;

            LoadKeyboard(new GreeterConfig
            {
                Rules = Text("rules"),
                Model = Text("model"),
                Layout = Text("layout"),
                Variant = Text("variant"),
                Options = Text("options"),
                RepeatDelay = Number("repeat_delay"),
                RepeatRate = Number("repeat_rate"),
            });
            Status = Strings.Copied;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Status = Strings.NoSettingsService(ex.Message);
        }
    }

    /// <summary>The person's color scheme, from the settings daemon.</summary>
    public async Task CopySchemeAsync()
    {
        try
        {
            using var client = await SettingsClient.ConnectAsync().ConfigureAwait(true);
            var palette = await client.GetAsync(PaletteKey).ConfigureAwait(true) as string;
            SelectedScheme = WlrixSchemes.ById(palette) ?? WlrixSchemes.Default;
            Status = Strings.Copied;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Status = Strings.NoSettingsService(ex.Message);
        }
    }

    /// <summary>Every display exactly as the session has it now.</summary>
    public void CopyDisplays()
    {
        if (_snapshot is null)
        {
            Status = Strings.NoDisplays;
            return;
        }

        LoadOutputs(_snapshot.Heads.Select(OutputMapping.FromHead).ToList());
        Status = Strings.Copied;
    }

    public async Task CopyAllAsync()
    {
        await CopyKeyboardAsync().ConfigureAwait(true);
        if (Status != Strings.Copied)
            return;
        await CopySchemeAsync().ConfigureAwait(true);
        if (Status != Strings.Copied)
            return;
        CopyDisplays();
    }

    /// <summary>
    /// Lets the compositor lay out every display itself: nothing is set, and entries for
    /// monitors that are not connected are dropped.
    /// </summary>
    public void AutomaticDisplays()
    {
        foreach (var row in Outputs.Where(row => row.IsDisconnected).ToList())
            Outputs.Remove(row);
        foreach (var row in Outputs)
            row.IsSet = false;
    }

    /// <summary>Drops a disconnected monitor's entry.</summary>
    public void Remove(OutputRowViewModel row) => Outputs.Remove(row);

    private void OnSnapshot(OutputSnapshot snapshot) => Dispatcher.UIThread.Post(() =>
    {
        _snapshot = snapshot;
        Merge(snapshot);
    });

    private void OnUnavailable() => Dispatcher.UIThread.Post(() =>
    {
        if (string.IsNullOrEmpty(Status))
            Status = Strings.NoDisplays;
    });

    /// <summary>
    /// Brings the rows in step with the connected monitors: one row each, joined to an entry from
    /// the file by connector name, plus the file's entries for monitors that are not plugged in.
    /// </summary>
    internal void Merge(OutputSnapshot snapshot)
    {
        foreach (var head in snapshot.Heads)
        {
            var row = Outputs.FirstOrDefault(row => row.Name == head.Name);
            if (row is null)
            {
                row = new OutputRowViewModel(head.Name);
                Outputs.Add(row);
            }

            row.Connect(head);
        }

        foreach (var row in Outputs.Where(row => snapshot.Heads.All(head => head.Name != row.Name)))
            row.Connect(null);
    }

    /// <summary>
    /// The entry for <paramref name="code"/>, adding one to the list if xkeyboard-config does not
    /// know it (a hand-set value), so what the file says can always be shown and written back.
    /// </summary>
    private XkbEntry Select(IReadOnlyList<XkbEntry> list, string? code, Action<IReadOnlyList<XkbEntry>> replace,
        string property)
    {
        if (string.IsNullOrEmpty(code))
            return list[0];
        if (list.FirstOrDefault(entry => entry.Code == code) is { } found)
            return found;

        var entry = new XkbEntry(code, code);
        replace([list[0], entry, .. list.Skip(1)]);
        this.RaisePropertyChanged(property);
        return entry;
    }

    private static string? Code(XkbEntry entry) => entry.Code.Length == 0 ? null : entry.Code;

    public void Dispose()
    {
        _feed.SnapshotReceived -= OnSnapshot;
        _feed.Unavailable -= OnUnavailable;
        _feed.Dispose();
    }
}
