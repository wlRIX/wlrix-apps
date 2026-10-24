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
    public static string Find => Catalog.Get("Find");
    public static string IconCatalog => Catalog.Get("IconCatalog");
    public static string Help => Catalog.Get("Help");
    public static string Customize => Catalog.Get("Customize");
    public static string Windows => Catalog.Get("Windows");
    public static string Sounds => Catalog.Get("Sounds");
    public static string Background => Catalog.Get("Background");
    public static string ColorSchemes => Catalog.Get("ColorSchemes");
    public static string Language => Catalog.Get("Language");
    public static string Keyboard => Catalog.Get("Keyboard");
    public static string Mouse => Catalog.Get("Mouse");
    public static string ExtraDesks => Catalog.Get("ExtraDesks");
    public static string ControlAudio => Catalog.Get("ControlAudio");
    public static string ClipboardHistory => Catalog.Get("ClipboardHistory");
    public static string OpenTerminal => Catalog.Get("OpenTerminal");
    public static string LogOut => Catalog.Get("LogOut");
    public static string LogOutPrompt => Catalog.Get("LogOutPrompt");
    public static string ManageSoftware => Catalog.Get("ManageSoftware");
    public static string DisplayProperties => Catalog.Get("DisplayProperties");
    public static string NetworkProperties => Catalog.Get("NetworkProperties");
    public static string BluetoothProperties => Catalog.Get("BluetoothProperties");
    public static string PowerProperties => Catalog.Get("PowerProperties");
    public static string UserAccounts => Catalog.Get("UserAccounts");
    public static string LoginProperties => Catalog.Get("LoginProperties");
    public static string RestartSystem => Catalog.Get("RestartSystem");
    public static string ShutDownSystem => Catalog.Get("ShutDownSystem");
    public static string AboutToolchest => Catalog.Get("AboutToolchest");

    /// <summary>The About dialog body, with the version substituted in.</summary>
    public static string AboutMessage(string version) => Catalog.Format("AboutMessage", version);

    // What a failed launch says. These reach a MessageDialog, not just the log — see
    // AppLauncher.Fail, which does both.

    public static string NoTerminal => Catalog.Get("NoTerminal");

    /// <summary>The program a launcher names is not on the PATH.</summary>
    public static string NotInstalled(string program) => Catalog.Format("NotInstalled", program);

    /// <summary>Starting it threw; <paramref name="detail"/> is what the OS said.</summary>
    public static string CouldNotStart(string displayName, string detail) =>
        Catalog.Format("CouldNotStart", displayName, detail);
}
