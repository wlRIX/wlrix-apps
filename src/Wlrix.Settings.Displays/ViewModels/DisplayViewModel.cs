using ReactiveUI;
using Wlrix.Settings.Displays.Layout;
using Wlrix.Settings.Displays.Localization;
using Wlrix.Settings.Displays.Models;

namespace Wlrix.Settings.Displays.ViewModels;

/// <summary>What an edit to a display did to the layout.</summary>
public enum EditKind
{
    /// <summary>Nothing: the display is where it was and the same size.</summary>
    None,

    /// <summary>The display changed size.</summary>
    Resized,

    /// <summary>The display was switched on or off.</summary>
    Toggled,
}

/// <summary>A size a display offers, for the Resolution list.</summary>
public sealed record ResolutionChoice(int Width, int Height, string Label);

/// <summary>A refresh rate the chosen size offers, for the Refresh rate list.</summary>
public sealed record RefreshChoice(OutputMode Mode, string Label);

/// <summary>
/// One display, as the panel has it staged: starts as the compositor describes it, takes every
/// edit, and turns back into an <see cref="OutputChange"/> when applied. Nothing here reaches the
/// compositor until then.
/// </summary>
public sealed class DisplayViewModel : ViewModelBase
{
    public const double MinScalePercent = 50;
    public const double MaxScalePercent = 300;
    public const int MinSdrWhite = 80;
    public const int MaxSdrWhite = 500;

    // How far a staged scale may be from the reported one and still count as unchanged. The
    // protocol carries scale as 24.8 fixed point, so 1.15 comes back as 1.1484.
    private const double ScaleTolerance = 1.0 / 256;

    private bool _enabled;
    private ResolutionChoice? _resolution;
    private IReadOnlyList<RefreshChoice> _refreshRates = [];
    private RefreshChoice? _refresh;
    private double _scalePercent;
    private OutputTransform _transform;
    private bool _adaptiveSync;
    private bool _hdr;
    private int _sdrWhite;
    private int _x, _y;
    private bool _canDisable = true;

    public DisplayViewModel(OutputHead head)
    {
        Head = head;
        Resolutions = BuildResolutions(head.Modes);
        Load(head);
    }

    /// <summary>What the compositor last said about this display.</summary>
    public OutputHead Head { get; private set; }

    public string Name => Head.Name;

    /// <summary>"NEC Corporation EA274WMi 3X100335NB", for the Display list.</summary>
    public string Label => string.Join(' ', new[] { Make, Head.Model, Head.Serial }
        .Where(part => !string.IsNullOrWhiteSpace(part) && part != "Unknown"));

    /// <summary>The lines drawn on this display's tile in the arrangement.</summary>
    public IReadOnlyList<string> TileLines
    {
        get
        {
            var lines = new List<string>();
            foreach (var part in new[] { Make, Head.Model, Head.Serial })
            {
                if (!string.IsNullOrWhiteSpace(part) && part != "Unknown")
                    lines.Add(part);
            }
            if (lines.Count == 0)
                lines.Add(Head.Name);
            if (Mode is { } mode)
                lines.Add($"({mode.Width}x{mode.Height})");
            return lines;
        }
    }

    // A monitor whose EDID could not be read is "Unknown"; its connector says more.
    private string Make => Head.Make is "" or "Unknown" ? Head.Name : Head.Make;

    public IReadOnlyList<ResolutionChoice> Resolutions { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled || (!value && !CanDisable))
                return;
            this.RaiseAndSetIfChanged(ref _enabled, value);
            this.RaisePropertyChanged(nameof(CanToggleEnabled));
            OnEdited(EditKind.Toggled);
        }
    }

    /// <summary>
    /// False for the last display switched on: turning it off would leave nothing to see the
    /// panel on, and the compositor refuses it anyway.
    /// </summary>
    public bool CanDisable
    {
        get => _canDisable;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _canDisable, value);
            this.RaisePropertyChanged(nameof(CanToggleEnabled));
        }
    }

    /// <summary>Whether the Enabled box can be clicked: always to turn on, not always to turn off.</summary>
    public bool CanToggleEnabled => !_enabled || _canDisable;

    public ResolutionChoice? Resolution
    {
        get => _resolution;
        set
        {
            if (value is null || value == _resolution)
                return;
            this.RaiseAndSetIfChanged(ref _resolution, value);
            // A new size has its own refresh rates. Keep the rate if the new size offers it,
            // otherwise take the fastest.
            var previous = _refresh?.Mode.RefreshMilliHz;
            RefreshRates = BuildRefreshRates(value);
            Refresh = RefreshRates.FirstOrDefault(r => r.Mode.RefreshMilliHz == previous)
                      ?? RefreshRates.FirstOrDefault();
            OnEdited(EditKind.Resized);
        }
    }

    public IReadOnlyList<RefreshChoice> RefreshRates
    {
        get => _refreshRates;
        private set => this.RaiseAndSetIfChanged(ref _refreshRates, value);
    }

    public RefreshChoice? Refresh
    {
        get => _refresh;
        set
        {
            if (value is null || value == _refresh)
                return;
            this.RaiseAndSetIfChanged(ref _refresh, value);
            this.RaisePropertyChanged(nameof(TileLines));
            OnEdited(EditKind.None);
        }
    }

    /// <summary>The mode the choices above add up to.</summary>
    public OutputMode? Mode => _refresh?.Mode;

    /// <summary>Scale as a percentage, held to 50–300 and to whole percents.</summary>
    public double ScalePercent
    {
        get => _scalePercent;
        set
        {
            value = Math.Round(Math.Clamp(value, MinScalePercent, MaxScalePercent));
            if (value == _scalePercent)
                return;
            this.RaiseAndSetIfChanged(ref _scalePercent, value);
            this.RaisePropertyChanged(nameof(ScalePercentValue));
            OnEdited(EditKind.Resized);
        }
    }

    /// <summary><see cref="ScalePercent"/> for the spin box, which works in decimals.</summary>
    public decimal? ScalePercentValue
    {
        get => (decimal)_scalePercent;
        set
        {
            if (value is { } percent)
                ScalePercent = (double)percent;
            else
                this.RaisePropertyChanged();
        }
    }

    public double Scale => _scalePercent / 100.0;

    public OutputTransform Transform
    {
        get => _transform;
        private set
        {
            if (value == _transform)
                return;
            this.RaiseAndSetIfChanged(ref _transform, value);
            this.RaisePropertyChanged(nameof(IsRotation0));
            this.RaisePropertyChanged(nameof(IsRotation90));
            this.RaisePropertyChanged(nameof(IsRotation180));
            this.RaisePropertyChanged(nameof(IsRotation270));
            OnEdited(EditKind.Resized);
        }
    }

    // One per orientation button. Only the quarter turn changes; a flip somebody set with
    // another tool is kept.
    public bool IsRotation0 { get => Rotation == 0; set { if (value) Rotate(0); } }
    public bool IsRotation90 { get => Rotation == 1; set { if (value) Rotate(1); } }
    public bool IsRotation180 { get => Rotation == 2; set { if (value) Rotate(2); } }
    public bool IsRotation270 { get => Rotation == 3; set { if (value) Rotate(3); } }

    private int Rotation => (int)_transform & 3;

    private void Rotate(int quarterTurns) =>
        Transform = (OutputTransform)(((int)_transform & 4) | quarterTurns);

    public bool AdaptiveSyncSupported => Head.AdaptiveSyncSupported;

    public bool AdaptiveSync
    {
        get => _adaptiveSync;
        set
        {
            if (value == _adaptiveSync || (value && !AdaptiveSyncSupported))
                return;
            this.RaiseAndSetIfChanged(ref _adaptiveSync, value);
            OnEdited(EditKind.None);
        }
    }

    public bool HdrSupported => Head.HdrSupported;

    /// <summary>Why the HDR box is grayed out, when it is.</summary>
    public string? HdrTip => HdrSupported ? null : Strings.HdrUnsupported;

    /// <summary>Why the adaptive sync box is grayed out, when it is.</summary>
    public string? AdaptiveSyncTip => AdaptiveSyncSupported ? null : Strings.AdaptiveSyncUnsupported;

    public bool Hdr
    {
        get => _hdr;
        set
        {
            if (value == _hdr || (value && !HdrSupported))
                return;
            this.RaiseAndSetIfChanged(ref _hdr, value);
            OnEdited(EditKind.None);
        }
    }

    /// <summary>Where SDR white lands while the display is in HDR, in cd/m².</summary>
    public double SdrWhite
    {
        get => _sdrWhite;
        set
        {
            var nits = (int)Math.Round(Math.Clamp(value, MinSdrWhite, MaxSdrWhite));
            if (nits == _sdrWhite)
                return;
            this.RaiseAndSetIfChanged(ref _sdrWhite, nits);
            this.RaisePropertyChanged(nameof(SdrWhiteValue));
            OnEdited(EditKind.None);
        }
    }

    /// <summary><see cref="SdrWhite"/> for the spin box.</summary>
    public decimal? SdrWhiteValue
    {
        get => _sdrWhite;
        set
        {
            if (value is { } nits)
                SdrWhite = (double)nits;
            else
                this.RaisePropertyChanged();
        }
    }

    public int X => _x;
    public int Y => _y;

    /// <summary>Where and how big this display is in the layout, as staged.</summary>
    public Box Box
    {
        get
        {
            var mode = Mode ?? Head.CurrentMode ?? Head.Modes.FirstOrDefault();
            var (width, height) = mode is null
                ? (0, 0)
                : ArrangementMath.LogicalSize(mode.Width, mode.Height, _transform, Scale);
            return new Box(_x, _y, width, height);
        }
    }

    /// <summary>Raised on any edit.</summary>
    public event Action<DisplayViewModel, EditKind>? Edited;

    /// <summary>Whether anything differs from what the compositor reported.</summary>
    public bool IsDirty
    {
        get
        {
            var head = Head;
            if (_enabled != head.Enabled)
                return true;
            if (!_enabled)
                return false;
            return !(Mode?.SameAs(head.CurrentMode) ?? head.CurrentMode is null)
                   || _x != head.X || _y != head.Y
                   || _transform != head.Transform
                   || Math.Abs(Scale - head.Scale) > ScaleTolerance
                   || _adaptiveSync != head.AdaptiveSync
                   || _hdr != head.HdrEnabled
                   || _sdrWhite != head.SdrWhiteNits;
        }
    }

    /// <summary>Move this display in the layout. Done by the arrangement, never by hand.</summary>
    internal void MoveTo(int x, int y)
    {
        if (x == _x && y == _y)
            return;
        _x = x;
        _y = y;
        this.RaisePropertyChanged(nameof(Box));
    }

    /// <summary>
    /// Take a newer description from the compositor. With <paramref name="keepEdits"/> the
    /// staged values stay as they are; otherwise they are replaced with the reported ones.
    /// </summary>
    internal void Update(OutputHead head, bool keepEdits)
    {
        Head = head;
        this.RaisePropertyChanged(nameof(Head));
        this.RaisePropertyChanged(nameof(Label));
        this.RaisePropertyChanged(nameof(HdrSupported));
        this.RaisePropertyChanged(nameof(HdrTip));
        this.RaisePropertyChanged(nameof(AdaptiveSyncSupported));
        this.RaisePropertyChanged(nameof(AdaptiveSyncTip));
        if (!keepEdits)
            Load(head);
        this.RaisePropertyChanged(nameof(IsDirty));
    }

    /// <summary>What to ask the compositor for.</summary>
    public OutputChange ToChange() => new(
        Head.Name, _enabled, Mode, _x, _y, _transform, Scale, _adaptiveSync, _hdr, _sdrWhite);

    private void Load(OutputHead head)
    {
        _enabled = head.Enabled;
        var mode = head.CurrentMode ?? head.Modes.FirstOrDefault(m => m.Preferred) ?? head.Modes.FirstOrDefault();
        _resolution = mode is null
            ? Resolutions.FirstOrDefault()
            : Resolutions.FirstOrDefault(r => r.Width == mode.Width && r.Height == mode.Height);
        _refreshRates = _resolution is null ? [] : BuildRefreshRates(_resolution);
        _refresh = _refreshRates.FirstOrDefault(r => r.Mode.SameAs(mode)) ?? _refreshRates.FirstOrDefault();
        _scalePercent = Math.Round(Math.Clamp(head.Scale * 100, MinScalePercent, MaxScalePercent));
        _transform = head.Transform;
        _adaptiveSync = head.AdaptiveSync;
        _hdr = head.HdrEnabled;
        _sdrWhite = Math.Clamp(head.SdrWhiteNits, MinSdrWhite, MaxSdrWhite);
        _x = head.X;
        _y = head.Y;

        // Everything at once, since all of it may have changed.
        this.RaisePropertyChanged(string.Empty);
    }

    private void OnEdited(EditKind kind)
    {
        if (kind != EditKind.None)
            this.RaisePropertyChanged(nameof(Box));
        this.RaisePropertyChanged(nameof(IsDirty));
        this.RaisePropertyChanged(nameof(TileLines));
        Edited?.Invoke(this, kind);
    }

    private static IReadOnlyList<ResolutionChoice> BuildResolutions(IReadOnlyList<OutputMode> modes)
    {
        var preferred = modes.FirstOrDefault(m => m.Preferred);
        return modes
            .Select(m => (m.Width, m.Height))
            .Distinct()
            .OrderByDescending(size => (long)size.Width * size.Height)
            .ThenByDescending(size => size.Width)
            .Select(size =>
            {
                var label = Strings.Resolution(size.Width, size.Height, AspectRatio(size.Width, size.Height));
                if (preferred is not null && preferred.Width == size.Width && preferred.Height == size.Height)
                    label = Strings.Preferred(label);
                return new ResolutionChoice(size.Width, size.Height, label);
            })
            .ToList();
    }

    private IReadOnlyList<RefreshChoice> BuildRefreshRates(ResolutionChoice size) =>
        Head.Modes
            .Where(m => m.Width == size.Width && m.Height == size.Height)
            .DistinctBy(m => m.RefreshMilliHz)
            .OrderByDescending(m => m.RefreshMilliHz)
            .Select(m => new RefreshChoice(m, Strings.Refresh(m.RefreshMilliHz)))
            .ToList();

    // The names people use, which are not always the reduced fraction: 1920×1200 is "16:10",
    // not "8:5", and 3440×1440 is sold as "21:9" though it is nearer 43:18.
    private static readonly (int W, int H)[] KnownAspects =
        [(16, 9), (16, 10), (4, 3), (5, 4), (3, 2), (21, 9), (32, 9), (32, 10), (1, 1)];

    /// <summary>The common name for a size's aspect ratio, or null when it has none.</summary>
    internal static string? AspectRatio(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;
        var ratio = (double)width / height;
        var best = KnownAspects.MinBy(a => Math.Abs(ratio / ((double)a.W / a.H) - 1));
        return Math.Abs(ratio / ((double)best.W / best.H) - 1) <= 0.03 ? $"{best.W}:{best.H}" : null;
    }
}
