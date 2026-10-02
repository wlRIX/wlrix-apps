// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace Wlrix.Clock;

/// <summary>
/// The date and time the tooltip shows, as two lines:
/// <code>
/// 2026年9月30日水曜日
/// GMT-4: 17:24:45
/// </code>
/// </summary>
/// <remarks>
/// The date is the culture's long date, weekday and all, so it reads the way the rest of the
/// system writes a date. The time is a fixed 24-hour <c>HH:mm:ss</c> after the zone, whatever the
/// culture: the zone label already says which clock this is.
/// </remarks>
internal static class ClockText
{
    /// <summary>Both lines for <paramref name="now"/>, in the current culture.</summary>
    public static string Format(DateTimeOffset now) => Format(now, CultureInfo.CurrentCulture);

    /// <summary>Both lines for <paramref name="now"/>, with the date in <paramref name="culture"/>.</summary>
    internal static string Format(DateTimeOffset now, CultureInfo culture) =>
        $"{now.ToString("D", culture)}\n{Zone(now.Offset)}: {now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The offset from GMT the way KDE writes a zone with no name: <c>GMT-4</c>,
    /// <c>GMT+5:30</c>. Minutes only appear when there are some.
    /// </summary>
    internal static string Zone(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var magnitude = offset.Duration();
        return magnitude.Minutes == 0
            ? $"GMT{sign}{magnitude.Hours}"
            : $"GMT{sign}{magnitude.Hours}:{magnitude.Minutes:00}";
    }
}
