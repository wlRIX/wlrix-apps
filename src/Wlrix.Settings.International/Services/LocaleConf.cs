namespace Wlrix.Settings.International.Services;

/// <summary>
/// The personal language: <c>~/.config/locale.conf</c>, the per-user file Arch's
/// <c>/etc/profile.d/locale.sh</c> and <c>start-wlrix</c> read.
/// </summary>
/// <remarks>
/// <para>
/// The same <c>KEY=value</c> lines as <c>/etc/locale.conf</c>, so one parser serves both. This panel
/// owns <c>LANG</c> and the five formats variables; any other line -- <c>LC_COLLATE</c>, say, or a
/// comment -- is somebody's hand-edit and is kept where it was.
/// </para>
/// <para>
/// The language is <c>LANG</c>; the formats are dates, numbers, money, paper size and units, set
/// together because nobody wants their dates German and their decimal point American. When the
/// formats are the language's own they are not written at all, so <c>LANG</c> alone decides them.
/// </para>
/// </remarks>
internal static class LocaleConf
{
    /// <summary>The variables the Formats choice sets.</summary>
    public static readonly IReadOnlyList<string> FormatVariables =
        ["LC_TIME", "LC_NUMERIC", "LC_MONETARY", "LC_PAPER", "LC_MEASUREMENT"];

    /// <summary>Where the personal file is: <c>$XDG_CONFIG_HOME/locale.conf</c>.</summary>
    public static string UserPath() =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
                ? config
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "locale.conf");

    /// <summary>The variables in a locale.conf, quotes removed. Later lines win, as when a shell sources it.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();
            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;
            values[line[..equals].Trim()] = line[(equals + 1)..].Trim().Trim('"', '\'');
        }
        return values;
    }

    /// <summary>
    /// <paramref name="existing"/> with the language and the formats set. <paramref name="formats"/>
    /// equal to <paramref name="language"/>, or null, removes the formats lines.
    /// </summary>
    public static string Write(string existing, string language, string? formats)
    {
        var owned = new HashSet<string>(FormatVariables.Prepend("LANG"), StringComparer.Ordinal);
        var kept = existing.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line =>
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("export ", StringComparison.Ordinal))
                    trimmed = trimmed[7..].TrimStart();
                var equals = trimmed.IndexOf('=');
                return equals <= 0 || trimmed.StartsWith('#') || !owned.Contains(trimmed[..equals].Trim());
            })
            .ToList();
        while (kept.Count > 0 && kept[^1].Trim().Length == 0)
            kept.RemoveAt(kept.Count - 1);

        var lines = new List<string> { $"LANG={language}" };
        if (formats is { Length: > 0 } && formats != language)
            lines.AddRange(FormatVariables.Select(variable => $"{variable}={formats}"));

        return string.Join('\n', lines.Concat(kept)) + "\n";
    }

    /// <summary>
    /// What the formats are, from a set of variables: the shared value of the five when they agree
    /// and differ from <c>LANG</c>, otherwise null for "the language's own".
    /// </summary>
    public static string? FormatsOf(IReadOnlyDictionary<string, string> values)
    {
        var set = FormatVariables
            .Select(variable => values.TryGetValue(variable, out var value) ? value : null)
            .ToList();
        if (set.Any(value => value is null) || set.Distinct().Count() != 1)
            return null;
        return values.TryGetValue("LANG", out var language) && language == set[0] ? null : set[0];
    }
}
