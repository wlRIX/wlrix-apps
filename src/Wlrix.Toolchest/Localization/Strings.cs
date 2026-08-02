using System.Globalization;
using System.Resources;

namespace Wlrix.Toolchest.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites). Values are resolved against <see cref="CultureInfo.CurrentUICulture"/>
/// — which .NET seeds from the system locale — so no designer/codegen file is needed.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager =
        new("Wlrix.Toolchest.Localization.Strings", typeof(Strings).Assembly);

    public static string Toolchest => Get("Toolchest");
    public static string Desktop => Get("Desktop");
    public static string Applications => Get("Applications");
    public static string Help => Get("Help");
    public static string OpenUnixShell => Get("OpenUnixShell");
    public static string AboutToolchest => Get("AboutToolchest");
    public static string Loading => Get("Loading");

    /// <summary>The localized display name for a <see cref="Applications.MainCategory"/> id.</summary>
    public static string Category(string id) => Get($"Category_{id}");

    /// <summary>The About dialog body, with the version substituted in.</summary>
    public static string AboutMessage(string version) =>
        string.Format(CultureInfo.CurrentCulture, Get("AboutMessage"), version);

    private static string Get(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
