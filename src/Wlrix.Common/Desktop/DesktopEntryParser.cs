using System.Globalization;
using System.Text;

namespace Wlrix.Common.Desktop;

/// <summary>
/// Parses a freedesktop <c>.desktop</c> file and resolves localized values against a
/// <see cref="CultureInfo"/>.
/// </summary>
/// <remarks>
/// Reads the <c>[Desktop Entry]</c> group and any <c>[Desktop Action <em>id</em>]</c>
/// groups it declares. The action groups are why this walks the whole file rather than
/// stopping at the first group boundary, which is all the Toolchest ever needed.
/// </remarks>
public static class DesktopEntryParser
{
    /// <summary>
    /// Parses <paramref name="lines"/> as a desktop entry. Returns <c>null</c> when the
    /// <c>[Desktop Entry]</c> group has no <c>Name</c> (nothing useful to show).
    /// </summary>
    /// <param name="id">The desktop-file id.</param>
    /// <param name="lines">The file's lines.</param>
    /// <param name="filePath">Where it came from, for <see cref="DesktopEntry.FilePath"/>.</param>
    public static DesktopEntry? Parse(string id, IEnumerable<string> lines, string? filePath = null)
    {
        var main = new Group();
        var actionGroups = new Dictionary<string, Group>(StringComparer.Ordinal);

        // null until [Desktop Entry] is seen, so keys loose at the top of the file --
        // which the spec forbids but files in the wild still have -- are ignored.
        Group? current = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line[0] == '[')
            {
                if (line == "[Desktop Entry]")
                {
                    current = main;
                }
                else if (line.StartsWith("[Desktop Action ", StringComparison.Ordinal) && line[^1] == ']')
                {
                    var actionId = line["[Desktop Action ".Length..^1].Trim();
                    // A duplicate group is a malformed file; the first wins, as it
                    // does for a duplicate key.
                    if (actionId.Length > 0 && !actionGroups.ContainsKey(actionId))
                        actionGroups[actionId] = current = new Group();
                    else
                        current = null;
                }
                else
                {
                    // Some other group -- an unknown extension, or a vendor's own.
                    current = null;
                }
                continue;
            }

            if (current is null)
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
                current.SetLocalized(key[..lb], key[(lb + 1)..^1], Unescape(value));
                continue;
            }

            current.Set(key, value);
        }

        if (main.Name.Count == 0)
            return null;

        // Order follows Actions=, not the order the groups happen to appear in, because
        // that is the order the application asked for them to be shown.
        var actions = new List<DesktopAction>();
        foreach (var actionId in main.List("Actions"))
        {
            if (!actionGroups.TryGetValue(actionId, out var group))
                continue;
            actions.Add(new DesktopAction(actionId, group.Name, group.Value("Exec"), group.Value("Icon")));
        }

        return new DesktopEntry
        {
            Id = id,
            Type = main.Value("Type"),
            Name = main.Name,
            Comment = main.Comment,
            Exec = main.Value("Exec"),
            TryExec = main.Value("TryExec"),
            Categories = main.List("Categories"),
            Terminal = main.Bool("Terminal"),
            NoDisplay = main.Bool("NoDisplay"),
            Hidden = main.Bool("Hidden"),
            OnlyShowIn = main.List("OnlyShowIn"),
            NotShowIn = main.List("NotShowIn"),
            Icon = main.Value("Icon"),
            MimeType = main.List("MimeType"),
            Keywords = main.Keywords,
            Path = main.Value("Path"),
            StartupWmClass = main.Value("StartupWMClass"),
            Actions = actions,
            FilePath = filePath
        };
    }

    /// <summary>The keys of one group, before they are given meaning.</summary>
    /// <remarks>
    /// A bag rather than a field per key, because the action groups take the same shape
    /// as the main one and duplicating the switch for them invites the two to drift.
    /// </remarks>
    private sealed class Group
    {
        private readonly Dictionary<string, string> _plain = new(StringComparer.Ordinal);

        public Dictionary<string, string> Name { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Comment { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Keywords { get; } = new(StringComparer.Ordinal);

        public void Set(string key, string value)
        {
            switch (key)
            {
                case "Name": Name[string.Empty] = Unescape(value); break;
                case "Comment": Comment[string.Empty] = Unescape(value); break;
                case "Keywords": Keywords[string.Empty] = Unescape(value); break;
                // The spec says the first occurrence of a duplicate key wins.
                default: _plain.TryAdd(key, value); break;
            }
        }

        public void SetLocalized(string key, string locale, string value)
        {
            switch (key)
            {
                case "Name": Name[locale] = value; break;
                case "Comment": Comment[locale] = value; break;
                case "Keywords": Keywords[locale] = value; break;
            }
        }

        public string? Value(string key) => _plain.GetValueOrDefault(key);

        public IReadOnlyList<string> List(string key) =>
            _plain.TryGetValue(key, out var value) ? SplitList(value) : [];

        public bool Bool(string key) => _plain.TryGetValue(key, out var value) && ParseBool(value);
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
