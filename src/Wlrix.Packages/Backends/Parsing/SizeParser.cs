using System.Globalization;

namespace Wlrix.Packages.Backends.Parsing;

/// <summary>
/// Sizes as package managers print them for people: <c>9.68 MiB</c>, <c>1024.00 KiB</c>,
/// <c>2.1 GB</c>. Every backend needs the same answer in kilobytes, so the conversion lives
/// here rather than three times over.
/// </summary>
internal static class SizeParser
{
    /// <summary>
    /// <paramref name="text"/> as kilobytes, or zero if it is not a size. Zero is the library's
    /// "the package manager did not say", which is why an unparseable value returns it rather
    /// than throwing: a size is a column in a list, never a reason to fail a listing.
    /// </summary>
    internal static long ToKilobytes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var span = text.AsSpan().Trim();
        var digits = 0;
        while (digits < span.Length && (char.IsAsciiDigit(span[digits]) || span[digits] is '.' or ','))
            digits++;

        if (digits == 0)
            return 0;

        // Package managers print in the C locale here (see ProcessRunner), so the decimal point
        // is a point and any comma is a thousands separator.
        var number = span[..digits].ToString().Replace(",", string.Empty);
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return 0;

        var unit = span[digits..].Trim();
        var multiplier = unit switch
        {
            // The binary units, which is what all three actually mean even when they write
            // "KB": pacman, dpkg and zypper all report powers of 1024.
            var u when u.Equals("B", StringComparison.OrdinalIgnoreCase) => 1d / 1024,
            var u when u.StartsWith("K", StringComparison.OrdinalIgnoreCase) => 1d,
            var u when u.StartsWith("M", StringComparison.OrdinalIgnoreCase) => 1024d,
            var u when u.StartsWith("G", StringComparison.OrdinalIgnoreCase) => 1024d * 1024,
            var u when u.StartsWith("T", StringComparison.OrdinalIgnoreCase) => 1024d * 1024 * 1024,
            // No unit at all: dpkg's Installed-Size field, which is documented as kilobytes.
            { Length: 0 } => 1d,
            _ => 0d,
        };

        var kilobytes = (long)Math.Round(value * multiplier);

        // A real size never reports as zero, because zero is this parser's "unknown". A package
        // of a few hundred bytes rounds down to nothing otherwise, and the Size column would
        // claim not to know a size it was just told.
        return kilobytes == 0 && value > 0 && multiplier > 0 ? 1 : kilobytes;
    }
}
