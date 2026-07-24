namespace Wlrix.Settings.Keyboard.Services;

/// <summary>One selectable xkb entry: its code (what the config stores) and a human name.</summary>
public sealed record XkbEntry(string Code, string Description)
{
    /// <summary>What the combo box shows, e.g. "Japanese 106-key (jp106)".</summary>
    public string Display => $"{Description} ({Code})";
}

/// <summary>
/// The available keyboard layouts and models, read from xkeyboard-config's rules list
/// (<c>/usr/share/X11/xkb/rules/evdev.lst</c>) — the same catalog every X/Wayland desktop
/// draws these from. Falls back to a small built-in set if the list is not installed.
/// </summary>
public static class XkbCatalog
{
    private static readonly string[] RulesPaths =
    [
        "/usr/share/X11/xkb/rules/evdev.lst",
        "/usr/share/X11/xkb/rules/base.lst",
    ];

    public static (IReadOnlyList<XkbEntry> Layouts, IReadOnlyList<XkbEntry> Models) Load()
    {
        foreach (var path in RulesPaths)
        {
            if (!File.Exists(path))
                continue;
            try
            {
                var lines = File.ReadAllLines(path);
                var layouts = ParseSection(lines, "layout");
                var models = ParseSection(lines, "model");
                if (layouts.Count > 0 && models.Count > 0)
                    return (layouts, models);
            }
            catch (IOException)
            {
                // Try the next path, then the fallback.
            }
        }

        return (FallbackLayouts, FallbackModels);
    }

    // The .lst file groups entries under "! layout", "! model", etc.; each entry is a code
    // followed by whitespace and a description.
    private static List<XkbEntry> ParseSection(string[] lines, string section)
    {
        var result = new List<XkbEntry>();
        var inSection = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("! ", StringComparison.Ordinal))
            {
                inSection = line.AsSpan(2).Trim().SequenceEqual(section);
                continue;
            }

            if (!inSection)
                continue;

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            var split = 0;
            while (split < trimmed.Length && !char.IsWhiteSpace(trimmed[split]))
                split++;
            if (split == 0 || split == trimmed.Length)
                continue;

            var code = trimmed[..split];
            var description = trimmed[split..].TrimStart();
            result.Add(new XkbEntry(code, description));
        }

        result.Sort((a, b) => string.Compare(a.Description, b.Description, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static readonly IReadOnlyList<XkbEntry> FallbackLayouts =
    [
        new XkbEntry("us", "English (US)"),
        new XkbEntry("jp", "Japanese"),
    ];

    private static readonly IReadOnlyList<XkbEntry> FallbackModels =
    [
        new XkbEntry("pc104", "Generic 104-key PC"),
        new XkbEntry("pc105", "Generic 105-key PC"),
        new XkbEntry("jp106", "Japanese 106-key"),
    ];
}
