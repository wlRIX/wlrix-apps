using Wlrix.Common.Localization;

namespace Wlrix.Settings.Keyboard.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The static labels in the window come through <c>{loc:Tr}</c>, which reads the same
/// <see cref="Catalog"/>. What is here is what code has to build: the slider readouts, which
/// have a number in them, and the messages that carry the daemon's words.
///
/// The layout and model names are deliberately absent. They come from xkeyboard-config's own
/// rules list, which is the catalog every X and Wayland desktop reads and which carries its own
/// translations; see <see cref="Services.XkbCatalog"/>.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.Keyboard.Localization.Strings", typeof(Strings).Assembly);

    /// <summary>A duration in milliseconds, as the repeat-delay slider's readout shows it.</summary>
    public static string Milliseconds(int value) => Catalog.Format("Milliseconds", value);

    /// <summary>There is no settings service to write through; <paramref name="detail"/> is why.</summary>
    public static string NoSettingsService(string detail) =>
        Catalog.Format("NoSettingsService", detail);

    /// <summary>Somebody is halfway through hand-editing <paramref name="path"/>.</summary>
    public static string FileInvalid(string path) => Catalog.Format("FileInvalid", path);
}
