// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Settings.Client;
using Wlrix.Settings.Mouse.Localization;
using Wlrix.Settings.Mouse.Services;

namespace Wlrix.Settings.Mouse.ViewModels;

/// <summary>
/// Backs the Mouse Settings window, after IRIX's panel of the same name.
///
/// Everything it shows lives in <c>compositor.toml</c> and goes through
/// <c>wlrix-settings-daemon</c>, as the keyboard panel's settings do: <c>[mouse]</c> for the
/// acceleration, the button mapping and the double-click time, and <c>[cursor]</c> for the theme,
/// which is wlRIX's addition. A change is committed shortly after the last adjustment, so a
/// slider drag writes once; <see cref="Reset"/> puts back what was in force when the window
/// opened.
///
/// The Touchpad tab is wlRIX's own -- IRIX had no touchpads -- and writes <c>[touchpad]</c> the
/// same way. Handedness and the double-click time stay on the Mouse tab and apply to both: they
/// are the person's, not the device's.
///
/// IRIX's acceleration panel also had a <em>threshold</em> slider. libinput has no threshold --
/// its acceleration is a curve over pointer speed rather than a step at one -- so it is not here
/// rather than being a slider that does nothing.
/// </summary>
public sealed class MouseSettingsViewModel : ViewModelBase, IDisposable
{
    private const string AccelerationKey = "compositor.mouse.acceleration";
    private const string LeftHandedKey = "compositor.mouse.left_handed";
    private const string DoubleClickKey = "compositor.mouse.double_click_ms";
    private const string ThemeKey = "compositor.cursor.theme";
    private const string TouchpadAccelerationKey = "compositor.touchpad.acceleration";
    private const string TapKey = "compositor.touchpad.tap";
    private const string TapDragKey = "compositor.touchpad.tap_drag";
    private const string DragLockKey = "compositor.touchpad.drag_lock";
    private const string NaturalScrollKey = "compositor.touchpad.natural_scroll";
    private const string DisableWhileTypingKey = "compositor.touchpad.disable_while_typing";
    private const string DisableWithMouseKey = "compositor.touchpad.disable_with_mouse";
    private const string ScrollKey = "compositor.touchpad.scroll";
    private const string SecondaryClickKey = "compositor.touchpad.secondary_click";

    /// <summary>The scroll method a touchpad uses when the file names none: two fingers, on any
    /// pad that can sense them.</summary>
    private const string DefaultScroll = "two-finger";

    /// <summary><c>secondary_click</c>'s values, by the diamond after "the pad's own".</summary>
    private static readonly string[] SecondaryClicks = ["fingers", "corner"];

    /// <summary>The size the cursor previews are read at: <c>sgi</c>'s only one.</summary>
    private const int PreviewSize = 32;

    /// <summary>How long the Test button shows its verdict before asking to be clicked again.</summary>
    private static readonly TimeSpan VerdictTime = TimeSpan.FromSeconds(1.5);

    /// <summary>The shapes the preview shows, each with the legacy names themes still use.</summary>
    private static readonly string[][] PreviewShapes =
    [
        ["default", "left_ptr"],
        ["pointer", "hand2", "hand1"],
        ["text", "xterm"],
        ["wait", "watch"],
        ["crosshair", "cross"],
    ];

    private readonly DispatcherTimer _commitTimer;
    private readonly DispatcherTimer _singleTimer;
    private readonly DispatcherTimer _verdictTimer;
    private readonly ClickTest _clickTest = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly IReadOnlyList<string> _searchPath = CursorThemes.SearchPath();

    private SettingsClient? _client;

    // The double-click slider's range, from the daemon's schema once it has answered.
    private int _clickMinimum = 100;
    private int _clickMaximum = 1500;

    // What was in force when the window opened, for Reset. The theme is kept as the *file's*
    // value: null means the file does not name one, and Reset has to remove the key again
    // rather than write down whatever the environment happened to say.
    private double _openAcceleration;
    private bool _openLeftHanded;
    private int _openDoubleClick = 400;
    private string? _openThemeInFile;
    private TouchpadValues _openTouchpad;

    // The theme the file names right now, or null. Written only when the selection differs from
    // it, so moving a slider does not pin a theme the user never chose into their file.
    private string? _themeInFile;

    // True while loading or applying a change from elsewhere, so those do not schedule a commit.
    private bool _loading;

    private bool _ready;
    private string _status = string.Empty;
    private double _acceleration;
    private bool _leftHanded;
    private int _doubleClickMs = 400;
    private bool _showAccelerationSlider;
    private bool _showClickSpeedSlider;
    private string _testLabel = Strings.ClickHere;
    private IReadOnlyList<CursorTheme> _themes;
    private CursorTheme? _selectedTheme;
    private IReadOnlyList<Bitmap> _preview = [];

    // The Touchpad tab. The two choices that may be absent from the file are kept twice, as with
    // the theme: what the file says, and what it should say once the next commit is done, so a
    // choice of "the pad's own" removes the key rather than writing one down.
    private TouchpadValues _touchpad = TouchpadValues.Defaults;
    private string? _scrollInFile;
    private string? _secondaryInFile;
    private bool _showTouchpadAccelerationSlider;
    private int _selectedTab;

    /// <summary>
    /// Builds the window's model without touching the daemon, so the XAML designer and a session
    /// with no settings service both get something to draw. <see cref="InitializeAsync"/> is what
    /// fills it in.
    /// </summary>
    public MouseSettingsViewModel()
    {
        _themes = CursorThemes.List(_searchPath);

        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _commitTimer.Tick += OnCommitTick;
        _singleTimer = new DispatcherTimer();
        _singleTimer.Tick += OnSingleTick;
        _verdictTimer = new DispatcherTimer { Interval = VerdictTime };
        _verdictTimer.Tick += OnVerdictTick;
    }

    /// <summary>libinput's pointer speed, -1 to 1.</summary>
    public double Acceleration
    {
        get => _acceleration;
        set
        {
            this.RaiseAndSetIfChanged(ref _acceleration, value);
            this.RaisePropertyChanged(nameof(AccelerationPreset));
            this.RaisePropertyChanged(nameof(AccelerationText));
            ScheduleCommit();
        }
    }

    /// <summary>Which acceleration diamond is lit, or -1 for a value between them.</summary>
    public int AccelerationPreset
    {
        get => Presets.IndexOf(Presets.Acceleration, _acceleration);
        set
        {
            if (value >= 0 && value < Presets.Acceleration.Count)
                Acceleration = Presets.Acceleration[value];
        }
    }

    /// <summary>The acceleration, for the slider's tooltip.</summary>
    public string AccelerationText => _acceleration.ToString("+0.00;-0.00;0.00");

    /// <summary>Whether the acceleration slider is showing: the first Finer Control.</summary>
    public bool ShowAccelerationSlider
    {
        get => _showAccelerationSlider;
        set => this.RaiseAndSetIfChanged(ref _showAccelerationSlider, value);
    }

    /// <summary>The primary and secondary buttons swapped.</summary>
    public bool LeftHanded
    {
        get => _leftHanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _leftHanded, value);
            this.RaisePropertyChanged(nameof(RightHanded));
            ScheduleCommit();
        }
    }

    /// <summary>The other diamond of the pair, so each can be bound one way round.</summary>
    public bool RightHanded
    {
        get => !_leftHanded;
        set => LeftHanded = !value;
    }

    /// <summary>The longest gap, in milliseconds, between the clicks of a double click.</summary>
    public int DoubleClickMs
    {
        get => _doubleClickMs;
        set
        {
            value = Math.Clamp(value, _clickMinimum, _clickMaximum);
            this.RaiseAndSetIfChanged(ref _doubleClickMs, value);
            this.RaisePropertyChanged(nameof(ClickSpeedPreset));
            this.RaisePropertyChanged(nameof(ClickSpeedPosition));
            this.RaisePropertyChanged(nameof(ClickSpeedText));
            ScheduleCommit();
        }
    }

    /// <summary>Which click-speed diamond is lit, or -1 for a value between them.</summary>
    public int ClickSpeedPreset
    {
        get => Presets.IndexOf(Presets.DoubleClick, _doubleClickMs);
        set
        {
            if (value >= 0 && value < Presets.DoubleClick.Count)
                DoubleClickMs = Presets.DoubleClick[value];
        }
    }

    /// <summary>
    /// The click-speed slider's position. The slider reads Slow to Fast like the diamonds above
    /// it, and a fast clicker wants a <em>short</em> time, so its position is the time turned
    /// round within the range.
    /// </summary>
    public double ClickSpeedPosition
    {
        get => ClickSpeedSlider.PositionOf(_doubleClickMs, _clickMinimum, _clickMaximum);
        set => DoubleClickMs = ClickSpeedSlider.TimeAt(value, _clickMinimum, _clickMaximum);
    }

    /// <summary>The slider's ends, which are the daemon's range for the setting.</summary>
    public double ClickSpeedMinimum => _clickMinimum;

    /// <inheritdoc cref="ClickSpeedMinimum"/>
    public double ClickSpeedMaximum => _clickMaximum;

    /// <summary>The double-click time, for the slider's tooltip.</summary>
    public string ClickSpeedText => Strings.Milliseconds(_doubleClickMs);

    /// <summary>Whether the click-speed slider is showing: the second Finer Control.</summary>
    public bool ShowClickSpeedSlider
    {
        get => _showClickSpeedSlider;
        set => this.RaiseAndSetIfChanged(ref _showClickSpeedSlider, value);
    }

    /// <summary>"Click Here!", or what the last click was.</summary>
    public string TestLabel
    {
        get => _testLabel;
        private set => this.RaiseAndSetIfChanged(ref _testLabel, value);
    }

    /// <summary>The installed cursor themes, plus the configured one if it is not installed.</summary>
    public IReadOnlyList<CursorTheme> Themes
    {
        get => _themes;
        private set => this.RaiseAndSetIfChanged(ref _themes, value);
    }

    public CursorTheme? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTheme, value);
            Preview = LoadPreview(value?.Name);
            ScheduleCommit();
        }
    }

    /// <summary>A few of the selected theme's pointers, at the size the desktop draws them.</summary>
    public IReadOnlyList<Bitmap> Preview
    {
        get => _preview;
        private set
        {
            var old = _preview;
            this.RaiseAndSetIfChanged(ref _preview, value);
            foreach (var bitmap in old)
                bitmap.Dispose();
        }
    }

    /// <summary>Which tab of the top panel is showing: 0 Mouse, 1 Touchpad.</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set => this.RaiseAndSetIfChanged(ref _selectedTab, value);
    }

    /// <summary>Whether this computer has a touchpad. The tab is there either way.</summary>
    public bool HasTouchpad { get; } = Touchpads.Present();

    /// <summary>libinput's pointer speed for touchpads, -1 to 1.</summary>
    public double TouchpadAcceleration
    {
        get => _touchpad.Acceleration;
        set => SetTouchpad(_touchpad with { Acceleration = value });
    }

    /// <summary>Which touchpad acceleration diamond is lit, or -1 for a value between them.</summary>
    public int TouchpadAccelerationPreset
    {
        get => Presets.IndexOf(Presets.Acceleration, _touchpad.Acceleration);
        set
        {
            if (value >= 0 && value < Presets.Acceleration.Count)
                TouchpadAcceleration = Presets.Acceleration[value];
        }
    }

    /// <summary>The touchpad acceleration, for the slider's tooltip.</summary>
    public string TouchpadAccelerationText => _touchpad.Acceleration.ToString("+0.00;-0.00;0.00");

    /// <summary>Whether the touchpad acceleration slider is showing.</summary>
    public bool ShowTouchpadAccelerationSlider
    {
        get => _showTouchpadAccelerationSlider;
        set => this.RaiseAndSetIfChanged(ref _showTouchpadAccelerationSlider, value);
    }

    public bool Tap
    {
        get => _touchpad.Tap;
        set => SetTouchpad(_touchpad with { Tap = value });
    }

    public bool TapDrag
    {
        get => _touchpad.TapDrag;
        set => SetTouchpad(_touchpad with { TapDrag = value });
    }

    public bool DragLock
    {
        get => _touchpad.DragLock;
        set => SetTouchpad(_touchpad with { DragLock = value });
    }

    public bool NaturalScroll
    {
        get => _touchpad.NaturalScroll;
        set => SetTouchpad(_touchpad with { NaturalScroll = value });
    }

    public bool DisableWhileTyping
    {
        get => _touchpad.DisableWhileTyping;
        set => SetTouchpad(_touchpad with { DisableWhileTyping = value });
    }

    public bool DisableWithMouse
    {
        get => _touchpad.DisableWithMouse;
        set => SetTouchpad(_touchpad with { DisableWithMouse = value });
    }

    /// <summary>Two-finger scrolling, the first of the pair of scrolling diamonds.</summary>
    public bool TwoFingerScroll
    {
        get => (_touchpad.Scroll ?? DefaultScroll) == "two-finger";
        set
        {
            if (value)
                SetTouchpad(_touchpad with { Scroll = "two-finger" });
        }
    }

    /// <summary>Scrolling along the right edge, the other of the pair.</summary>
    public bool EdgeScroll
    {
        get => _touchpad.Scroll == "edge";
        set
        {
            if (value)
                SetTouchpad(_touchpad with { Scroll = "edge" });
        }
    }

    /// <summary>
    /// Which secondary-click diamond is lit: 0 the pad's own, 1 a two-finger press, 2 the
    /// bottom-right corner.
    /// </summary>
    public int SecondaryClickChoice
    {
        get => _touchpad.SecondaryClick is { } named ? Array.IndexOf(SecondaryClicks, named) + 1 : 0;
        set
        {
            if (value >= 0 && value <= SecondaryClicks.Length)
                SetTouchpad(_touchpad with { SecondaryClick = value == 0 ? null : SecondaryClicks[value - 1] });
        }
    }

    /// <summary>Whether the settings have loaded and the controls mean anything yet.</summary>
    public bool IsReady
    {
        get => _ready;
        private set => this.RaiseAndSetIfChanged(ref _ready, value);
    }

    /// <summary>What happened to the last change, when it is worth saying. Empty when it applied.</summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>
    /// Ask the daemon what these settings hold and what their ranges are, then listen for changes
    /// from anywhere else. Failing leaves the window drawn but not ready, with the reason in
    /// <see cref="Status"/>.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            _client = await SettingsClient.ConnectAsync().ConfigureAwait(true);
            var described = (await _client.DescribeAsync("compositor").ConfigureAwait(true))
                .ToDictionary(setting => setting.Key);
            var values = await _client.GetAllAsync("compositor").ConfigureAwait(true);

            if (described.GetValueOrDefault(DoubleClickKey) is { } click)
            {
                _clickMinimum = (int)(click.Min ?? _clickMinimum);
                _clickMaximum = (int)(click.Max ?? _clickMaximum);
                this.RaisePropertyChanged(nameof(ClickSpeedMinimum));
                this.RaisePropertyChanged(nameof(ClickSpeedMaximum));
            }

            _loading = true;
            Apply(values);
            _loading = false;

            _openAcceleration = _acceleration;
            _openLeftHanded = _leftHanded;
            _openDoubleClick = _doubleClickMs;
            _openThemeInFile = _themeInFile;
            _openTouchpad = _touchpad;

            await _client.WatchAsync().ConfigureAwait(true);
            _client.Changed += OnChangedElsewhere;
            _client.FileInvalid += OnFileInvalid;

            IsReady = true;
        }
        catch (Exception e)
        {
            _loading = false;
            // Bus activation means "not on the bus" is a broken install rather than an idle
            // session, so say so rather than silently doing nothing.
            Status = Strings.NoSettingsService(e.Message);
        }
    }

    /// <summary>The Test button was clicked.</summary>
    public void TestClicked()
    {
        var window = TimeSpan.FromMilliseconds(_doubleClickMs);
        _singleTimer.Stop();
        _verdictTimer.Stop();

        if (_clickTest.Press(_clock.Elapsed, window) is ClickKind.Double)
        {
            ShowVerdict(ClickKind.Double);
            return;
        }

        // A first click: it is a single only once the double-click time has passed without a
        // second. The button says nothing until then, as a desktop acting on a single click
        // would also have to wait.
        TestLabel = Strings.ClickHere;
        _singleTimer.Interval = window;
        _singleTimer.Start();
    }

    /// <summary>Restore the settings to how they were when the window opened, and apply them.</summary>
    public void Reset()
    {
        if (!IsReady)
            return;

        _loading = true;
        Acceleration = _openAcceleration;
        LeftHanded = _openLeftHanded;
        DoubleClickMs = _openDoubleClick;
        SelectedTheme = FindTheme(_openThemeInFile ?? EnvironmentTheme());
        SetTouchpad(_openTouchpad);
        _loading = false;
        _ = CommitAsync(resetTheme: _openThemeInFile is null && _themeInFile is not null);
    }

    public void Dispose()
    {
        // Flush a change still waiting out its debounce, so closing right after an adjustment does
        // not drop it. Waited on: the process is about to exit, and an unawaited write is lost.
        var pending = _commitTimer.IsEnabled;
        _commitTimer.Stop();
        _commitTimer.Tick -= OnCommitTick;
        _singleTimer.Stop();
        _singleTimer.Tick -= OnSingleTick;
        _verdictTimer.Stop();
        _verdictTimer.Tick -= OnVerdictTick;
        if (pending)
            CommitAsync().GetAwaiter().GetResult();

        if (_client is not null)
        {
            _client.Changed -= OnChangedElsewhere;
            _client.FileInvalid -= OnFileInvalid;
            _client.Dispose();
            _client = null;
        }

        Preview = [];
    }

    private void OnSingleTick(object? sender, EventArgs e)
    {
        _singleTimer.Stop();
        if (_clickTest.Expire() is { } kind)
            ShowVerdict(kind);
    }

    private void OnVerdictTick(object? sender, EventArgs e)
    {
        _verdictTimer.Stop();
        TestLabel = Strings.ClickHere;
    }

    private void ShowVerdict(ClickKind kind)
    {
        TestLabel = kind == ClickKind.Double ? Strings.DoubleClick : Strings.SingleClick;
        _verdictTimer.Start();
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

    private async Task CommitAsync(bool resetTheme = false)
    {
        if (_client is null)
            return;

        var values = new Dictionary<string, object>
        {
            [AccelerationKey] = Math.Round(_acceleration, 2),
            [LeftHandedKey] = _leftHanded,
            [DoubleClickKey] = (long)_doubleClickMs,
            [TouchpadAccelerationKey] = Math.Round(_touchpad.Acceleration, 2),
            [TapKey] = _touchpad.Tap,
            [TapDragKey] = _touchpad.TapDrag,
            [DragLockKey] = _touchpad.DragLock,
            [NaturalScrollKey] = _touchpad.NaturalScroll,
            [DisableWhileTypingKey] = _touchpad.DisableWhileTyping,
            [DisableWithMouseKey] = _touchpad.DisableWithMouse,
        };
        if (!resetTheme && _selectedTheme is { } theme && theme.Name != _themeInFile)
            values[ThemeKey] = theme.Name;

        // The two that may be absent: written when they changed to a value, removed when they
        // changed to "the pad's own".
        var removals = new List<string>();
        if (resetTheme)
            removals.Add(ThemeKey);
        foreach (var (key, wanted, inFile) in new[]
                 {
                     (ScrollKey, _touchpad.Scroll, _scrollInFile),
                     (SecondaryClickKey, _touchpad.SecondaryClick, _secondaryInFile),
                 })
        {
            if (wanted == inFile)
                continue;
            if (wanted is null)
                removals.Add(key);
            else
                values[key] = wanted;
        }

        try
        {
            var result = await _client.SetManyAsync(values).ConfigureAwait(true);
            if (values.TryGetValue(ThemeKey, out var written))
                _themeInFile = (string)written;
            if (values.TryGetValue(ScrollKey, out var scroll))
                _scrollInFile = (string)scroll;
            if (values.TryGetValue(SecondaryClickKey, out var secondary))
                _secondaryInFile = (string)secondary;
            foreach (var key in removals)
            {
                await _client.ResetAsync(key).ConfigureAwait(true);
                switch (key)
                {
                    case ThemeKey: _themeInFile = null; break;
                    case ScrollKey: _scrollInFile = null; break;
                    case SecondaryClickKey: _secondaryInFile = null; break;
                }
            }
            Status = result.Advice ?? string.Empty;
        }
        catch (Exception e)
        {
            // The daemon refusing a value, or the compositor refusing the file, both land here with
            // a message that names the key.
            Status = e.Message.Split('\n')[0];
        }
    }

    /// <summary>
    /// Somebody else changed a setting this window shows -- a hand-edit of
    /// <c>compositor.toml</c>, or another panel.
    /// </summary>
    private void OnChangedElsewhere(object? sender, SettingsChangedEventArgs e)
    {
        // The echo of our own write, which the daemon sends to everyone including us.
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
        Dispatcher.UIThread.Post(() => Status = Strings.FileInvalid(e.Path));

    /// <summary>
    /// Put whatever of these settings is present into the controls. Used for both the initial
    /// load and a change from elsewhere, so a change to one leaves the others where they are.
    /// </summary>
    private void Apply(IReadOnlyDictionary<string, object?> values)
    {
        if (values.TryGetValue(AccelerationKey, out var acceleration))
            Acceleration = Math.Clamp(AsNumber(acceleration, 0.0), -1.0, 1.0);

        if (values.TryGetValue(LeftHandedKey, out var left) && left is bool leftHanded)
            LeftHanded = leftHanded;

        if (values.TryGetValue(DoubleClickKey, out var click) && click is not null)
            DoubleClickMs = (int)AsNumber(click, _doubleClickMs);

        var touchpad = _touchpad;
        if (values.TryGetValue(TouchpadAccelerationKey, out var touchAcceleration))
            touchpad = touchpad with { Acceleration = Math.Clamp(AsNumber(touchAcceleration, 0.0), -1.0, 1.0) };
        touchpad = touchpad with
        {
            Tap = AsBool(values, TapKey, touchpad.Tap),
            TapDrag = AsBool(values, TapDragKey, touchpad.TapDrag),
            DragLock = AsBool(values, DragLockKey, touchpad.DragLock),
            NaturalScroll = AsBool(values, NaturalScrollKey, touchpad.NaturalScroll),
            DisableWhileTyping = AsBool(values, DisableWhileTypingKey, touchpad.DisableWhileTyping),
            DisableWithMouse = AsBool(values, DisableWithMouseKey, touchpad.DisableWithMouse),
        };
        if (values.ContainsKey(ScrollKey))
        {
            _scrollInFile = values[ScrollKey] as string is { Length: > 0 } scroll ? scroll : null;
            touchpad = touchpad with { Scroll = _scrollInFile };
        }
        if (values.ContainsKey(SecondaryClickKey))
        {
            _secondaryInFile = values[SecondaryClickKey] as string is { Length: > 0 } secondary ? secondary : null;
            touchpad = touchpad with { SecondaryClick = _secondaryInFile };
        }
        SetTouchpad(touchpad);

        if (values.ContainsKey(ThemeKey))
        {
            _themeInFile = values[ThemeKey] as string is { Length: > 0 } named ? named : null;
            SelectedTheme = FindTheme(_themeInFile ?? EnvironmentTheme());
        }
    }

    /// <summary>Change the Touchpad tab's values, tell every control bound to them, and commit.</summary>
    private void SetTouchpad(TouchpadValues values)
    {
        if (values == _touchpad)
            return;
        _touchpad = values;
        foreach (var name in TouchpadProperties)
            this.RaisePropertyChanged(name);
        ScheduleCommit();
    }

    private static readonly string[] TouchpadProperties =
    [
        nameof(TouchpadAcceleration), nameof(TouchpadAccelerationPreset), nameof(TouchpadAccelerationText),
        nameof(Tap), nameof(TapDrag), nameof(DragLock), nameof(NaturalScroll),
        nameof(DisableWhileTyping), nameof(DisableWithMouse),
        nameof(TwoFingerScroll), nameof(EdgeScroll), nameof(SecondaryClickChoice),
    ];

    private static bool AsBool(IReadOnlyDictionary<string, object?> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value) && value is bool flag ? flag : fallback;

    /// <summary>
    /// What the desktop is drawing when the file names no theme: what the session put in this
    /// process's environment, as the compositor would read its own.
    /// </summary>
    private static string EnvironmentTheme() =>
        Environment.GetEnvironmentVariable("XCURSOR_THEME") is { Length: > 0 } theme ? theme : "default";

    /// <summary>
    /// The theme called <paramref name="name"/>, added to the list if it is not installed so
    /// opening the window never silently replaces it with something else.
    /// </summary>
    private CursorTheme FindTheme(string name)
    {
        if (Themes.FirstOrDefault(theme => theme.Name == name) is { } found)
            return found;
        var missing = new CursorTheme(name, Strings.NotInstalled(name));
        Themes = [missing, .. Themes];
        return missing;
    }

    private IReadOnlyList<Bitmap> LoadPreview(string? theme)
    {
        if (theme is null)
            return [];

        var bitmaps = new List<Bitmap>();
        foreach (var shape in PreviewShapes)
        {
            try
            {
                if (CursorThemes.Find(_searchPath, theme, shape) is not { } path)
                    continue;
                if (CursorThemes.Read(File.ReadAllBytes(path), PreviewSize) is { } image)
                    bitmaps.Add(ToBitmap(image));
            }
            catch (IOException)
            {
                // A shape that cannot be read is left out of the row, not reported: the row is a
                // picture of the theme, and four pointers instead of five still are one.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return bitmaps;
    }

    private static Bitmap ToBitmap(CursorImage image)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var stride = image.Width * 4;
        for (var row = 0; row < image.Height; row++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                image.Bgra, row * stride, buffer.Address + row * buffer.RowBytes, stride);
        }
        return bitmap;
    }

    private static double AsNumber(object? value, double fallback) => value switch
    {
        long number => number,
        int number => number,
        double number => number,
        _ => fallback,
    };
}

/// <summary>The click-speed slider's arithmetic: Slow on the left, which is the longest time.</summary>
internal static class ClickSpeedSlider
{
    public static double PositionOf(int milliseconds, int minimum, int maximum) =>
        minimum + maximum - Math.Clamp(milliseconds, minimum, maximum);

    public static int TimeAt(double position, int minimum, int maximum) =>
        Math.Clamp((int)Math.Round(minimum + maximum - position), minimum, maximum);
}

/// <summary>
/// Everything on the Touchpad tab, as one value so Reset can put it all back at once. The
/// defaults are the compositor's: tapping on, the rest libinput's.
/// </summary>
internal readonly record struct TouchpadValues(
    double Acceleration,
    bool Tap,
    bool TapDrag,
    bool DragLock,
    bool NaturalScroll,
    bool DisableWhileTyping,
    bool DisableWithMouse,
    string? Scroll,
    string? SecondaryClick)
{
    public static TouchpadValues Defaults { get; } =
        new(0.0, Tap: true, TapDrag: true, DragLock: false, NaturalScroll: false,
            DisableWhileTyping: true, DisableWithMouse: false, Scroll: null, SecondaryClick: null);
}
