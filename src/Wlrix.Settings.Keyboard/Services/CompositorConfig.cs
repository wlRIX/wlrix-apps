using Wlrix.Settings.Keyboard.Models;

namespace Wlrix.Settings.Keyboard.Services;

/// <summary>
/// Reads and writes the compositor's <c>[keyboard]</c> settings in
/// <c>~/.config/wlrix/compositor.toml</c>.
///
/// Writing is surgical: only the four managed keys in the <c>[keyboard]</c> table are
/// rewritten. Every other table, comment and hand-set key — including <c>[[output]]</c>
/// blocks and a hand-set <c>options</c>/<c>variant</c> — is left exactly as the user wrote it.
/// That matters because <c>compositor.toml</c> is a file you edit by hand as well.
/// </summary>
public static class CompositorConfig
{
    private const string SectionHeader = "[keyboard]";

    /// <summary>The keys the app owns, in the order it writes them.</summary>
    private static readonly string[] ManagedKeys = ["layout", "model", "repeat_delay", "repeat_rate"];

    /// <summary>The user's config path, where changes are written.</summary>
    public static string UserConfigPath() =>
        Path.Combine(ConfigHome(), "wlrix", "compositor.toml");

    /// <summary>
    /// The current settings: the user's file if present, else the system file, else defaults.
    /// A missing key falls back to its default, so a partial section is fine.
    /// </summary>
    public static KeyboardSettings Read()
    {
        foreach (var path in ReadPaths())
        {
            if (!File.Exists(path))
                continue;
            try
            {
                return ParseKeyboard(File.ReadAllText(path));
            }
            catch (IOException)
            {
                // Fall through to the next candidate, then defaults.
            }
        }

        return new KeyboardSettings();
    }

    /// <summary>
    /// Write the settings into the user's <c>compositor.toml</c>, preserving everything else,
    /// then return. The file is replaced atomically. Creates the file (and its directory) with
    /// just a <c>[keyboard]</c> table if none exists yet.
    /// </summary>
    public static void Write(KeyboardSettings settings)
    {
        var path = UserConfigPath();
        var original = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var updated = ApplyKeyboard(original, settings);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, updated);
        File.Move(tmp, path, overwrite: true);
    }

    private static IEnumerable<string> ReadPaths()
    {
        yield return UserConfigPath();
        yield return "/etc/wlrix/compositor.toml";
    }

    // --- Pure helpers, unit-testable without touching the filesystem ---

    /// <summary>Parse the <c>[keyboard]</c> values out of a config's text.</summary>
    public static KeyboardSettings ParseKeyboard(string text)
    {
        var lines = SplitLines(text);
        var settings = new KeyboardSettings();
        if (FindSection(lines) is not { } range)
            return settings;
        var (start, end) = range;

        for (var i = start; i < end; i++)
        {
            if (!TryParseKeyValue(lines[i], out var key, out var value))
                continue;
            switch (key)
            {
                case "layout":
                    settings = settings with { Layout = Unquote(value) };
                    break;
                case "model":
                    settings = settings with { Model = Unquote(value) };
                    break;
                case "repeat_delay" when int.TryParse(value, out var delay):
                    settings = settings with { RepeatDelay = delay };
                    break;
                case "repeat_rate" when int.TryParse(value, out var rate):
                    settings = settings with { RepeatRate = rate };
                    break;
            }
        }

        return settings;
    }

    /// <summary>
    /// Return <paramref name="original"/> with the managed <c>[keyboard]</c> keys set to
    /// <paramref name="settings"/>, leaving all other content in place.
    /// </summary>
    public static string ApplyKeyboard(string original, KeyboardSettings settings)
    {
        var eol = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = new List<string>(SplitLines(original));

        var managed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["layout"] = Quote(settings.Layout),
            ["model"] = Quote(settings.Model),
            ["repeat_delay"] = settings.RepeatDelay.ToString(),
            ["repeat_rate"] = settings.RepeatRate.ToString(),
        };

        if (FindSection(lines) is { } range)
        {
            var (start, end) = range;
            // Rewrite the managed keys in place; leave comments and unmanaged keys as they are.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = start; i < end; i++)
            {
                if (TryParseKeyValue(lines[i], out var key, out _) && managed.ContainsKey(key))
                {
                    lines[i] = $"{key} = {managed[key]}";
                    seen.Add(key);
                }
            }

            // Append any managed key the section did not already have, at the section's end.
            var missing = ManagedKeys.Where(key => !seen.Contains(key))
                .Select(key => $"{key} = {managed[key]}");
            lines.InsertRange(end, missing);
        }
        else
        {
            // No [keyboard] table yet: add one, separated by a blank line from any existing body.
            if (lines.Count > 0 && lines[^1].Trim().Length != 0)
                lines.Add(string.Empty);
            lines.Add(SectionHeader);
            lines.AddRange(ManagedKeys.Select(key => $"{key} = {managed[key]}"));
        }

        return string.Join(eol, lines);
    }

    // The [keyboard] table body: [start, end). end is the next table header (or EOF).
    private static (int Start, int End)? FindSection(IReadOnlyList<string> lines)
    {
        var header = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim() == SectionHeader)
            {
                header = i;
                break;
            }
        }

        if (header < 0)
            return null;

        var end = lines.Count;
        for (var i = header + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('['))
            {
                end = i;
                break;
            }
        }

        return (header + 1, end);
    }

    // A bare `key = value` line (not a comment). Keys are simple identifiers, which is all the
    // [keyboard] table uses.
    private static bool TryParseKeyValue(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        if (line.TrimStart().StartsWith('#'))
            return false;
        var equals = line.IndexOf('=');
        if (equals < 0)
            return false;

        var keyPart = line[..equals].Trim();
        if (keyPart.Length == 0)
            return false;
        foreach (var c in keyPart)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }

        key = keyPart;
        value = StripComment(line[(equals + 1)..]).Trim();
        return true;
    }

    // Drop a trailing `# comment`, but not a '#' inside a quoted string.
    private static string StripComment(string value)
    {
        var inString = false;
        var quote = '"';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (inString)
            {
                if (c == quote)
                    inString = false;
            }
            else if (c is '"' or '\'')
            {
                inString = true;
                quote = c;
            }
            else if (c == '#')
            {
                return value[..i];
            }
        }

        return value;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string Quote(string value)
    {
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string ConfigHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg))
            return xdg;
        var home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        return Path.Combine(home, ".config");
    }
}
