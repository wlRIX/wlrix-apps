namespace Wlrix.Packages.Backends.Parsing;

/// <summary>
/// The <c>Key : Value</c> block format several package managers print for
/// <c>pacman -Qi</c>, <c>pacman -Si</c> and <c>zypper info</c>: left-aligned keys, values
/// aligned to a column, long values wrapped onto indented continuation lines, and a blank line
/// between one package and the next.
/// </summary>
internal static class FieldBlocks
{
    /// <summary>
    /// Splits <paramref name="output"/> into one dictionary per package, in the order printed.
    /// Keys are trimmed; a repeated key within a block keeps the first, which is what
    /// <c>pacman -Qi</c> wants — it prints <c>Installed From</c> ahead of <c>Name</c> on some
    /// configurations, but never repeats a field that matters.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyDictionary<string, string>> Parse(string output)
    {
        var blocks = new List<IReadOnlyDictionary<string, string>>();
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        string? lastKey = null;

        foreach (var line in output.Split('\n'))
        {
            var text = line.TrimEnd('\r');

            if (text.Trim().Length == 0)
            {
                // Blank line: end of a package.
                if (current.Count > 0)
                {
                    blocks.Add(current);
                    current = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                lastKey = null;
                continue;
            }

            // A continuation is indented and belongs to whatever field came before it. Values
            // are joined with a single space: every wrapped field these tools produce is a
            // space-separated list or a sentence, and preserving the wrap column would put the
            // formatting of an 80-column terminal into the data.
            if (char.IsWhiteSpace(text[0]))
            {
                if (lastKey is not null)
                    current[lastKey] = $"{current[lastKey]} {text.Trim()}".Trim();
                continue;
            }

            // Keys never contain " : ", and values often contain ":" (a URL, a timestamp), so
            // the separator is the spaced one and only its first occurrence counts.
            var separator = text.IndexOf(" : ", StringComparison.Ordinal);
            if (separator < 0)
            {
                // A trailing "Key :" with an empty value still declares the field.
                if (text.TrimEnd().EndsWith(':') || text.TrimEnd().EndsWith(" :"))
                {
                    lastKey = text.TrimEnd().TrimEnd(':').Trim();
                    current.TryAdd(lastKey, string.Empty);
                }

                continue;
            }

            lastKey = text[..separator].Trim();
            current.TryAdd(lastKey, text[(separator + 3)..].Trim());
        }

        if (current.Count > 0)
            blocks.Add(current);

        return blocks;
    }

    /// <summary>
    /// The value for <paramref name="key"/>, with the package managers' several spellings of
    /// "nothing" flattened to an empty string.
    /// </summary>
    internal static string Value(this IReadOnlyDictionary<string, string> block, string key)
    {
        var value = block.GetValueOrDefault(key, string.Empty);
        return value is "None" or "(none)" or "none" ? string.Empty : value;
    }

    /// <summary>A space-separated list field, as an array with the "nothing" markers dropped.</summary>
    internal static IReadOnlyList<string> List(this IReadOnlyDictionary<string, string> block, string key)
    {
        var value = block.Value(key);
        return value.Length == 0
            ? []
            : value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
