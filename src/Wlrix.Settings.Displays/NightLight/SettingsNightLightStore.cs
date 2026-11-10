using System.Globalization;
using Wlrix.Settings.Client;

namespace Wlrix.Settings.Displays.NightLight;

/// <summary>
/// The night light's settings, through <c>wlrix-settings-daemon</c>.
///
/// Unlike everything else in this panel, which configures displays live over
/// wlr-output-management. The night light is a schedule the compositor keeps, not a state of one
/// display, so it lives in <c>compositor.toml</c> with the rest of the compositor's settings and
/// is written the way every other panel writes those.
/// </summary>
public sealed class SettingsNightLightStore : INightLightStore
{
    internal const string ModeKey = "compositor.night_light.mode";
    internal const string TemperatureKey = "compositor.night_light.temperature";
    internal const string LocationKey = "compositor.night_light.location";
    internal const string LatitudeKey = "compositor.night_light.latitude";
    internal const string LongitudeKey = "compositor.night_light.longitude";

    private const string LocationTimeZone = "timezone";
    private const string LocationManual = "manual";

    private SettingsClient? _client;
    private NightLightSettings _current = NightLightSettings.Default;

    public event Action<NightLightSettings>? Changed;

    public async Task<NightLightState> LoadAsync()
    {
        _client = await SettingsClient.ConnectAsync().ConfigureAwait(false);
        var described = (await _client.DescribeAsync("compositor").ConfigureAwait(false))
            .ToDictionary(setting => setting.Key);
        // A daemon from before the night light existed answers, but not about this. Writing
        // keys it does not know would be refused one at a time; better to say so once.
        if (!described.TryGetValue(TemperatureKey, out var temperature) || !described.ContainsKey(ModeKey))
            throw new NightLightUnsupportedException();

        var values = await _client.GetAllAsync("compositor").ConfigureAwait(false);
        _current = Merge(NightLightSettings.Default, values);

        await _client.WatchAsync().ConfigureAwait(false);
        _client.Changed += OnChangedElsewhere;

        return new NightLightState(
            _current,
            (int)(temperature.Min ?? 1000),
            (int)(temperature.Max ?? 6500));
    }

    public async Task<string?> SaveAsync(NightLightSettings settings)
    {
        if (_client is null)
            return null;

        // Only what changed. A slider drag is then one key per write, and a file that has
        // never had coordinates in it does not gain a latitude of zero for no reason.
        var values = new Dictionary<string, object>();
        if (settings.Mode != _current.Mode)
            values[ModeKey] = ModeName(settings.Mode);
        if (settings.Temperature != _current.Temperature)
            values[TemperatureKey] = (long)settings.Temperature;
        if (settings.UseTimeZone != _current.UseTimeZone)
            values[LocationKey] = settings.UseTimeZone ? LocationTimeZone : LocationManual;
        if (settings.Latitude != _current.Latitude)
            values[LatitudeKey] = settings.Latitude;
        if (settings.Longitude != _current.Longitude)
            values[LongitudeKey] = settings.Longitude;
        if (values.Count == 0)
            return null;

        var result = await _client.SetManyAsync(values).ConfigureAwait(false);
        _current = settings;
        return result.Advice;
    }

    public void Dispose()
    {
        if (_client is { } client)
        {
            client.Changed -= OnChangedElsewhere;
            client.Dispose();
        }
        _client = null;
    }

    private void OnChangedElsewhere(object? sender, SettingsChangedEventArgs e)
    {
        // The echo of our own write, which the daemon sends to everyone including us.
        if (e.Origin == _client?.UniqueName)
            return;
        var merged = Merge(_current, e.Values);
        if (merged == _current)
            return;
        _current = merged;
        Changed?.Invoke(merged);
    }

    /// <summary>
    /// <paramref name="settings"/> with whatever <paramref name="values"/> says about the night
    /// light laid over it. A key that is present but null was removed from the file, which puts
    /// it back to its default; one that is absent was not mentioned and is left alone.
    /// </summary>
    internal static NightLightSettings Merge(
        NightLightSettings settings,
        IReadOnlyDictionary<string, object?> values)
    {
        var defaults = NightLightSettings.Default;
        if (values.TryGetValue(ModeKey, out var mode))
            settings = settings with { Mode = ParseMode(mode as string) };
        if (values.TryGetValue(TemperatureKey, out var temperature))
            settings = settings with { Temperature = (int)(Number(temperature) ?? defaults.Temperature) };
        if (values.TryGetValue(LocationKey, out var location))
            settings = settings with { UseTimeZone = location as string != LocationManual };
        if (values.TryGetValue(LatitudeKey, out var latitude))
            settings = settings with { Latitude = Number(latitude) ?? defaults.Latitude };
        if (values.TryGetValue(LongitudeKey, out var longitude))
            settings = settings with { Longitude = Number(longitude) ?? defaults.Longitude };
        return settings;
    }

    internal static string ModeName(NightLightMode mode) => mode switch
    {
        NightLightMode.On => "on",
        NightLightMode.Auto => "auto",
        _ => "off",
    };

    private static NightLightMode ParseMode(string? name) => name switch
    {
        "on" => NightLightMode.On,
        "auto" => NightLightMode.Auto,
        _ => NightLightMode.Off,
    };

    // The daemon sends integers as int64 and fractions as double, and a whole-number latitude
    // somebody typed into the file by hand may arrive as either.
    private static double? Number(object? value) => value switch
    {
        long number => number,
        double number => number,
        int number => number,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };
}
