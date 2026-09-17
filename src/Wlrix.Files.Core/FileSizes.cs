using System.Globalization;

namespace Wlrix.Files.Core;

/// <summary>How a file's size is written out.</summary>
/// <remarks>
/// Here rather than in a view model because two applications show the same listing — the file
/// manager and the file chooser — and a size that read differently in the two would look like
/// one of them was wrong. <see cref="Platform.StorageDevices.FormatSize"/> is the deliberate
/// exception and uses powers of ten, because that is what is printed on a disk.
/// </remarks>
public static class FileSizes
{
    /// <summary>
    /// IRIX-style size text: whole bytes below a kibibyte, one decimal above.
    /// </summary>
    /// <remarks>
    /// Choosing the unit by magnitude alone is not enough, because the rounding happens
    /// afterwards: 1,048,575 bytes divides to 1023.999 KiB, which stops the loop and then
    /// prints as "1024.0 KiB". The second step below carries those cases up a unit, so a
    /// sorted listing never shows a value of 1024 in the smaller unit next to 1.0 in the
    /// larger one.
    /// </remarks>
    public static string Binary(long bytes)
    {
        if (bytes < 1024)
            return string.Format(CultureInfo.CurrentCulture, "{0} B", bytes);

        string[] units = ["KiB", "MiB", "GiB", "TiB", "PiB"];
        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Rounded to the one decimal place we print, the value can still reach 1024.
        if (unit < units.Length - 1 && Math.Round(value, 1) >= 1024)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.0} {1}", value, units[unit]);
    }
}
