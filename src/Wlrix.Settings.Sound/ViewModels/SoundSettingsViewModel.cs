// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Settings.Client;
using Wlrix.Settings.Sound.Localization;
using Wlrix.Settings.Sound.Services;

namespace Wlrix.Settings.Sound.ViewModels;

/// <summary>An entry in one of the panel's option menus.</summary>
/// <param name="Value">What is written to the setting.</param>
/// <param name="Label">What the menu shows.</param>
public sealed record Choice(string Value, string Label);

/// <summary>
/// Backs IRIX's System &amp; Desktop Sounds panel: Mute System, a checkbox per kind of sound, and
/// the output desktop sounds play through -- plus, as a wlRIX addition, the sound theme.
/// </summary>
/// <remarks>
/// <para>
/// Two different things are set here, and they go two different ways. <b>Mute System</b> is the
/// sound server's: on IRIX it muted the machine, not the desktop's sounds, so it mutes the
/// default output through PipeWire and is the same mute <c>Wlrix.Settings.Audio</c> shows.
/// Everything else is <c>sounds.toml</c>, written through <c>wlrix-settings-daemon</c> like
/// every other panel's settings and followed by <c>wlrix-sound</c>.
/// </para>
/// <para>
/// Built like <c>Wlrix.Settings.Windows</c>: one commit is one <c>SetMany</c> of every setting
/// shown, after a short debounce, and Reset puts back what was there when the window opened.
/// </para>
/// </remarks>
public sealed class SoundSettingsViewModel : ViewModelBase, IDisposable
{
    private const string Namespace = "sounds";
    private const string StartupKey = "sounds.play.startup";
    private const string DesktopKey = "sounds.play.desktop";
    private const string WindowsKey = "sounds.play.windows";
    private const string AlertsKey = "sounds.play.alerts";
    private const string BellKey = "sounds.play.bell";
    private const string OutputKey = "sounds.output";
    private const string ThemeKey = "sounds.theme";

    /// <summary>What <c>sounds.toml</c> means by no output: follow the default.</summary>
    private const string DefaultOutput = "";

    /// <summary>The sound a new theme or output is heard with. Every theme has one.</summary>
    private const string SampleSound = "dialog-information";

    private readonly DispatcherTimer _commitTimer;
    private SettingsClient? _client;
    private SoundDaemon? _daemon;

    private Snapshot _open = Snapshot.Defaults;
    private bool _loading;
    private bool _ready;
    private string _status = string.Empty;

    private bool _muteSystem;
    private bool _muteKnown;
    private bool _playStartup;
    private bool _playDesktop = true;
    private bool _playWindows;
    private bool _playAlerts = true;
    private bool _playBell = true;
    private IReadOnlyList<Choice> _outputs;
    private Choice? _selectedOutput;
    private IReadOnlyList<Choice> _themes;
    private Choice? _selectedTheme;

    /// <summary>
    /// Builds the window's model without touching the bus or the sound server, so the XAML
    /// designer and a session with neither both get something to draw.
    /// </summary>
    public SoundSettingsViewModel()
    {
        _outputs = [DefaultOutputChoice];
        _selectedOutput = _outputs[0];
        _themes = [new Choice("wlrix", "wlRIX")];
        _selectedTheme = _themes[0];

        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _commitTimer.Tick += OnCommitTick;
    }

    private static Choice DefaultOutputChoice => new(DefaultOutput, Strings.DefaultOutput);

    /// <summary>The default output's mute: the machine's, not only the desktop's sounds.</summary>
    public bool MuteSystem
    {
        get => _muteSystem;
        set
        {
            if (_muteSystem == value)
                return;
            this.RaiseAndSetIfChanged(ref _muteSystem, value);
            if (!_loading)
                _ = ApplyMuteAsync(value);
        }
    }

    /// <summary>Whether the sound server answered about the mute, so the box means anything.</summary>
    public bool MuteKnown
    {
        get => _muteKnown;
        private set => this.RaiseAndSetIfChanged(ref _muteKnown, value);
    }

    /// <summary>Startup &amp; Shutdown Tunes: logging in, logging out, locking.</summary>
    public bool PlayStartup
    {
        get => _playStartup;
        set => SetCategory(ref _playStartup, value, "desktop-logout");
    }

    /// <summary>Desktop Sounds: icons, folders, launching, the dumpster.</summary>
    public bool PlayDesktop
    {
        get => _playDesktop;
        set => SetCategory(ref _playDesktop, value, "wlrix-launch");
    }

    /// <summary>Window Sounds, wlRIX's addition: minimize, restore, desk switches.</summary>
    public bool PlayWindows
    {
        get => _playWindows;
        set => SetCategory(ref _playWindows, value, "window-minimized");
    }

    /// <summary>System Alerts Sounds: dialogs.</summary>
    public bool PlayAlerts
    {
        get => _playAlerts;
        set => SetCategory(ref _playAlerts, value, "dialog-warning");
    }

    /// <summary>Keyboard Bell.</summary>
    public bool PlayBell
    {
        get => _playBell;
        set => SetCategory(ref _playBell, value, "bell");
    }

    /// <summary>Default Output, then every sink PipeWire has.</summary>
    public IReadOnlyList<Choice> Outputs
    {
        get => _outputs;
        private set => this.RaiseAndSetIfChanged(ref _outputs, value);
    }

    public Choice? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedOutput))
                return;
            this.RaiseAndSetIfChanged(ref _selectedOutput, value);
            if (!_loading && IsReady)
                ScheduleCommit(previewAfter: true);
        }
    }

    /// <summary>Every installed sound theme.</summary>
    public IReadOnlyList<Choice> Themes
    {
        get => _themes;
        private set => this.RaiseAndSetIfChanged(ref _themes, value);
    }

    public Choice? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedTheme))
                return;
            this.RaiseAndSetIfChanged(ref _selectedTheme, value);
            if (!_loading && IsReady)
            {
                Preview(value.Value, SampleSound);
                ScheduleCommit();
            }
        }
    }

    /// <summary>Whether the settings have loaded and the controls mean anything yet.</summary>
    public bool IsReady
    {
        get => _ready;
        private set => this.RaiseAndSetIfChanged(ref _ready, value);
    }

    /// <summary>What happened to the last change, when it is worth saying. Empty otherwise.</summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>
    /// Read the settings, the installed themes, the outputs and the mute, then start following
    /// changes made anywhere else.
    /// </summary>
    public async Task InitializeAsync()
    {
        // The sound server first and on its own: it answers whatever the settings daemon does.
        await RefreshSoundServerAsync().ConfigureAwait(true);

        try
        {
            _daemon = SoundDaemon.Connect();
            var themes = await _daemon.ListThemesAsync().ConfigureAwait(true);
            if (themes.Count > 0)
                Themes = [.. themes.Select(theme => new Choice(theme.Id, theme.Name))];
        }
        catch (Exception)
        {
            // No sound daemon: the menu keeps the one theme it knows is the default, and the
            // settings can still be written for when there is one.
            _daemon = null;
        }

        try
        {
            _client = await SettingsClient.ConnectAsync().ConfigureAwait(true);
            var values = await _client.GetAllAsync(Namespace).ConfigureAwait(true);

            _loading = true;
            Apply(values);
            _loading = false;
            _open = Capture();

            await _client.WatchAsync().ConfigureAwait(true);
            _client.Changed += OnChangedElsewhere;
            _client.FileInvalid += OnFileInvalid;

            IsReady = true;
        }
        catch (Exception e)
        {
            _loading = false;
            Status = Strings.NoSettingsService(e.Message);
        }
    }

    /// <summary>
    /// Ask PipeWire again for the outputs and the mute. Called when the window comes forward, so
    /// a headset plugged in or a mute set in the audio panel meanwhile shows here.
    /// </summary>
    public async Task RefreshSoundServerAsync()
    {
        var outputs = await Pipewire.ListOutputsAsync().ConfigureAwait(true);
        var muted = await Pipewire.IsMutedAsync().ConfigureAwait(true);

        _loading = true;
        var wanted = _selectedOutput?.Value ?? DefaultOutput;
        Outputs = [DefaultOutputChoice, .. outputs.Select(output => new Choice(output.Name, output.Description))];
        SelectOutput(wanted);
        MuteKnown = muted is not null;
        MuteSystem = muted ?? false;
        _loading = false;
    }

    /// <summary>Put the settings back to how they were when the window opened, and apply them.</summary>
    public void Reset()
    {
        _loading = true;
        Restore(_open);
        _loading = false;
        _ = CommitAsync(previewAfter: false);
    }

    public void Dispose()
    {
        // Flush a change still waiting out its debounce, so closing straight after a click does
        // not lose it. Waited on: the process is about to exit, and an unawaited write would go
        // with it.
        var pending = _commitTimer.IsEnabled;
        _commitTimer.Stop();
        _commitTimer.Tick -= OnCommitTick;
        if (pending)
            CommitAsync(previewAfter: false).GetAwaiter().GetResult();

        if (_client is not null)
        {
            _client.Changed -= OnChangedElsewhere;
            _client.FileInvalid -= OnFileInvalid;
            _client.Dispose();
            _client = null;
        }
    }

    /// <summary>
    /// A category checkbox moved. Turning one on plays one of its sounds, as a choice of sound
    /// should; turning one off is heard as silence soon enough.
    /// </summary>
    private void SetCategory(ref bool field, bool value, string sample, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (field == value)
            return;
        field = value;
        this.RaisePropertyChanged(property);
        if (_loading || !IsReady)
            return;
        if (value)
            Preview(string.Empty, sample);
        ScheduleCommit();
    }

    private async Task ApplyMuteAsync(bool muted)
    {
        if (!await Pipewire.SetMutedAsync(muted).ConfigureAwait(true))
        {
            Status = Strings.MuteFailed;
            // Show what the sound server says, not what was asked for.
            var actual = await Pipewire.IsMutedAsync().ConfigureAwait(true);
            _loading = true;
            MuteKnown = actual is not null;
            MuteSystem = actual ?? false;
            _loading = false;
        }
    }

    private void Preview(string theme, string name)
    {
        if (_daemon is null)
            return;
        _ = PreviewAsync(_daemon, theme, name);

        static async Task PreviewAsync(SoundDaemon daemon, string theme, string name)
        {
            try
            {
                await daemon.PreviewAsync(theme, name).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A sample that does not play is not worth a word on the status line.
            }
        }
    }

    private bool _previewAfterCommit;

    private void ScheduleCommit(bool previewAfter = false)
    {
        if (_loading || !IsReady)
            return;
        _previewAfterCommit |= previewAfter;
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private void OnCommitTick(object? sender, EventArgs e)
    {
        _commitTimer.Stop();
        var preview = _previewAfterCommit;
        _previewAfterCommit = false;
        _ = CommitAsync(preview);
    }

    private async Task CommitAsync(bool previewAfter)
    {
        if (_client is null)
            return;

        var values = new Dictionary<string, object>
        {
            [StartupKey] = _playStartup,
            [DesktopKey] = _playDesktop,
            [WindowsKey] = _playWindows,
            [AlertsKey] = _playAlerts,
            [BellKey] = _playBell,
            [OutputKey] = _selectedOutput?.Value ?? DefaultOutput,
            [ThemeKey] = _selectedTheme?.Value ?? string.Empty,
        };

        try
        {
            var result = await _client.SetManyAsync(values).ConfigureAwait(true);
            Status = result.Advice ?? string.Empty;
            // An output is heard on the output, which only happens once the daemon has it.
            if (previewAfter)
                Preview(string.Empty, SampleSound);
        }
        catch (Exception e)
        {
            Status = e.Message.Split('\n')[0];
        }
    }

    private void OnChangedElsewhere(object? sender, SettingsChangedEventArgs e)
    {
        // The echo of our own write.
        if (e.Origin == _client?.UniqueName)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            _loading = true;
            Apply(e.Values);
            _loading = false;
        });
    }

    private void OnFileInvalid(object? sender, SettingsFileInvalidEventArgs e) =>
        Dispatcher.UIThread.Post(() => Status = Strings.FileInvalid(e.Path));

    /// <summary>Put whatever of the settings shown is present into the controls.</summary>
    private void Apply(IReadOnlyDictionary<string, object?> values)
    {
        if (values.TryGetValue(StartupKey, out var startup) && startup is bool playStartup)
            PlayStartup = playStartup;
        if (values.TryGetValue(DesktopKey, out var desktop) && desktop is bool playDesktop)
            PlayDesktop = playDesktop;
        if (values.TryGetValue(WindowsKey, out var windows) && windows is bool playWindows)
            PlayWindows = playWindows;
        if (values.TryGetValue(AlertsKey, out var alerts) && alerts is bool playAlerts)
            PlayAlerts = playAlerts;
        if (values.TryGetValue(BellKey, out var bell) && bell is bool playBell)
            PlayBell = playBell;
        if (values.TryGetValue(OutputKey, out var output) && output is string outputName)
            SelectOutput(outputName);
        if (values.TryGetValue(ThemeKey, out var theme) && theme is string themeId)
            SelectTheme(themeId);
    }

    /// <summary>
    /// Show <paramref name="name"/> as the output, adding it if PipeWire does not have it -- a
    /// headset that is unplugged right now is still the setting, and showing Default Output
    /// instead would invite the next commit to overwrite it.
    /// </summary>
    private void SelectOutput(string name)
    {
        var choice = Outputs.FirstOrDefault(output => output.Value == name);
        if (choice is null)
        {
            choice = new Choice(name, Strings.NotConnected(name));
            Outputs = [.. Outputs, choice];
        }
        _selectedOutput = choice;
        this.RaisePropertyChanged(nameof(SelectedOutput));
    }

    /// <summary>The same for a theme that is named but not installed.</summary>
    private void SelectTheme(string id)
    {
        if (string.IsNullOrEmpty(id))
            id = "wlrix";
        var choice = Themes.FirstOrDefault(theme => theme.Value == id);
        if (choice is null)
        {
            choice = new Choice(id, Strings.NotInstalled(id));
            Themes = [.. Themes, choice];
        }
        _selectedTheme = choice;
        this.RaisePropertyChanged(nameof(SelectedTheme));
    }

    private Snapshot Capture() => new(
        _playStartup, _playDesktop, _playWindows, _playAlerts, _playBell,
        _selectedOutput?.Value ?? DefaultOutput, _selectedTheme?.Value ?? "wlrix");

    private void Restore(Snapshot snapshot)
    {
        PlayStartup = snapshot.Startup;
        PlayDesktop = snapshot.Desktop;
        PlayWindows = snapshot.Windows;
        PlayAlerts = snapshot.Alerts;
        PlayBell = snapshot.Bell;
        SelectOutput(snapshot.Output);
        SelectTheme(snapshot.Theme);
    }

    /// <summary>The settings shown, for Reset.</summary>
    private sealed record Snapshot(
        bool Startup, bool Desktop, bool Windows, bool Alerts, bool Bell, string Output, string Theme)
    {
        public static Snapshot Defaults { get; } = new(false, true, false, true, true, DefaultOutput, "wlrix");
    }
}
