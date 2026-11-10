namespace Wlrix.Settings.Displays.NightLight;

/// <summary>When the night light warms the screen.</summary>
public enum NightLightMode
{
    Off,
    On,
    /// <summary>From sunset to sunrise.</summary>
    Auto,
}

/// <summary>
/// The night light as the panel shows it: what <c>[night_light]</c> in <c>compositor.toml</c>
/// holds, in the shapes the controls want.
/// </summary>
/// <param name="Temperature">The night color temperature, in kelvin.</param>
/// <param name="UseTimeZone">
/// Whether sunset is worked out for the time zone's own coordinates rather than
/// <paramref name="Latitude"/> and <paramref name="Longitude"/>.
/// </param>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east.</param>
public sealed record NightLightSettings(
    NightLightMode Mode,
    int Temperature,
    bool UseTimeZone,
    double Latitude,
    double Longitude)
{
    /// <summary>What the compositor does when its config says nothing: off, at 4500 K.</summary>
    public static NightLightSettings Default { get; } = new(NightLightMode.Off, 4500, true, 0, 0);
}

/// <summary>What a store answers when it is first asked: the settings, and the slider's range.</summary>
public sealed record NightLightState(NightLightSettings Settings, int MinTemperature, int MaxTemperature);

/// <summary>
/// Where the night light's settings are kept.
///
/// The seam between the panel and the settings daemon, so the view model can be run against
/// something that is not a D-Bus service: <c>--demo</c>, the designer, and the tests.
/// </summary>
public interface INightLightStore : IDisposable
{
    /// <summary>
    /// Read the settings and start listening for changes. Throws when there is nothing to read
    /// them from; <see cref="NightLightUnsupportedException"/> when the settings service is
    /// running but is too old to know about the night light.
    /// </summary>
    Task<NightLightState> LoadAsync();

    /// <summary>
    /// Write the settings, which the compositor then picks up at once. Answers what a person
    /// should be told about it, or null when it simply applied.
    /// </summary>
    Task<string?> SaveAsync(NightLightSettings settings);

    /// <summary>
    /// The settings were changed by something else -- a hand-edited file, another program. May
    /// be raised on any thread.
    /// </summary>
    event Action<NightLightSettings>? Changed;
}

/// <summary>The settings service is there but has no night light settings.</summary>
public sealed class NightLightUnsupportedException() : Exception("the settings service has no night light settings");
