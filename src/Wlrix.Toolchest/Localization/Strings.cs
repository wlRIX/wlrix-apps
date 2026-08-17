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
    public static string System => Catalog.Get("System");
    public static string Applications => Catalog.Get("Applications");
    public static string Help => Catalog.Get("Help");
    public static string ExtraDesks => Catalog.Get("ExtraDesks");
    public static string OpenTerminal => Catalog.Get("OpenTerminal");
    public static string LogOut => Catalog.Get("LogOut");
    public static string LogOutPrompt => Catalog.Get("LogOutPrompt");
    public static string SoftwareManager => Catalog.Get("SoftwareManager");
    public static string RestartSystem => Catalog.Get("RestartSystem");
    public static string ShutDownSystem => Catalog.Get("ShutDownSystem");
    public static string AboutToolchest => Catalog.Get("AboutToolchest");
    public static string Loading => Catalog.Get("Loading");

    /// <summary>The localized display name for a <see cref="Applications.MainCategory"/> id.</summary>
    public static string Category(string id) => Catalog.Get($"Category_{id}");

    /// <summary>The About dialog body, with the version substituted in.</summary>
    public static string AboutMessage(string version) => Catalog.Format("AboutMessage", version);
}
