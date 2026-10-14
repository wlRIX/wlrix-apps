using Wlrix.Common.Localization;
using Wlrix.Settings.Login.Core;

namespace Wlrix.Settings.Login.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The static labels in the window come through <c>{loc:Tr}</c>, which reads the same
/// <see cref="Catalog"/>. What is here is what code has to build: the messages that carry a path
/// or the helper's own words, and the names of modes and orientations.
///
/// The layout, model and scheme names are deliberately absent: they come from xkeyboard-config
/// and from the theme, as they do in the Keyboard and Color Scheme panels.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.Login.Localization.Strings", typeof(Strings).Assembly);

    public static string HelpTitle => Catalog.Get("HelpTitle");
    public static string HelpText => Catalog.Get("HelpText");
    public static string ConfirmReset => Catalog.Get("ConfirmReset");
    public static string SystemDefault => Catalog.Get("SystemDefault");
    public static string ModeAutomatic => Catalog.Get("ModeAutomatic");
    public static string Applied => Catalog.Get("Applied");
    public static string ResetDone => Catalog.Get("ResetDone");
    public static string Copied => Catalog.Get("Copied");
    public static string NotAuthorized => Catalog.Get("NotAuthorized");
    public static string NoDisplays => Catalog.Get("NoDisplays");

    /// <summary>Said only when the login screen these settings are for is not the one in use.</summary>
    public static string InstallStatus(GreeterInstall install) => install switch
    {
        GreeterInstall.NotInstalled => Catalog.Get("GreeterNotInstalled"),
        GreeterInstall.NotEnabled => Catalog.Get("GreeterNotEnabled"),
        _ => string.Empty,
    };

    public static string SourceOwn(string path) => Catalog.Format("SourceOwn", path);
    public static string SourceSystem(string path) => Catalog.Format("SourceSystem", path);
    public static string ReadFailed(string path, string detail) => Catalog.Format("ReadFailed", path, detail);
    public static string Invalid(string detail) => Catalog.Format("Invalid", detail);
    public static string HelperUnavailable(string detail) => Catalog.Format("HelperUnavailable", detail);
    public static string HelperFailed(string detail) => Catalog.Format("HelperFailed", detail);

    /// <summary>There is no settings service to copy from; <paramref name="detail"/> is why.</summary>
    public static string NoSettingsService(string detail) => Catalog.Format("NoSettingsService", detail);

    /// <summary>A resolution and refresh, as a mode list shows it.</summary>
    public static string Mode(GreeterMode mode) => mode.RefreshHz is { } hz
        ? Catalog.Format("ModeWithRefresh", mode.Width, mode.Height, hz)
        : Catalog.Format("ModeFastest", mode.Width, mode.Height);

    /// <summary>An orientation, by the compositor's name for it.</summary>
    public static string Transform(string id) => id switch
    {
        "90" => Catalog.Get("Transform90"),
        "180" => Catalog.Get("Transform180"),
        "270" => Catalog.Get("Transform270"),
        "flipped" => Catalog.Get("TransformFlipped"),
        "flipped-90" => Catalog.Get("TransformFlipped90"),
        "flipped-180" => Catalog.Get("TransformFlipped180"),
        "flipped-270" => Catalog.Get("TransformFlipped270"),
        _ => Catalog.Get("TransformNormal"),
    };
}
