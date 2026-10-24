using System.Globalization;

namespace Wlrix.Settings.International.Services;

/// <summary>One time zone, where its main city is, and which country it belongs to.</summary>
/// <param name="Id">The tzdata name timedated takes, e.g. <c>Asia/Tokyo</c>.</param>
/// <param name="Country">The ISO 3166 code of the country it is listed under.</param>
/// <param name="Latitude">Degrees north; negative is south.</param>
/// <param name="Longitude">Degrees east; negative is west.</param>
/// <param name="Comment">tzdata's note on which part of the country it covers, often empty.</param>
public sealed record TimeZoneEntry(string Id, string Country, double Latitude, double Longitude, string Comment)
{
    /// <summary>The zone as a list shows it: <c>America/New York</c>, with the note when there is one.</summary>
    public string Display => Comment.Length > 0
        ? $"{Id.Replace('_', ' ')} ({Comment})"
        : Id.Replace('_', ' ');

    /// <summary>
    /// Where this zone is on an equirectangular world map <paramref name="width"/> by
    /// <paramref name="height"/> pixels, longitude -180 at the left and latitude 90 at the top.
    /// </summary>
    public (double X, double Y) MapPoint(double width, double height) =>
        ((Longitude + 180) / 360 * width, (90 - Latitude) / 180 * height);
}

/// <summary>
/// The time zones, from tzdata's <c>zone.tab</c>.
/// </summary>
/// <remarks>
/// <c>zone.tab</c> rather than the newer <c>zone1970.tab</c>: the newer one merges zones that have
/// agreed since 1970 and lists several countries per line ("JP,AU" for Asia/Tokyo), which is right
/// for a clock and wrong for "the time zones in this country". <c>zone.tab</c> lists each country's
/// own zones, its most populous first, which is also what makes the first one a sensible default.
/// </remarks>
internal static class TimeZones
{
    public const string ZoneTabPath = "/usr/share/zoneinfo/zone.tab";

    public static IReadOnlyList<TimeZoneEntry> Load()
    {
        try
        {
            return Parse(File.ReadAllText(ZoneTabPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Every line of a <c>zone.tab</c>, in file order.</summary>
    public static IReadOnlyList<TimeZoneEntry> Parse(string text)
    {
        var result = new List<TimeZoneEntry>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var fields = line.Split('\t');
            if (fields.Length < 3 || !TryParseCoordinates(fields[1], out var latitude, out var longitude))
                continue;
            result.Add(new TimeZoneEntry(fields[2].Trim(), fields[0], latitude, longitude,
                fields.Length > 3 ? fields[3].Trim() : string.Empty));
        }
        return result;
    }

    /// <summary>
    /// ISO 6709 as tzdata writes it: <c>±DDMM±DDDMM</c> or <c>±DDMMSS±DDDMMSS</c>, latitude first.
    /// </summary>
    public static bool TryParseCoordinates(string text, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        if (text.Length < 2)
            return false;
        // The longitude starts at the second sign.
        var split = text.IndexOfAny(['+', '-'], 1);
        if (split < 0)
            return false;
        return TryParseAngle(text[..split], degreeDigits: 2, out latitude)
               && TryParseAngle(text[split..], degreeDigits: 3, out longitude);
    }

    private static bool TryParseAngle(string text, int degreeDigits, out double value)
    {
        value = 0;
        if (text.Length < 1 + degreeDigits + 2 || text[0] is not ('+' or '-'))
            return false;
        var digits = text[1..];
        if (!digits.All(char.IsAsciiDigit) || (digits.Length != degreeDigits + 2 && digits.Length != degreeDigits + 4))
            return false;

        var degrees = int.Parse(digits[..degreeDigits], CultureInfo.InvariantCulture);
        var minutes = int.Parse(digits.Substring(degreeDigits, 2), CultureInfo.InvariantCulture);
        var seconds = digits.Length > degreeDigits + 2
            ? int.Parse(digits[(degreeDigits + 2)..], CultureInfo.InvariantCulture)
            : 0;
        value = (degrees + minutes / 60.0 + seconds / 3600.0) * (text[0] == '-' ? -1 : 1);
        return true;
    }
}
