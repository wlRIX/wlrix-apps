using Wlrix.Common.Localization;

namespace Wlrix.Shutdown.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The static labels in the window come through <c>{loc:Tr}</c>, which reads the same
/// <see cref="Catalog"/>. What is here is what code has to build: the title, which has the
/// hostname in it, and the failure messages, which have the bus's own words in them.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Shutdown.Localization.Strings", typeof(Strings).Assembly);

    public static string ErrorTitle => Catalog.Get("ErrorTitle");
    public static string NoSystemBus => Catalog.Get("NoSystemBus");
    public static string PowerOffUnavailable => Catalog.Get("PowerOffUnavailable");
    public static string RestartUnavailable => Catalog.Get("RestartUnavailable");

    /// <summary>The window title, with this machine's hostname in it.</summary>
    public static string WindowTitle(string hostname) => Catalog.Format("WindowTitle", hostname);

    /// <summary>Powering off failed; <paramref name="detail"/> is what the bus said.</summary>
    public static string PowerOffFailed(string detail) => Catalog.Format("PowerOffFailed", detail);

    /// <summary>Restarting failed; <paramref name="detail"/> is what the bus said.</summary>
    public static string RestartFailed(string detail) => Catalog.Format("RestartFailed", detail);

    /// <summary>
    /// Arming the firmware-setup flag failed, so nothing was restarted. Restarting anyway would
    /// take the machine down and come back to the ordinary boot, which is not what was asked
    /// for and cannot be taken back.
    /// </summary>
    public static string FirmwareSetupFailed(string detail) => Catalog.Format("FirmwareSetupFailed", detail);
}
