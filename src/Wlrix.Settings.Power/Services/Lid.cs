// SPDX-License-Identifier: GPL-3.0-or-later
namespace Wlrix.Settings.Power.Services;

/// <summary>
/// Whether this computer has a lid, for the line the panel shows on a desktop.
///
/// ACPI lists a laptop's lid switch under <c>/proc/acpi/button/lid</c>, one directory per lid,
/// readable by anyone -- the same switch logind reads. A desktop has no such directory, or an
/// empty one.
/// </summary>
public static class Lid
{
    private const string AcpiLids = "/proc/acpi/button/lid";

    /// <summary>Whether ACPI knows of a lid. False when it cannot be read.</summary>
    public static bool Present() => Present(AcpiLids);

    internal static bool Present(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).Any();
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
