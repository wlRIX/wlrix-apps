using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Settings.Displays.Localization;
using Wlrix.Settings.Displays.NightLight;

namespace Wlrix.Settings.Displays.ViewModels;

/// <summary>
/// The Night Light page: when the screen is warmed, how warm, and where sunset is worked out for.
///
/// This page saves as it goes, like the other wlRIX panels and unlike the page beside it. A
/// display mode is staged because a wrong one can leave nothing visible to undo it with; a
/// warmer screen cannot, and a temperature is something to judge by looking at it while the
/// slider moves. The compositor helps with that: a new temperature is shown at full strength
/// for a few seconds even at midday, when the schedule would otherwise show nothing.
/// </summary>
public sealed class NightLightViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// How long a change waits before it is written. Short, so the screen follows a dragged
    /// slider; not zero, so a drag is a handful of writes a second rather than one per pixel.
    /// </summary>
    internal static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(120);

    private readonly INightLightStore _store;
    private readonly Action<Action> _post;
    private readonly Action<Action> _defer;

    private NightLightSettings _settings = NightLightSettings.Default;
    private NightLightSettings _opened = NightLightSettings.Default;
    private int _minTemperature = 1000;
    private int _maxTemperature = 6500;
    private bool _isReady;
    private string _status = string.Empty;
    private bool _saveQueued;
    private bool _saving;
    private bool _unsaved;

    public NightLightViewModel() : this(new SampleNightLightStore()) { }

    /// <param name="post">
    /// How a change from elsewhere reaches the UI thread. Defaults to the dispatcher; the tests
    /// run it in place.
    /// </param>
    /// <param name="defer">
    /// How a save is put off by <see cref="SaveDelay"/>. Defaults to a dispatcher timer; the
    /// tests hold on to the action and run it when they choose.
    /// </param>
    public NightLightViewModel(INightLightStore store, Action<Action>? post = null, Action<Action>? defer = null)
    {
        _store = store;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _defer = defer ?? (action => DispatcherTimer.RunOnce(action, SaveDelay));
    }

    /// <summary>Whether the settings have been read, and so whether there is anything to edit.</summary>
    public bool IsReady
    {
        get => _isReady;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isReady, value);
            RaiseDerived();
        }
    }

    /// <summary>What became of the last change, when it is worth saying. Empty when it applied.</summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    // One per radio button. A button being unchecked says nothing: the one being checked does.
    public bool IsOff { get => _settings.Mode == NightLightMode.Off; set { if (value) SetMode(NightLightMode.Off); } }
    public bool IsOn { get => _settings.Mode == NightLightMode.On; set { if (value) SetMode(NightLightMode.On); } }
    public bool IsAuto { get => _settings.Mode == NightLightMode.Auto; set { if (value) SetMode(NightLightMode.Auto); } }

    public int MinTemperature => _minTemperature;
    public int MaxTemperature => _maxTemperature;

    /// <summary>The night color temperature in kelvin, for the slider.</summary>
    public double Temperature
    {
        get => _settings.Temperature;
        set
        {
            var kelvin = (int)Math.Round(Math.Clamp(value, _minTemperature, _maxTemperature));
            if (kelvin == _settings.Temperature)
                return;
            Edit(_settings with { Temperature = kelvin });
        }
    }

    /// <summary><see cref="Temperature"/> for the spin box.</summary>
    public decimal? TemperatureValue
    {
        get => _settings.Temperature;
        set
        {
            if (value is { } kelvin)
                Temperature = (double)kelvin;
            else
                this.RaisePropertyChanged();
        }
    }

    /// <summary>Sunset for the time zone's own coordinates, rather than the two below.</summary>
    public bool UseTimeZone
    {
        get => _settings.UseTimeZone;
        set
        {
            if (value != _settings.UseTimeZone)
                Edit(_settings with { UseTimeZone = value });
        }
    }

    /// <summary>Degrees north, for the spin box.</summary>
    public decimal? Latitude
    {
        get => (decimal)_settings.Latitude;
        set => SetCoordinate(value, 90, _settings.Latitude, v => _settings with { Latitude = v });
    }

    /// <summary>Degrees east, for the spin box.</summary>
    public decimal? Longitude
    {
        get => (decimal)_settings.Longitude;
        set => SetCoordinate(value, 180, _settings.Longitude, v => _settings with { Longitude = v });
    }

    /// <summary>The temperature means nothing while the night light is off.</summary>
    public bool CanSetTemperature => _isReady && _settings.Mode != NightLightMode.Off;

    /// <summary>Only the sunset-to-sunrise schedule needs to know where the sun is.</summary>
    public bool CanSetLocation => _isReady && _settings.Mode == NightLightMode.Auto;

    public bool CanSetCoordinates => CanSetLocation && !_settings.UseTimeZone;

    /// <summary>Whether anything differs from when the window opened.</summary>
    public bool CanReset => _isReady && _settings != _opened;

    /// <summary>
    /// Read the settings and listen for changes from elsewhere. Failing leaves the page drawn
    /// but not ready, with the reason in <see cref="Status"/>.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            var state = await _store.LoadAsync().ConfigureAwait(true);
            _minTemperature = state.MinTemperature;
            _maxTemperature = state.MaxTemperature;
            this.RaisePropertyChanged(nameof(MinTemperature));
            this.RaisePropertyChanged(nameof(MaxTemperature));

            _opened = state.Settings;
            Show(state.Settings);
            _store.Changed += OnChangedElsewhere;
            IsReady = true;
        }
        catch (NightLightUnsupportedException)
        {
            Status = Strings.NightLightUnsupported;
        }
        catch (Exception e)
        {
            // Bus activation means "not on the bus" is a broken install rather than an idle
            // session, so say so rather than silently doing nothing.
            Status = Strings.NoSettingsService(FirstLine(e));
        }
    }

    /// <summary>Put everything back to what it was when the window opened.</summary>
    public void Reset()
    {
        if (CanReset)
            Edit(_opened);
    }

    public void Dispose()
    {
        _store.Changed -= OnChangedElsewhere;
        _store.Dispose();
    }

    private void SetMode(NightLightMode mode)
    {
        if (mode != _settings.Mode)
            Edit(_settings with { Mode = mode });
    }

    private void SetCoordinate(decimal? value, double limit, double current, Func<double, NightLightSettings> with)
    {
        if (value is not { } degrees)
        {
            // The box was cleared: show what is still in effect rather than an empty field.
            RaiseAll();
            return;
        }
        // Four places is about ten meters, far finer than a sunset needs, and keeps the config
        // file free of the tail a decimal-to-double conversion would otherwise leave in it.
        var rounded = Math.Round(Math.Clamp((double)degrees, -limit, limit), 4);
        if (rounded != current)
            Edit(with(rounded));
    }

    /// <summary>A change made here: show it, and write it shortly.</summary>
    private void Edit(NightLightSettings settings)
    {
        Show(settings);
        if (!_isReady)
            return;
        _unsaved = true;
        QueueSave();
    }

    private void Show(NightLightSettings settings)
    {
        _settings = settings;
        RaiseAll();
    }

    private void QueueSave()
    {
        if (_saveQueued)
            return;
        _saveQueued = true;
        _defer(() => _ = SaveAsync());
    }

    private async Task SaveAsync()
    {
        _saveQueued = false;
        if (!_unsaved)
            return;
        if (_saving)
        {
            // The last write has not come back yet. Writes must not overtake each other, or an
            // older temperature could land after a newer one; wait another turn.
            QueueSave();
            return;
        }

        _saving = true;
        _unsaved = false;
        try
        {
            Status = await _store.SaveAsync(_settings).ConfigureAwait(true) ?? string.Empty;
        }
        catch (Exception e)
        {
            // The daemon refusing a value, or the compositor refusing the file, both land here
            // with a message that names the key.
            Status = FirstLine(e);
        }
        finally
        {
            _saving = false;
        }
    }

    private void OnChangedElsewhere(NightLightSettings settings) => _post(() =>
    {
        // Shown, not written back: it is already in the file.
        Show(settings);
    });

    private void RaiseAll()
    {
        this.RaisePropertyChanged(nameof(IsOff));
        this.RaisePropertyChanged(nameof(IsOn));
        this.RaisePropertyChanged(nameof(IsAuto));
        this.RaisePropertyChanged(nameof(Temperature));
        this.RaisePropertyChanged(nameof(TemperatureValue));
        this.RaisePropertyChanged(nameof(UseTimeZone));
        this.RaisePropertyChanged(nameof(Latitude));
        this.RaisePropertyChanged(nameof(Longitude));
        RaiseDerived();
    }

    private void RaiseDerived()
    {
        this.RaisePropertyChanged(nameof(CanSetTemperature));
        this.RaisePropertyChanged(nameof(CanSetLocation));
        this.RaisePropertyChanged(nameof(CanSetCoordinates));
        this.RaisePropertyChanged(nameof(CanReset));
    }

    private static string FirstLine(Exception e) => e.Message.Split('\n')[0];
}
