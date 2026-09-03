using Wlrix.Common.Localization;

namespace Wlrix.Settings.Schemes.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The static labels in the window come through <c>{loc:Tr}</c>, which reads the same
/// <see cref="Catalog"/>. What is here is what code has to build: the scheme description, which
/// has the scheme's own name and gamma in it, and the messages that carry the daemon's words.
///
/// The scheme *names* are deliberately absent. They come from
/// <c>WlrixSchemes.All</c>, generated into the theme from the palette JSON, and a copy of them
/// here would be a list to keep in step for no gain — "wlRIX Classic (gamma 1.7)" is a proper
/// noun and a number.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.Schemes.Localization.Strings", typeof(Strings).Assembly);

    public static string HelpTitle => Catalog.Get("HelpTitle");
    public static string HelpText => Catalog.Get("HelpText");
    public static string ComponentsDisagree => Catalog.Get("ComponentsDisagree");

    /// <summary>The line under the list: what the selected scheme is.</summary>
    public static string SchemeDescription(string name, bool dark, string gamma) =>
        Catalog.Format("SchemeDescription", name,
            Catalog.Get(dark ? "SchemeDark" : "SchemeLight"), gamma);

    /// <summary>There is no settings service to write through; <paramref name="detail"/> is why.</summary>
    public static string NoSettingsService(string detail) =>
        Catalog.Format("NoSettingsService", detail);

    /// <summary>Somebody is halfway through hand-editing <paramref name="path"/>.</summary>
    public static string FileInvalid(string path) => Catalog.Format("FileInvalid", path);
}
