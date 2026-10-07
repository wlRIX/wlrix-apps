// SPDX-License-Identifier: GPL-3.0-or-later

using ReactiveUI;
using Wlrix.Settings.Audio.Audio;
using Wlrix.Settings.Audio.Localization;
using Wlrix.Settings.Audio.Models;
using Wlrix.Settings.Audio.Services;

namespace Wlrix.Settings.Audio.ViewModels;

/// <summary>A choice in one of the context menu's radio submenus.</summary>
/// <param name="Value">What choosing it sends: a rate in Hz (0 for Automatic), or a port name.</param>
public sealed record MenuChoice(string Value, string Label, bool IsChecked, bool IsEnabled, string Hint);

/// <summary>
/// One device's column: its sliders, its toggles, the lines of text under them, and what its
/// context menu offers.
///
/// Every change goes straight to the server, as on the IRIX panel; there is nothing to apply.
/// What the column shows is what the server last said, except for the sliders just after
/// they have been moved, which would otherwise jump back to the previous value for the moment
/// the server's answer takes.
/// </summary>
public sealed class DeviceColumnViewModel : ViewModelBase
{
    /// <summary>The rates the Sample Rate menu offers, the IRIX list.</summary>
    internal static readonly int[] Rates = [8000, 11025, 16000, 22050, 32000, 44100, 48000, 96000, 192000];

    /// <summary>How long after a slider moves the server's older values for it are ignored.</summary>
    private const long SettleMilliseconds = 400;

    private readonly IAudioFeed _feed;
    private readonly PanelState _state;
    private readonly Func<long> _clock;
    private AudioDevice _device;
    private bool _isPipeWire;
    private IReadOnlyList<int> _allowedRates = [];
    private int _forcedRate;
    private double _left;
    private double _right;
    private bool _grouped;
    private bool _metering;
    private bool _isSelected;
    private float _leftPeak;
    private float _rightPeak;
    private long _movedAt = long.MinValue;
    private bool _updating;

    internal DeviceColumnViewModel(AudioDevice device, IAudioFeed feed, PanelState state, Func<long>? clock = null)
    {
        _device = device;
        _feed = feed;
        _state = state;
        _clock = clock ?? (() => Environment.TickCount64);
        var remembered = state.Get(device.Key);
        _grouped = remembered.Grouped;
        _metering = remembered.Metering && device.Kind == DeviceKind.Input;
        TakeVolumes(force: true);
        if (_metering && !device.IsPlaceholder)
            _feed.SetMetering(device.Key, true);
    }

    public AudioDevice Device => _device;
    public string Key => _device.Key;
    public bool IsInput => _device.Kind == DeviceKind.Input;
    public bool IsOutput => _device.Kind == DeviceKind.Output;

    /// <summary>The column header: the device's name.</summary>
    public string Title => _device.Description;

    /// <summary>The italic line under the header: the jack in use.</summary>
    public string Subtitle => _device.ActivePortDescription ?? (_device.IsPlaceholder ? Strings.Off : "");

    /// <summary>"48 kHz"; empty for a device that is not there.</summary>
    public string RateText => _device.SampleRate == 0 ? "" : Strings.Rate(_device.SampleRate);

    /// <summary>"Internal", "USB" and so on.</summary>
    public string BusText => Strings.Bus(_device.Bus);

    /// <summary>Lights the header lamp: sound is flowing.</summary>
    public bool IsActive => _device.State == DeviceState.Running;

    /// <summary>False for an unplugged or switched-off device, whose column is greyed.</summary>
    public bool IsAvailable => _device.IsAvailable && !_device.IsPlaceholder;

    /// <summary>Two sliders, or one for a mono device.</summary>
    public bool IsStereo => _device.Volumes.Count >= 2;

    public string ColumnHint => _device.IsPlaceholder ? Strings.SwitchedOffHint(Title)
        : !_device.IsAvailable ? Strings.UnavailableHint(Title)
        : Strings.ColumnHint(Title);

    public string LeftHint => Strings.GainHint(_device.Kind, PortName, IsStereo ? ChannelPosition.Left : null);
    public string RightHint => Strings.GainHint(_device.Kind, PortName, ChannelPosition.Right);
    public string LeftMeterHint => Strings.MeterLevelHint(ChannelPosition.Left);
    public string RightMeterHint => Strings.MeterLevelHint(ChannelPosition.Right);

    private string PortName => _device.ActivePortDescription ?? Title;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    /// <summary>The left slider, or a mono device's only one, 0 to 10.</summary>
    public double Left
    {
        get => _left;
        set => MoveSlider(ref _left, value, LeftChannel, nameof(Left));
    }

    /// <summary>The right slider, 0 to 10.</summary>
    public double Right
    {
        get => _right;
        set => MoveSlider(ref _right, value, RightChannel, nameof(Right));
    }

    public bool Grouped
    {
        get => _grouped;
        set
        {
            if (_grouped == value)
                return;
            this.RaiseAndSetIfChanged(ref _grouped, value);
            Remember();
        }
    }

    public bool Muted
    {
        get => _device.Muted;
        set
        {
            if (_device.Muted != value && !_updating)
                _feed.SetMute(Key, value);
        }
    }

    public bool Metering
    {
        get => _metering;
        set
        {
            if (_metering == value)
                return;
            this.RaiseAndSetIfChanged(ref _metering, value);
            _feed.SetMetering(Key, value);
            Remember();
            if (!value)
                SetPeaks(0, 0);
        }
    }

    public bool Monitoring
    {
        get => _device.IsMonitored;
        set
        {
            if (_device.IsMonitored != value && !_updating)
                _feed.SetMonitor(Key, value);
        }
    }

    /// <summary>The newest peaks, 0 to 1 and over; the meters hold and decay them.</summary>
    public float LeftPeak
    {
        get => _leftPeak;
        private set => this.RaiseAndSetIfChanged(ref _leftPeak, value);
    }

    public float RightPeak
    {
        get => _rightPeak;
        private set => this.RaiseAndSetIfChanged(ref _rightPeak, value);
    }

    internal void SetPeaks(float left, float right)
    {
        LeftPeak = left;
        RightPeak = IsStereo ? right : left;
    }

    // ------------------------------------------------------------ the context menu

    /// <summary>"Make Default Output", or Input.</summary>
    public string MakeDefaultLabel => IsInput ? Strings.MenuMakeDefaultInput : Strings.MenuMakeDefaultOutput;
    public string MakeDefaultHint => IsInput ? Strings.HintMakeDefaultInput : Strings.HintMakeDefaultOutput;

    /// <summary>"Output Destination", or "Input Source".</summary>
    public string PortMenuLabel => IsInput ? Strings.MenuInputSource : Strings.MenuOutputDestination;

    public bool CanMakeDefault => IsAvailable && !_device.IsDefault;
    public bool CanEnable => _device.IsPlaceholder;
    public bool CanChangeRate => _isPipeWire && !_device.IsPlaceholder;
    public bool CanChangePort => !_device.IsPlaceholder && _device.Ports.Count > 1;

    /// <summary>
    /// Automatic, then the IRIX rates. The rate is PipeWire's graph rate, the same for every
    /// device, so the checked item is the forced rate rather than this device's own.
    /// </summary>
    public IReadOnlyList<MenuChoice> RateChoices
    {
        get
        {
            var choices = new List<MenuChoice>
            {
                new("0", Strings.RateAutomatic, _forcedRate == 0, CanChangeRate,
                    CanChangeRate ? Strings.RateHint(0) : Strings.HintRateNotPipeWire),
            };
            foreach (var rate in Rates)
            {
                var allowed = CanChangeRate && _allowedRates.Contains(rate);
                var hint = !_isPipeWire ? Strings.HintRateNotPipeWire
                    : allowed ? Strings.RateHint(rate)
                    : Strings.HintRateUnavailable;
                choices.Add(new MenuChoice(rate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Strings.Rate((uint)rate), _forcedRate == rate, allowed, hint));
            }
            return choices;
        }
    }

    public IReadOnlyList<MenuChoice> PortChoices => _device.Ports
        .Select(port => new MenuChoice(port.Name, port.Description, port.Name == _device.ActivePort,
            port.Available || port.Name == _device.ActivePort,
            port.Available ? Strings.PortHint(port.Description) : Strings.PortUnavailableHint(port.Description)))
        .ToList();

    internal void ChooseRate(string value) =>
        _feed.SetForcedRate(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

    internal void ChoosePort(string port)
    {
        if (port != _device.ActivePort)
            _feed.SetPort(Key, port);
    }

    internal void MakeDefault()
    {
        if (CanMakeDefault)
            _feed.SetDefault(Key);
    }

    internal void Enable()
    {
        if (CanEnable)
            _feed.EnableDevice(Key);
    }

    // ------------------------------------------------------------ updates

    /// <summary>Take what the server now says about the device.</summary>
    internal void Update(AudioDevice device, bool isPipeWire, IReadOnlyList<int> allowedRates, int forcedRate)
    {
        var wasPlaceholder = _device.IsPlaceholder;
        _device = device;
        _isPipeWire = isPipeWire;
        _allowedRates = allowedRates;
        _forcedRate = forcedRate;
        TakeVolumes(force: false);

        // A device that has just been switched on picks up its remembered meter.
        if (wasPlaceholder && !device.IsPlaceholder && _metering)
            _feed.SetMetering(Key, true);

        _updating = true;
        try
        {
            foreach (var name in new[]
                     {
                         nameof(Device), nameof(Title), nameof(Subtitle), nameof(RateText), nameof(BusText),
                         nameof(IsActive), nameof(IsAvailable), nameof(IsStereo), nameof(Muted), nameof(Monitoring),
                         nameof(ColumnHint), nameof(LeftHint), nameof(RightHint), nameof(CanMakeDefault),
                         nameof(CanEnable), nameof(CanChangeRate), nameof(CanChangePort),
                     })
            {
                this.RaisePropertyChanged(name);
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private int LeftChannel => Channel(ChannelPosition.Left, 0);
    private int RightChannel => Channel(ChannelPosition.Right, Math.Min(1, _device.Volumes.Count - 1));

    private int Channel(ChannelPosition position, int fallback)
    {
        for (var i = 0; i < _device.Positions.Count; i++)
        {
            if (_device.Positions[i] == position)
                return i;
        }
        return fallback;
    }

    private void TakeVolumes(bool force)
    {
        if (!force && _clock() - _movedAt < SettleMilliseconds)
            return;
        if (_device.Volumes.Count == 0)
            return;
        _updating = true;
        try
        {
            SetSlider(ref _left, VolumeScale.ToSlider(_device.Volumes[LeftChannel]), nameof(Left));
            SetSlider(ref _right, VolumeScale.ToSlider(_device.Volumes[RightChannel]), nameof(Right));
        }
        finally
        {
            _updating = false;
        }
    }

    private void SetSlider(ref double field, double value, string name)
    {
        if (Math.Abs(field - value) < 1e-9)
            return;
        field = value;
        this.RaisePropertyChanged(name);
    }

    private void MoveSlider(ref double field, double value, int channel, string name)
    {
        if (Math.Abs(field - value) < 1e-9)
            return;
        field = value;
        this.RaisePropertyChanged(name);
        if (_updating || _device.IsPlaceholder || _device.Volumes.Count == 0)
            return;

        _movedAt = _clock();
        if (_grouped && IsStereo)
        {
            // The other slider follows on screen at once, not when the server answers.
            _updating = true;
            try
            {
                SetSlider(ref _left, value, nameof(Left));
                SetSlider(ref _right, value, nameof(Right));
            }
            finally
            {
                _updating = false;
            }
        }

        var volumes = VolumeScale.Move(_device.Volumes, channel, value, _grouped);
        // Keep the device's volumes current with what was sent, so that moving the other
        // slider before the server answers starts from this value rather than the old one.
        _device = _device with { Volumes = volumes };
        _feed.SetVolume(Key, volumes);
    }

    private void Remember() => _state.Set(Key, new ColumnState(_grouped, _metering));
}
