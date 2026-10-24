using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Settings.Client;
using Wlrix.Settings.International.Core;
using Wlrix.Settings.International.Localization;
using Wlrix.Settings.International.Services;

namespace Wlrix.Settings.International.ViewModels;

/// <summary>
/// Backs the International window: IRIX's ipanel, which is a world map, four buttons that each
/// open a chooser -- Country, Language, Keyboard Layout, Time Zone -- and Cancel, Set System and
/// Set Personal.
/// </summary>
/// <remarks>
/// <para>
/// Choices are <em>staged</em>: the dialogs change what the window shows, and nothing is written
/// until one of the Set buttons, which is what lets one window offer both. Set Personal writes
/// the language to <c>~/.config/locale.conf</c> and the keyboard through <c>com.wlrix.Settings</c>;
/// Set System writes the language, keyboard and time zone through systemd-localed and
/// systemd-timedated. The time zone has no personal setting.
/// </para>
/// <para>
/// The clock is the exception. Turning NTP on or off and setting the time are actions on the
/// machine, not settings to stage, and the Time Zone dialog does them at once.
/// </para>
/// </remarks>
public sealed class InternationalViewModel : ViewModelBase, IDisposable
{
    private const string LayoutKey = "compositor.keyboard.layout";
    private const string VariantKey = "compositor.keyboard.variant";
    private const string ModelKey = "compositor.keyboard.model";
    private const string OptionsKey = "compositor.keyboard.options";

    private SettingsClient? _settings;
    private SystemSettings? _system;

    // What is in force, for deciding what each Set button has to write.
    private LocaleEntry? _personalLanguage;
    private LocaleEntry? _personalFormats;
    private KeyboardSetting _personalKeyboard = KeyboardSetting.Default;
    private LocaleEntry? _systemLanguage;
    private LocaleEntry? _systemFormats;
    private KeyboardSetting _systemKeyboard = KeyboardSetting.Default;
    private string? _systemTimeZone;

    // What the window shows: staged until a Set button.
    private Country? _country;
    private LocaleEntry? _language;
    private LocaleEntry? _formats;
    private KeyboardSetting _keyboard = KeyboardSetting.Default;
    private TimeZoneEntry? _timeZone;

    private bool _countryChosen;
    private bool _languageChosen;
    private bool _keyboardChosen;
    private bool _timeZoneChosen;

    private ClockState? _clock;
    private bool _ready;
    private bool _busy;
    private string _status = string.Empty;

    public IReadOnlyList<Country> Countries { get; private set; } = [];
    public IReadOnlyList<LocaleEntry> Locales { get; private set; } = [];
    internal XkbRegistry Xkb { get; private set; } = XkbRegistry.Empty;
    public IReadOnlyList<TimeZoneEntry> TimeZones { get; private set; } = [];

    /// <summary>The world map, when <c>wlrix-assets</c> installed it; the area is plain without it.</summary>
    public Bitmap? Map { get; private set; }

    /// <summary>The four buttons' pictures: other panels' icons, from the icon theme.</summary>
    public Bitmap? CountryIcon { get; private set; }
    public Bitmap? LanguageIcon { get; private set; }
    public Bitmap? KeyboardIcon { get; private set; }
    public Bitmap? TimeZoneIcon { get; private set; }

    public Country? Country => _country;
    public LocaleEntry? Language => _language;

    /// <summary>The formats locale, or null for the language's own.</summary>
    public LocaleEntry? Formats => _formats;

    public KeyboardSetting Keyboard => _keyboard;
    public TimeZoneEntry? TimeZone => _timeZone;

    /// <summary>The line under the map: the country, or IRIX's instruction until there is one.</summary>
    public string Prompt => _country?.Name ?? Strings.ChooseCountryPrompt;

    public string CountryLabel => _country?.Name ?? string.Empty;
    public string LanguageLabel => _language?.DisplayName ?? string.Empty;
    public string KeyboardLabel => string.Join(" / ", _keyboard.Layouts.Select(choice => LayoutName(choice.Layout)));
    public string TimeZoneLabel => _timeZone?.Id.Replace('_', ' ') ?? string.Empty;

    // The green border IRIX drew around a button once it held a choice.
    public bool CountryChosen { get => _countryChosen; private set => this.RaiseAndSetIfChanged(ref _countryChosen, value); }
    public bool LanguageChosen { get => _languageChosen; private set => this.RaiseAndSetIfChanged(ref _languageChosen, value); }
    public bool KeyboardChosen { get => _keyboardChosen; private set => this.RaiseAndSetIfChanged(ref _keyboardChosen, value); }
    public bool TimeZoneChosen { get => _timeZoneChosen; private set => this.RaiseAndSetIfChanged(ref _timeZoneChosen, value); }

    /// <summary>The map's frame in the window; the picture is scaled into it, whatever its own size.</summary>
    public const double MapWidth = 800;
    public const double MapHeight = 400;
    private const double MarkerRadius = 6;

    /// <summary>Where the time zone's marker goes in the map's frame: its top-left corner.</summary>
    public double MarkerLeft => _timeZone is { } zone ? zone.MapPoint(MapWidth, MapHeight).X - MarkerRadius : 0;
    public double MarkerTop => _timeZone is { } zone ? zone.MapPoint(MapWidth, MapHeight).Y - MarkerRadius : 0;
    public bool HasMarker => _timeZone is not null && Map is not null;

    public ClockState? Clock
    {
        get => _clock;
        private set
        {
            this.RaiseAndSetIfChanged(ref _clock, value);
            this.RaisePropertyChanged(nameof(Ntp));
            this.RaisePropertyChanged(nameof(CanSetClock));
            this.RaisePropertyChanged(nameof(ClockStatus));
        }
    }

    public bool Ntp => _clock?.Ntp ?? false;

    /// <summary>timedated refuses to set the time while NTP is keeping it.</summary>
    public bool CanSetClock => _clock is { Ntp: false } && !_busy;

    public string ClockStatus => _clock switch
    {
        null => string.Empty,
        { CanNtp: false } => Strings.NtpUnavailable,
        { Ntp: true, Synchronized: true } => Strings.NtpSynchronized,
        { Ntp: true } => Strings.NtpWaiting,
        _ => Strings.NtpOff,
    };

    /// <summary>Whether everything has loaded and the Set buttons mean anything yet.</summary>
    public bool IsReady
    {
        get => _ready;
        private set
        {
            this.RaiseAndSetIfChanged(ref _ready, value);
            this.RaisePropertyChanged(nameof(CanApply));
        }
    }

    public bool CanApply => _ready && !_busy;

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    private bool Busy
    {
        set
        {
            _busy = value;
            this.RaisePropertyChanged(nameof(CanApply));
            this.RaisePropertyChanged(nameof(CanSetClock));
        }
    }

    /// <summary>
    /// Read the lists and what is in force. The lists are files, some of them large, so they are
    /// read off the UI thread; the window is up and empty meanwhile.
    /// </summary>
    public async Task InitializeAsync()
    {
        var (countries, locales, xkb, zones, map, icons) = await Task.Run(() =>
        (
            Services.Countries.Load(),
            Services.Locales.Load(),
            XkbRegistry.Load(),
            Services.TimeZones.Load(),
            WorldMap.Load(),
            PanelIcons.Load()
        )).ConfigureAwait(true);

        Countries = countries;
        Locales = locales;
        Xkb = xkb;
        TimeZones = zones;
        Map = map;
        (CountryIcon, LanguageIcon, KeyboardIcon, TimeZoneIcon) = icons;
        this.RaisePropertyChanged(nameof(Map));
        this.RaisePropertyChanged(nameof(CountryIcon));
        this.RaisePropertyChanged(nameof(LanguageIcon));
        this.RaisePropertyChanged(nameof(KeyboardIcon));
        this.RaisePropertyChanged(nameof(TimeZoneIcon));

        var problems = new List<string>();
        await LoadPersonalAsync(problems).ConfigureAwait(true);
        await LoadSystemAsync(problems).ConfigureAwait(true);

        _language = _personalLanguage ?? _systemLanguage;
        _formats = _personalLanguage is not null ? _personalFormats : _systemFormats;
        _keyboard = _personalKeyboard;
        _timeZone = TimeZones.FirstOrDefault(zone => zone.Id == _systemTimeZone);
        _country = _language is { Territory.Length: > 0 } language
            ? Countries.FirstOrDefault(country => country.Code == language.Territory)
            : null;
        Refresh();

        Status = string.Join(' ', problems);
        IsReady = true;
    }

    private async Task LoadPersonalAsync(List<string> problems)
    {
        var path = LocaleConf.UserPath();
        IReadOnlyDictionary<string, string> values = new Dictionary<string, string>();
        try
        {
            if (File.Exists(path))
                values = LocaleConf.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add(Strings.CouldNotRead(path, e.Message));
        }

        // No personal file means the session is on whatever greetd handed it, which is the
        // system's language: that is what is in force, so that is what the button shows.
        _personalLanguage = Services.Locales.Find(Locales, values.GetValueOrDefault("LANG"));
        _personalFormats = Services.Locales.Find(Locales, LocaleConf.FormatsOf(values));

        try
        {
            _settings = await SettingsClient.ConnectAsync().ConfigureAwait(true);
            _personalKeyboard = KeyboardSetting.Parse(
                await _settings.GetAsync(LayoutKey).ConfigureAwait(true) as string,
                await _settings.GetAsync(VariantKey).ConfigureAwait(true) as string,
                await _settings.GetAsync(ModelKey).ConfigureAwait(true) as string,
                await _settings.GetAsync(OptionsKey).ConfigureAwait(true) as string);
        }
        catch (Exception e)
        {
            problems.Add(Strings.NoSettingsService(e.Message));
        }
    }

    private async Task LoadSystemAsync(List<string> problems)
    {
        try
        {
            _system = await SystemSettings.ConnectAsync().ConfigureAwait(true);
            _system.ClockChanged += OnClockChanged;

            var (language, formats) = await _system.GetLocaleAsync().ConfigureAwait(true);
            _systemLanguage = Services.Locales.Find(Locales, language);
            _systemFormats = Services.Locales.Find(Locales, formats);
            _systemKeyboard = await _system.GetKeyboardAsync().ConfigureAwait(true);
            Clock = await _system.GetClockAsync().ConfigureAwait(true);
            _systemTimeZone = Clock.TimeZone;
        }
        catch (Exception e)
        {
            problems.Add(Strings.NoSystemServices(SystemSettings.Message(e)));
        }

        // A personal keyboard nobody has set is the system's, which is what the compositor falls
        // back to as well.
        if (_settings is null || _personalKeyboard == KeyboardSetting.Default)
            _personalKeyboard = _systemKeyboard;
    }

    /// <summary>
    /// Country chosen: stage the language, keyboard and time zone it suggests, as IRIX did. A
    /// keyboard list that already has the country's layout is left alone -- somebody with
    /// <c>us,jp</c> who picks Japan has the Japanese layout already and wants to keep US too.
    /// </summary>
    public void ChooseCountry(Country country)
    {
        _country = country;
        CountryChosen = true;

        var suggestion = CountryDefaults.Suggest(country.Code, Locales, Xkb.Layouts, TimeZones);
        if (suggestion.Locale is { } locale)
        {
            _language = locale;
            _formats = null;
            LanguageChosen = true;
        }
        if (suggestion.Layout is { } layout && _keyboard.Layouts.All(choice => choice.Layout != layout.Name))
        {
            _keyboard = _keyboard with { Layouts = [new LayoutChoice(layout.Name, string.Empty)] };
            KeyboardChosen = true;
        }
        else if (suggestion.Layout is not null)
        {
            KeyboardChosen = true;
        }
        if (suggestion.TimeZone is { } zone)
        {
            _timeZone = zone;
            TimeZoneChosen = true;
        }
        Refresh();
    }

    public void ChooseLanguage(LocaleEntry language, LocaleEntry? formats)
    {
        _language = language;
        _formats = formats is not null && formats.Name != language.Name ? formats : null;
        LanguageChosen = true;
        Refresh();
    }

    public void ChooseKeyboard(KeyboardSetting keyboard)
    {
        _keyboard = keyboard;
        KeyboardChosen = true;
        Refresh();
    }

    public void ChooseTimeZone(TimeZoneEntry zone)
    {
        _timeZone = zone;
        TimeZoneChosen = true;
        Refresh();
    }

    /// <summary>
    /// Set Personal: this account's language and keyboard. The time zone is the machine's, so a
    /// change to it alone is pointed at Set System rather than silently dropped.
    /// </summary>
    public async Task SetPersonalAsync()
    {
        Busy = true;
        var said = new List<string>();
        try
        {
            if (_language is { } language
                && (language != _personalLanguage || _formats != _personalFormats || !File.Exists(LocaleConf.UserPath())))
            {
                if (!await EnsureGeneratedAsync([language, _formats], said).ConfigureAwait(true))
                    return;

                var path = LocaleConf.UserPath();
                var existing = File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(true) : string.Empty;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, LocaleConf.Write(existing, language.Name, _formats?.Name)).ConfigureAwait(true);
                _personalLanguage = language;
                _personalFormats = _formats;
                said.Add(Strings.LanguageAtNextLogin);
            }

            if (_keyboard != _personalKeyboard && _settings is not null)
            {
                var result = await _settings.SetManyAsync(new Dictionary<string, object>
                {
                    [LayoutKey] = _keyboard.Layout,
                    [VariantKey] = _keyboard.Variant,
                    [ModelKey] = _keyboard.Model,
                    [OptionsKey] = _keyboard.Options,
                }).ConfigureAwait(true);
                _personalKeyboard = _keyboard;
                if (result.Advice is { } advice)
                    said.Add(advice);
            }

            if (_timeZone is { } zone && zone.Id != _systemTimeZone)
                said.Add(Strings.TimeZoneIsSystemWide);

            Status = said.Count > 0 ? string.Join(' ', said) : Strings.Saved;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = Strings.CouldNotWrite(LocaleConf.UserPath(), e.Message);
        }
        catch (Exception e)
        {
            Status = e.Message.Split('\n')[0];
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Set System: the machine's language, keyboard and time zone, each only if it differs from
    /// what the machine has, because each is an administrator's password prompt.
    /// </summary>
    public async Task SetSystemAsync()
    {
        if (_system is null)
            return;

        Busy = true;
        var said = new List<string>();
        try
        {
            if (_language is { } language && (language != _systemLanguage || _formats != _systemFormats))
            {
                if (!await EnsureGeneratedAsync([language, _formats], said).ConfigureAwait(true))
                    return;
                await _system.SetLocaleAsync(language.Name, _formats?.Name).ConfigureAwait(true);
                _systemLanguage = language;
                _systemFormats = _formats;
                said.Add(Strings.LanguageAtNextLogin);
            }

            if (_keyboard != _systemKeyboard)
            {
                await _system.SetKeyboardAsync(_keyboard).ConfigureAwait(true);
                _systemKeyboard = _keyboard;
            }

            if (_timeZone is { } zone && zone.Id != _systemTimeZone)
            {
                await _system.SetTimeZoneAsync(zone.Id).ConfigureAwait(true);
                _systemTimeZone = zone.Id;
            }

            Status = said.Count > 0 ? string.Join(' ', said) : Strings.Saved;
        }
        catch (Exception e)
        {
            Status = SystemSettings.IsRefusal(e) ? Strings.NotAuthorized : SystemSettings.Message(e);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Turn NTP on or off, now.</summary>
    public async Task SetNtpAsync(bool enabled)
    {
        if (_system is null || _clock is null || _clock.Ntp == enabled)
            return;
        Busy = true;
        try
        {
            await _system.SetNtpAsync(enabled).ConfigureAwait(true);
            Clock = await _system.GetClockAsync().ConfigureAwait(true);
        }
        catch (Exception e)
        {
            Status = SystemSettings.IsRefusal(e) ? Strings.NotAuthorized : SystemSettings.Message(e);
            // Put the checkbox back where the machine actually is.
            this.RaisePropertyChanged(nameof(Ntp));
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Set the clock to <paramref name="local"/>, a time in the machine's own zone, now.</summary>
    public async Task SetClockAsync(DateTime local)
    {
        if (_system is null)
            return;
        Busy = true;
        try
        {
            await _system.SetTimeAsync(new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local))).ConfigureAwait(true);
            Status = Strings.ClockSet;
        }
        catch (Exception e)
        {
            Status = SystemSettings.IsRefusal(e) ? Strings.NotAuthorized : SystemSettings.Message(e);
        }
        finally
        {
            Busy = false;
        }
    }

    public void Dispose()
    {
        if (_system is not null)
        {
            _system.ClockChanged -= OnClockChanged;
            _system.Dispose();
            _system = null;
        }
        _settings?.Dispose();
        _settings = null;
    }

    /// <summary>
    /// Generate whichever of <paramref name="locales"/> are not built yet, through the helper.
    /// False, with the reason in <paramref name="said"/> and the status line, when that failed --
    /// writing a language nothing can load would bring the next login up in English.
    /// </summary>
    private async Task<bool> EnsureGeneratedAsync(IEnumerable<LocaleEntry?> locales, List<string> said)
    {
        var runner = new LocaleHelperRunner();
        foreach (var locale in locales.OfType<LocaleEntry>().Where(locale => !locale.IsGenerated).DistinctBy(locale => locale.Name).ToList())
        {
            Status = Strings.Generating(locale.DisplayName);
            var result = await runner.GenerateAsync(locale.Name).ConfigureAwait(true);
            if (result.Outcome != HelperOutcome.Succeeded)
            {
                Status = result.Outcome == HelperOutcome.NotAuthorized
                    ? Strings.NotAuthorized
                    : Strings.GenerateFailed(locale.Name, result.Message ?? result.Outcome.ToString());
                return false;
            }

            var generated = locale with { IsGenerated = true };
            Locales = [.. Locales.Select(entry => entry.Name == locale.Name ? generated : entry)];
            if (_language?.Name == locale.Name)
                _language = generated;
            if (_formats?.Name == locale.Name)
                _formats = generated;
        }
        return true;
    }

    private void OnClockChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(async () =>
        {
            if (_system is null)
                return;
            try
            {
                Clock = await _system.GetClockAsync().ConfigureAwait(true);
                _systemTimeZone = Clock.TimeZone;
            }
            catch (Exception)
            {
                // The next change will try again; a missed refresh only leaves a stale line.
            }
        });

    private string LayoutName(string name) => Xkb.Layout(name)?.Description ?? name;

    private void Refresh()
    {
        foreach (var name in new[]
                 {
                     nameof(Country), nameof(Language), nameof(Formats), nameof(Keyboard), nameof(TimeZone),
                     nameof(Prompt), nameof(CountryLabel), nameof(LanguageLabel), nameof(KeyboardLabel),
                     nameof(TimeZoneLabel), nameof(MarkerLeft), nameof(MarkerTop), nameof(HasMarker),
                 })
            this.RaisePropertyChanged(name);
    }

    /// <summary>A date, a number and an amount of money in <paramref name="locale"/>'s formats.</summary>
    public static string Preview(LocaleEntry? locale)
    {
        if (locale is null)
            return string.Empty;
        try
        {
            var culture = CultureInfo.GetCultureInfo(Services.Locales.CultureName(locale.Name));
            var now = DateTime.Now;
            return string.Join("\n",
                now.ToString("D", culture) + "  " + now.ToString("T", culture),
                1234567.89.ToString("N2", culture),
                1234.56m.ToString("C", culture));
        }
        catch (CultureNotFoundException)
        {
            return string.Empty;
        }
    }
}
