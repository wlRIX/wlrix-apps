// SPDX-License-Identifier: GPL-3.0-or-later
namespace Wlrix.Settings.Mouse.Services;

/// <summary>
/// Whether this computer has a touchpad, for the line the Touchpad tab shows when it has none.
///
/// libinput's devices belong to the compositor, and the event nodes under <c>/dev/input</c> are
/// readable by root alone, so the answer comes from udev's database instead: each input device's
/// record in <c>/run/udev/data</c> is world-readable and carries <c>ID_INPUT_TOUCHPAD=1</c> for a
/// touchpad -- the same udev tag libinput itself goes by. Input devices are character major 13,
/// so their records are the <c>c13:*</c> files.
/// </summary>
public static class Touchpads
{
    private const string UdevData = "/run/udev/data";

    /// <summary>Whether udev knows of a touchpad. False when its database cannot be read.</summary>
    public static bool Present() => Present(UdevData);

    internal static bool Present(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "c13:*")
                .Any(path => IsTouchpad(ReadOrEmpty(path)));
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

    /// <summary>Whether one udev record is a touchpad's.</summary>
    internal static bool IsTouchpad(string record) =>
        record.Split('\n').Any(line => line.TrimEnd() == "E:ID_INPUT_TOUCHPAD=1");

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
