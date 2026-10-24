using Wlrix.Common.Localization;

namespace Wlrix.Settings.Background.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The static labels in the window come through <c>{loc:Tr}</c>, which reads the same
/// <see cref="Catalog"/>. What is here is what code has to build.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.Background.Localization.Strings", typeof(Strings).Assembly);

    public static string HelpTitle => Catalog.Get("HelpTitle");
    public static string HelpText => Catalog.Get("HelpText");
    public static string NoPicture => Catalog.Get("NoPicture");
    public static string AddTitle => Catalog.Get("AddTitle");
    public static string FilterImages => Catalog.Get("FilterImages");
    public static string ColorTitle => Catalog.Get("ColorTitle");

    /// <summary>
    /// A fit mode's label. The daemon's English label when this catalog has none for the value,
    /// so a mode added to <c>wlrix-bg</c> later still gets a readable entry in the list.
    /// </summary>
    public static string Mode(string value, string fallback)
    {
        var key = "Mode_" + value;
        var label = Catalog.Get(key);
        return label == key ? fallback : label;
    }

    /// <summary>A tile for a picture the config names but which is not in any wallpaper folder.</summary>
    public static string Elsewhere(string name) => Catalog.Format("Elsewhere", name);

    /// <summary>Adding <paramref name="name"/> failed; <paramref name="detail"/> is why.</summary>
    public static string AddFailed(string name, string detail) => Catalog.Format("AddFailed", name, detail);

    /// <summary>Removing <paramref name="name"/> failed; <paramref name="detail"/> is why.</summary>
    public static string RemoveFailed(string name, string detail) => Catalog.Format("RemoveFailed", name, detail);

    /// <summary>A dropped or picked file the desktop cannot show.</summary>
    public static string Unsupported(string name) => Catalog.Format("Unsupported", name);

    /// <summary>There is no settings service to write through; <paramref name="detail"/> is why.</summary>
    public static string NoSettingsService(string detail) =>
        Catalog.Format("NoSettingsService", detail);

    /// <summary>Somebody is halfway through hand-editing <paramref name="path"/>.</summary>
    public static string FileInvalid(string path) => Catalog.Format("FileInvalid", path);
}
