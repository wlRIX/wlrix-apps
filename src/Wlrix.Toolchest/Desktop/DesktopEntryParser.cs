using System.Globalization;
using System.Text;

namespace Wlrix.Toolchest.Desktop;

/// <summary>
/// Parses the <c>[Desktop Entry]</c> group of a freedesktop <c>.desktop</c> file and resolves
/// localized values against a <see cref="CultureInfo"/>.
/// </summary>
public static class DesktopEntryParser
{
    /// <summary>
    /// Parses the first <c>[Desktop Entry]</c> group from <paramref name="lines"/>. Returns
    /// <c>null</c> when the group has no <c>Name</c> (nothing useful to show).
    /// </summary>
    public static DesktopEntry? Parse(string id, IEnumerable<string> lines)
    {
        var name = new Dictionary<string, string>(StringComparer.Ordinal);
        var comment = new Dictionary<string, string>(StringComparer.Ordinal);
        string? type = null, exec = null, tryExec = null;
        IReadOnlyList<string> categories = [], onlyShowIn = [], notShowIn = [];
        bool terminal = false, noDisplay = false, hidden = false;

        var started = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line[0] == '[')
            {
                if (started)
                    break; // reached the group after [Desktop Entry] — done
                started = line == "[Desktop Entry]";
                continue;
            }

            if (!started)
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].TrimEnd();
            var value = line[(eq + 1)..].TrimStart();

            // Localized key form: Name[ja] / Comment[sr@latin]
            var lb = key.IndexOf('[');
            if (lb > 0 && key[^1] == ']')
            {
                var baseKey = key[..lb];
                var locale = key[(lb + 1)..^1];
                if (baseKey == "Name")
                    name[locale] = Unescape(value);
                else if (baseKey == "Comment")
                    comment[locale] = Unescape(value);
                continue;
            }

            switch (key)
            {
                case "Name": name[string.Empty] = Unescape(value); break;
                case "Comment": comment[string.Empty] = Unescape(value); break;
                case "Type": type = value; break;
                case "Exec": exec = value; break;
                case "TryExec": tryExec = value; break;
                case "Categories": categories = SplitList(value); break;
                case "OnlyShowIn": onlyShowIn = SplitList(value); break;
                case "NotShowIn": notShowIn = SplitList(value); break;
                case "Terminal": terminal = ParseBool(value); break;
                case "NoDisplay": noDisplay = ParseBool(value); break;
                case "Hidden": hidden = ParseBool(value); break;
            }
        }

        if (name.Count == 0)
            return null;

        return new DesktopEntry
        {
            Id = id,
            Type = type,
            Name = name,
            Comment = comment,
            Exec = exec,
            TryExec = tryExec,
            Categories = categories,
            Terminal = terminal,
            NoDisplay = noDisplay,
            Hidden = hidden,
            OnlyShowIn = onlyShowIn,
            NotShowIn = notShowIn
        };
    }

    /// <summary>
    /// Picks the best localized value for <paramref name="culture"/> following the spec's
    /// fallback (<c>lang_COUNTRY → lang → unlocalized</c>).
    /// </summary>
    public static string ResolveLocalized(IReadOnlyDictionary<string, string> values, CultureInfo culture)
    {
        foreach (var key in LocaleKeys(culture))
            if (values.TryGetValue(key, out var localized))
                return localized;

        return values.TryGetValue(string.Empty, out var unlocalized) ? unlocalized : string.Empty;
    }

    // POSIX-style locale keys to try, most specific first (encoding/modifier omitted for brevity).
    private static IEnumerable<string> LocaleKeys(CultureInfo culture)
    {
        var lang = culture.TwoLetterISOLanguageName;
        if (string.IsNullOrEmpty(lang) || lang == "iv") // invariant
            yield break;

        if (!culture.IsNeutralCulture)
        {
            string? country = null;
            try
            {
                country = new RegionInfo(culture.Name).TwoLetterISORegionName;
            }
            catch (ArgumentException)
            {
                // No region for this culture — fall through to language-only.
            }

            if (!string.IsNullOrEmpty(country))
                yield return $"{lang}_{country}";
        }

        yield return lang;
    }

    private static IReadOnlyList<string> SplitList(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool ParseBool(string value) => value.Equals("true", StringComparison.Ordinal);

    // Unescapes the \s \n \t \r \\ sequences used in .desktop string values.
    private static string Unescape(string value)
    {
        if (!value.Contains('\\'))
            return value;

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }

            sb.Append(value[++i] switch
            {
                's' => ' ',
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '\\' => '\\',
                var other => other
            });
        }

        return sb.ToString();
    }
}
