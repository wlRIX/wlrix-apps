using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Settings.Client;

namespace Wlrix.Settings.Windows.ViewModels;

/// <summary>
/// Backs the window settings window: IRIX's Window Settings panel, which is the keyboard focus
/// policy plus the three flags 4Dwm called Click Raise, Opaque Window Move and Opaque Window
/// Resize.
///
/// Built the same way as <c>Wlrix.Settings.Keyboard</c>, and for the same reasons. Everything
/// about the config file goes through <c>wlrix-settings-daemon</c>: this app does not know
/// where <c>compositor.toml</c> lives, how to edit TOML without destroying the user's comments,
/// or how to find the compositor to signal it. It does not know what the defaults are either,
/// nor what strings <c>[focus] policy</c> accepts -- those come from the daemon's schema, so
/// there is one place they are written down.
///
/// One commit is one <c>SetMany</c>, which is what makes <see cref="Reset"/> a single write of
/// four keys rather than four writes the compositor reloads against one at a time.
/// </summary>
public sealed class WindowSettingsViewModel : ViewModelBase, IDisposable
{
    private const string PolicyKey = "compositor.focus.policy";
    private const string RaiseKey = "compositor.focus.raise_on_click";
    private const string OpaqueMoveKey = "compositor.windows.opaque_move";
    private const string OpaqueResizeKey = "compositor.windows.opaque_resize";

    /// <summary>
    /// What IRIX called each focus policy, against the value the compositor writes.
    ///
    /// The daemon supplies labels of its own and they are perfectly good English ("Click to
    /// focus", "Focus follows pointer") — but this panel is a reproduction of a specific window,
    /// and 4Dwm's Window Settings said "Click to type" and "Point to type". Presentation is the
    /// app's, which is what <see cref="SettingDescription.ChoiceLabels"/> documents itself as
    /// being for: a fallback for a value the app has no words of its own for. A policy that
    /// turns up here unknown gets the daemon's label rather than nothing.
    /// </summary>
    private static readonly Dictionary<string, string> IrixLabels = new()
    {
        ["click"] = "Click to type",
        ["pointer"] = "Point to type",
    };

    /// <summary>
    /// The radio group to show before the daemon has been asked, and if it never answers.
    ///
    /// The values are the compositor's, so a session with no settings service still draws a
    /// panel that says what the two policies are instead of an empty box. Nothing is written
    /// from here: with no client there is no commit, and once the daemon does answer this list
    /// is replaced wholesale by the one it describes.
    /// </summary>
    private static readonly string[] FallbackPolicies = ["click", "pointer"];

    private readonly DispatcherTimer _commitTimer;

    private SettingsClient? _client;

    // The state captured once the settings have loaded, for Reset.
    private string _openPolicy = "click";
    private bool _openRaiseOnClick = true;
    private bool _openOpaqueMove = true;
    private bool _openOpaqueResize = true;

    // True while loading, or while applying a change that came from somewhere else, so those do
    // not each schedule a commit of their own.
    private bool _loading;

    private bool _ready;
    private string _status = string.Empty;
    private bool _raiseOnClick = true;
    private bool _opaqueMove = true;
    private bool _opaqueResize = true;

    /// <summary>
    /// Builds the window's model without touching the daemon, so the XAML designer and a
    /// session with no settings service both get something to draw.
    /// <see cref="InitializeAsync"/> is what fills it in.
    /// </summary>
    public WindowSettingsViewModel()
    {
        FocusPolicies = [.. FallbackPolicies.Select(value => new FocusChoice(value, Label(value, value)))];
        Watch(FocusPolicies);
        Select("click");

        // Checkboxes do not produce the burst a slider drag does, but Reset moves four settings
        // at once and a person ticking two boxes in a row produces two. The debounce turns
        // either into one SetMany, so the compositor reloads its config once.
        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _commitTimer.Tick += OnCommitTick;
    }

    /// <summary>The keyboard focus policies, as the daemon describes them.</summary>
    public IReadOnlyList<FocusChoice> FocusPolicies { get; private set; }

    /// <summary>Whether clicking a window's client area also brings it to the front.</summary>
    public bool RaiseOnClick
    {
        get => _raiseOnClick;
        set
        {
            this.RaiseAndSetIfChanged(ref _raiseOnClick, value);
            ScheduleCommit();
        }
    }

    /// <summary>
    /// Whether a window is dragged as itself, rather than as a wireframe applied on release.
    /// </summary>
    public bool OpaqueMove
    {
        get => _opaqueMove;
        set
        {
            this.RaiseAndSetIfChanged(ref _opaqueMove, value);
            ScheduleCommit();
        }
    }

    /// <summary>The same for resizing, which IRIX let you set separately and so does this.</summary>
    public bool OpaqueResize
    {
        get => _opaqueResize;
        set
        {
            this.RaiseAndSetIfChanged(ref _opaqueResize, value);
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

            _loading = true;
            AdoptPolicies(described.GetValueOrDefault(PolicyKey));
            Apply(values);
            _loading = false;

            _openPolicy = SelectedPolicy;
            _openRaiseOnClick = _raiseOnClick;
            _openOpaqueMove = _opaqueMove;
            _openOpaqueResize = _opaqueResize;

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
        Select(_openPolicy);
        RaiseOnClick = _openRaiseOnClick;
        OpaqueMove = _openOpaqueMove;
        OpaqueResize = _openOpaqueResize;
        _loading = false;
        _ = CommitAsync();
    }

    public void Dispose()
    {
        // Flush a change still waiting out its debounce, so closing right after a click does not
        // drop it. Waited on rather than fired and forgotten: the process is about to exit, and
        // an unawaited write would be lost with it.
        var pending = _commitTimer.IsEnabled;
        _commitTimer.Stop();
        _commitTimer.Tick -= OnCommitTick;
        if (pending)
            CommitAsync().GetAwaiter().GetResult();

        Unwatch(FocusPolicies);

        if (_client is not null)
        {
            _client.Changed -= OnChangedElsewhere;
            _client.FileInvalid -= OnFileInvalid;
            _client.Dispose();
            _client = null;
        }
    }

    /// <summary>The value of the picked radio button, for the commit.</summary>
    private string SelectedPolicy =>
        FocusPolicies.FirstOrDefault(choice => choice.IsSelected)?.Value ?? _openPolicy;

    private void OnCommitTick(object? sender, EventArgs e)
    {
        _commitTimer.Stop();
        _ = CommitAsync();
    }

    // Coalesce a burst of changes into one write shortly after it settles.
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
            [PolicyKey] = SelectedPolicy,
            [RaiseKey] = _raiseOnClick,
            [OpaqueMoveKey] = _opaqueMove,
            [OpaqueResizeKey] = _opaqueResize,
        };

        try
        {
            var result = await _client.SetManyAsync(values).ConfigureAwait(true);
            Status = result.Advice ?? string.Empty;
        }
        catch (Exception e)
        {
            // The daemon refusing a value, or the compositor refusing the file, both land here
            // with a message that names the key. An older daemon that does not know
            // `raise_on_click` is the case worth expecting: it says so by name rather than the
            // checkbox silently doing nothing.
            Status = e.Message.Split('\n')[0];
        }
    }

    /// <summary>
    /// Somebody else changed a setting we are showing -- most likely the person editing
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
    /// Replace the radio group with the choices the daemon declares.
    ///
    /// A daemon that does not describe the policy at all -- or describes it as something other
    /// than an enum -- leaves the fallback group in place, which is the same two options the
    /// compositor has always had. Silently showing no radio buttons would be worse than showing
    /// a pair that turn out not to be writable.
    /// </summary>
    private void AdoptPolicies(SettingDescription? described)
    {
        if (described is null || described.Choices.Count == 0)
            return;

        Unwatch(FocusPolicies);
        FocusPolicies =
        [
            .. described.Choices.Select((value, index) => new FocusChoice(
                value,
                Label(value, index < described.ChoiceLabels.Count ? described.ChoiceLabels[index] : value))),
        ];
        Watch(FocusPolicies);
        this.RaisePropertyChanged(nameof(FocusPolicies));

        Select(described.Default as string ?? FocusPolicies[0].Value);
    }

    /// <summary>
    /// Put whatever of these four settings is present into the controls.
    ///
    /// Used for both the initial load and a change from elsewhere, so a hand-edit that touches
    /// only the focus policy leaves the checkboxes where they are.
    /// </summary>
    private void Apply(IReadOnlyDictionary<string, object?> values)
    {
        if (values.TryGetValue(PolicyKey, out var policy) && policy is string name)
            Select(name);

        if (values.TryGetValue(RaiseKey, out var raise) && raise is bool raiseOnClick)
            RaiseOnClick = raiseOnClick;

        if (values.TryGetValue(OpaqueMoveKey, out var move) && move is bool opaqueMove)
            OpaqueMove = opaqueMove;

        if (values.TryGetValue(OpaqueResizeKey, out var resize) && resize is bool opaqueResize)
            OpaqueResize = opaqueResize;
    }

    /// <summary>
    /// Pick the radio button for a policy, leaving the group alone if the value is one this
    /// daemon has not declared.
    ///
    /// Leaving it alone rather than clearing the group is deliberate: an unrecognized value is a
    /// config file this build is older than, and an empty radio group would invite the person to
    /// pick something and overwrite it.
    /// </summary>
    private void Select(string value)
    {
        var wanted = FocusPolicies.FirstOrDefault(choice => choice.Value == value);
        if (wanted is null)
            return;

        foreach (var choice in FocusPolicies)
            choice.IsSelected = ReferenceEquals(choice, wanted);
    }

    /// <summary>IRIX's name for a policy, or the daemon's if this build has none.</summary>
    private static string Label(string value, string fallback) =>
        IrixLabels.GetValueOrDefault(value) ?? fallback;

    private void Watch(IReadOnlyList<FocusChoice> choices)
    {
        foreach (var choice in choices)
            choice.PropertyChanged += OnChoiceChanged;
    }

    private void Unwatch(IReadOnlyList<FocusChoice> choices)
    {
        foreach (var choice in choices)
            choice.PropertyChanged -= OnChoiceChanged;
    }

    /// <summary>
    /// A radio button moved.
    ///
    /// Only the one turning *on* is acted on. Clicking B makes Avalonia clear A and set B, two
    /// events for one decision, and committing on the clear would write the policy of whichever
    /// order they happened to arrive in.
    /// </summary>
    private void OnChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FocusChoice.IsSelected) && sender is FocusChoice { IsSelected: true })
            ScheduleCommit();
    }
}
