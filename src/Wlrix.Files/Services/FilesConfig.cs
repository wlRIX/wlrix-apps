using Tomlyn;
using Tomlyn.Model;
using Wlrix.Files.Core.State;

namespace Wlrix.Files.Services;

/// <summary>
/// <c>files.toml</c>: the two settings that belong to the session rather than to a window.
/// </summary>
/// <remarks>
/// Everything else the file manager remembers is in its own JSON under
/// <c>$XDG_DATA_HOME/wlrix/files</c> — which tab was showing what, how wide the sidebar was.
/// That is where the windows were when they closed, not configuration, and putting it here
/// would mean declaring every field of it in the daemon's hand-kept schema forever.
///
/// <para>
/// Read here rather than over <c>Wlrix.Settings.Client</c>, and that is the convention of the
/// whole stack rather than a preference: every component reads its own file, the daemon is the
/// only thing that writes one, and nothing depends on the daemon running. It also has to be
/// this way — the daemon validates a candidate file by running <c>wlrix-files
/// --check-config</c> on it before renaming it into place, so the parser has to exist here
/// whatever the reader does.
/// </para>
///
/// <para>
/// <b>Unknown keys are rejected</b>, matching the <c>deny_unknown_fields</c> every Rust
/// component in wlRIX uses. That is what makes the daemon's validation gate mean something: a
/// settings panel writing a key this version does not know is told so, rather than having it
/// silently ignored until somebody wonders why the setting does nothing.
/// </para>
/// </remarks>
public sealed record FilesConfig
{
    /// <summary>What opening a folder does.</summary>
    public NavigationMode NavigationMode { get; init; } = NavigationMode.Modern;

    /// <summary>The icon theme the listing draws from. Empty means no named theme.</summary>
    public string IconTheme { get; init; } = "Adwaita";

    /// <summary>The file's name, under the wlRIX config directory.</summary>
    public const string FileName = "files.toml";

    /// <summary>
    /// Reads the file, falling back to defaults for anything it cannot use.
    /// </summary>
    /// <remarks>
    /// Never throws and never refuses to start. A file manager that would not open because a
    /// config file had a typo in it would be a worse answer than one that opens with the
    /// defaults and says so on stderr — which is what every other component here does, and why
    /// <see cref="Check"/> is a separate entry point rather than this one with a stricter mood.
    /// </remarks>
    public static FilesConfig Load(string? path = null)
    {
        path ??= DefaultPath();
        if (path is null || !File.Exists(path))
            return new FilesConfig();

        try
        {
            if (Parse(File.ReadAllText(path), out var config, out var problem))
                return config;

            Console.Error.WriteLine($"wlrix-files: {path}: {problem}; using defaults");
            return new FilesConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FilesConfig();
        }
    }

    /// <summary>
    /// Says whether a file would be accepted, for the daemon's validation gate.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Load"/>. That one warns and falls back, which is right at
    /// startup and wrong here: the daemon is asking "would you accept this?", and an answer of
    /// "yes, and I would ignore half of it" is what the gate exists to prevent. Same split as
    /// <c>wlrix-desktop</c>'s <c>config::check</c> against its <c>load</c>.
    /// </remarks>
    public static string? Check(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not read {path}: {ex.Message}";
        }

        return Parse(text, out _, out var problem) ? null : problem;
    }

    /// <summary>Parses the text, answering false with a reason rather than throwing.</summary>
    internal static bool Parse(string text, out FilesConfig config, out string? problem)
    {
        config = new FilesConfig();
        problem = null;

        TomlTable? document;
        try
        {
            document = TomlSerializer.Deserialize<TomlTable>(text);
        }
        catch (TomlException ex)
        {
            // Tomlyn's message carries the line and column, which is most of what makes a
            // config error actionable. The first line of it: the rest is a stack of syntax
            // productions that means nothing to whoever mistyped a key.
            problem = ex.Message.Split('\n')[0].Trim();
            return false;
        }

        if (document is null)
        {
            problem = "not valid TOML";
            return false;
        }

        var mode = NavigationMode.Modern;
        var theme = "Adwaita";

        foreach (var pair in document)
        {
            var key = pair.Key;
            var value = pair.Value;
            switch (key)
            {
                case "navigation" when value is TomlTable navigation:
                    if (!Section(navigation, "navigation", ["mode"], out problem))
                        return false;
                    if (navigation.TryGetValue("mode", out var raw))
                    {
                        if (raw is not string text2 || !Enum.TryParse(text2, ignoreCase: true, out mode))
                        {
                            problem = $"navigation.mode: expected \"modern\" or \"classic\", got {Describe(raw)}";
                            return false;
                        }
                    }
                    break;

                case "appearance" when value is TomlTable appearance:
                    if (!Section(appearance, "appearance", ["icon_theme"], out problem))
                        return false;
                    if (appearance.TryGetValue("icon_theme", out var themeValue))
                    {
                        if (themeValue is not string named)
                        {
                            problem = $"appearance.icon_theme: expected a string, got {Describe(themeValue)}";
                            return false;
                        }
                        theme = named;
                    }
                    break;

                default:
                    problem = $"unknown key \"{key}\"";
                    return false;
            }
        }

        config = new FilesConfig { NavigationMode = mode, IconTheme = theme };
        return true;
    }

    /// <summary>Rejects a key the section does not have.</summary>
    private static bool Section(TomlTable table, string name, string[] known, out string? problem)
    {
        foreach (var key in table.Keys)
        {
            if (!known.Contains(key, StringComparer.Ordinal))
            {
                problem = $"unknown key \"{name}.{key}\"";
                return false;
            }
        }

        problem = null;
        return true;
    }

    /// <summary>What a wrong value was, for a message somebody can act on.</summary>
    private static string Describe(object? value) => value switch
    {
        null => "nothing",
        string text => $"\"{text}\"",
        TomlTable => "a table",
        TomlArray => "an array",
        _ => value.ToString() ?? "something else"
    };

    /// <summary>
    /// Where the file is: the user's, then the system's, first one winning outright.
    /// </summary>
    /// <remarks>
    /// The whole stack's convention, and this is one more copy of it rather than a new rule —
    /// the repos build standalone, so <c>wlrix-compositor</c>, <c>wlrix-desktop</c> and the
    /// settings daemon each carry their own. Nothing is merged: a user file holding one key
    /// discards every system default, which is why the daemon seeds a new user file from the
    /// system one before writing to it.
    /// </remarks>
    public static string? DefaultPath()
    {
        foreach (var directory in SearchPaths())
        {
            var candidate = Path.Combine(directory, FileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>The directories <c>files.toml</c> may live in, user first.</summary>
    public static IEnumerable<string> SearchPaths()
    {
        var home = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(home))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
                home = Path.Combine(profile, ".config");
        }
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, "wlrix");

        yield return "/etc/wlrix";
    }
}
