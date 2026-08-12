using Wlrix.Common.Localization;

namespace Wlrix.Toolchest.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Toolchest.Localization.Strings", typeof(Strings).Assembly);

    public static string Toolchest => Catalog.Get("Toolchest");
    public static string Desktop => Catalog.Get("Desktop");
    public static string Applications => Catalog.Get("Applications");
    public static string Help => Catalog.Get("Help");
    public static string OpenUnixShell => Catalog.Get("OpenUnixShell");
    public static string AboutToolchest => Catalog.Get("AboutToolchest");
    public static string Loading => Catalog.Get("Loading");

    /// <summary>The localized display name for a <see cref="Applications.MainCategory"/> id.</summary>
    public static string Category(string id) => Catalog.Get($"Category_{id}");

    /// <summary>The About dialog body, with the version substituted in.</summary>
    public static string AboutMessage(string version) => Catalog.Format("AboutMessage", version);
}
