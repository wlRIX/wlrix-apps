using System.Text.RegularExpressions;

namespace Wlrix.Settings.International.Core;

/// <summary>One line of glibc's <c>SUPPORTED</c> list: a locale name and the charset it is built in.</summary>
/// <param name="Name">What <c>LANG</c> is set to, e.g. <c>ja_JP.UTF-8</c> or <c>sr_RS@latin</c>.</param>
/// <param name="Charset">The second column, e.g. <c>UTF-8</c>.</param>
public sealed record SupportedLocale(string Name, string Charset);

/// <summary>
/// Turning a locale on in <c>/etc/locale.gen</c>, which is what <c>locale-gen</c> reads.
/// </summary>
/// <remarks>
/// <para>
/// A locale has to be <em>generated</em> before a program can use it: <c>setlocale</c> silently
/// falls back to <c>C</c> for one that is not, so a language picked from the list but never built
/// would come up as English with no clue why. Arch ships every supported locale in
/// <c>locale.gen</c> commented out, and enabling one is removing a <c>#</c> and running
/// <c>locale-gen</c>.
/// </para>
/// <para>
/// The editing is here, as a pure function, so that it is tested; and here rather than in the
/// helper so the panel can validate a name with exactly the rule the helper will apply as root.
/// </para>
/// </remarks>
public static partial class LocaleGen
{
    /// <summary>glibc's list of every locale it can build.</summary>
    public const string SupportedPath = "/usr/share/i18n/SUPPORTED";

    /// <summary>The list <c>locale-gen</c> builds from.</summary>
    public const string LocaleGenPath = "/etc/locale.gen";

    /// <summary>
    /// The shape of a locale name: letters, digits and <c>_.@-</c>, nothing a shell or a path could
    /// make anything of. Being on the SUPPORTED list is the real test; this is the cheap one first.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.@-]{1,64}$")]
    private static partial Regex NameShape();

    /// <summary>Whether <paramref name="name"/> could be a locale name at all.</summary>
    public static bool IsWellFormed(string name) => NameShape().IsMatch(name);

    /// <summary>Every locale in a <c>SUPPORTED</c> file's text, in its order.</summary>
    public static IReadOnlyList<SupportedLocale> ParseSupported(string text)
    {
        var result = new List<SupportedLocale>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            // A header line in some builds: "SUPPORTED-LOCALES=\" and a trailing "\" per line.
            if (line.Length == 0 || line.StartsWith('#') || line.Contains('='))
                continue;
            line = line.TrimEnd('\\').Trim();
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !IsWellFormed(fields[0]))
                continue;
            // glibc writes the SUPPORTED entries as "name/charset" in its own tree and "name charset"
            // once installed; take either.
            var name = fields[0].Split('/')[0];
            result.Add(new SupportedLocale(name, fields[1]));
        }
        return result;
    }

    /// <summary>
    /// The text of <c>locale.gen</c> with <paramref name="locale"/> enabled: its commented-out line
    /// uncommented where there is one, or a line appended where there is not. Already enabled is
    /// no change at all.
    /// </summary>
    public static string Enable(string localeGen, SupportedLocale locale)
    {
        // Checked across the whole file first. Hand-enabled locales are often appended at the
        // bottom with the stock commented line left where it was, and uncommenting that one too
        // would list the locale twice.
        if (IsEnabled(localeGen, locale))
            return localeGen;

        var lines = localeGen.Split('\n').ToList();
        // A trailing newline splits into a last empty element; keep it out of the search and put
        // it back at the end.
        var trailingNewline = lines.Count > 0 && lines[^1].Length == 0;
        if (trailingNewline)
            lines.RemoveAt(lines.Count - 1);

        for (var i = 0; i < lines.Count; i++)
        {
            if (Matches(lines[i], locale, out var commented))
            {
                if (!commented)
                    return localeGen;
                lines[i] = $"{locale.Name} {locale.Charset}";
                return string.Join('\n', lines) + (trailingNewline ? "\n" : string.Empty);
            }
        }

        lines.Add($"{locale.Name} {locale.Charset}");
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>Whether <paramref name="localeGen"/> already has <paramref name="locale"/> enabled.</summary>
    public static bool IsEnabled(string localeGen, SupportedLocale locale) =>
        localeGen.Split('\n').Any(line => Matches(line, locale, out var commented) && !commented);

    private static bool Matches(string line, SupportedLocale locale, out bool commented)
    {
        var trimmed = line.Trim();
        commented = trimmed.StartsWith('#');
        var body = trimmed.TrimStart('#').Trim();
        var fields = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // Exactly two fields: the prose in locale.gen's header ("# Configuration file for ...")
        // is commented too, and must never be mistaken for a locale.
        return fields.Length == 2
               && string.Equals(fields[0], locale.Name, StringComparison.Ordinal)
               && string.Equals(fields[1], locale.Charset, StringComparison.OrdinalIgnoreCase);
    }
}
