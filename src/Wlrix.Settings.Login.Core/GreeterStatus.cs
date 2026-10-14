using Wlrix.Common;

namespace Wlrix.Settings.Login.Core;

/// <summary>Whether the login screen these settings are for is the one this machine shows.</summary>
public enum GreeterInstall
{
    /// <summary><c>wlrix-greeter</c> is installed and is the display manager.</summary>
    Active,

    /// <summary>Installed, but another display manager (or none) is enabled.</summary>
    NotEnabled,

    /// <summary><c>wlrix-greeter</c> is not installed.</summary>
    NotInstalled,
}

/// <summary>What the panel says about the greeter before anything is changed.</summary>
public static class GreeterStatus
{
    /// <summary>
    /// The link <c>systemctl enable</c> makes for whichever unit claims
    /// <c>Alias=display-manager.service</c>; <c>wlrix-greeter.service</c> is one.
    /// </summary>
    private const string DisplayManagerLink = "/etc/systemd/system/display-manager.service";

    private const string GreeterUnit = "wlrix-greeter.service";

    public static GreeterInstall Install()
    {
        if (!Executables.Exists("wlrix-greeter"))
            return GreeterInstall.NotInstalled;
        return IsDisplayManager() ? GreeterInstall.Active : GreeterInstall.NotEnabled;
    }

    /// <summary>Whether the login screen has settings of its own, rather than the system defaults.</summary>
    public static bool HasOwnSettings => File.Exists(GreeterConfigFile.GreeterPath);

    private static bool IsDisplayManager()
    {
        try
        {
            var target = new FileInfo(DisplayManagerLink).ResolveLinkTarget(returnFinalTarget: true);
            return target is not null && Path.GetFileName(target.FullName) == GreeterUnit;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
