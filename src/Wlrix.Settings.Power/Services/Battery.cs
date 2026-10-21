// SPDX-License-Identifier: GPL-3.0-or-later
namespace Wlrix.Settings.Power.Services;

/// <summary>
/// Whether this computer runs on a battery, for whether the On battery column is shown.
///
/// The kernel lists every power supply under <c>/sys/class/power_supply</c>. A laptop's own
/// battery is <c>type</c> <c>Battery</c>; a wireless mouse's is too, but its <c>scope</c> is
/// <c>Device</c>, and it does not run the computer.
/// </summary>
public static class Battery
{
    private const string Supplies = "/sys/class/power_supply";

    public static bool Present() => Present(Supplies);

    internal static bool Present(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).Any(dir =>
                Read(dir, "type") == "Battery" && Read(dir, "scope") != "Device");
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

    private static string Read(string dir, string name)
    {
        try
        {
            return File.ReadAllText(Path.Combine(dir, name)).Trim();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
