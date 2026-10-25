namespace Wlrix.Common.Desktop;

/// <summary>
/// The preferred terminal, as the xdg-terminal-exec specification stores it.
/// </summary>
/// <remarks>
/// The file is a list of desktop entry ids, best first, in <c>xdg-terminals.list</c> and its
/// desktop-specific twin <c>&lt;desktop&gt;-xdg-terminals.list</c>, looked for in
/// <c>$XDG_CONFIG_HOME</c> and then each of <c>$XDG_CONFIG_DIRS</c>. The first file found is
/// the one that counts. The rest of the session reads the same files, and so does
/// <c>xdg-terminal-exec</c> for anything not ours.
///
/// <para>
/// A line may carry a <c>+</c> or <c>-</c> before the id and a <c>:action</c> after it. Only
/// the bare id matters here: choosing a terminal puts its id on top, and every other line,
/// comments too, stays as it was.
/// </para>
/// </remarks>
public sealed class TerminalList(string configHome, IReadOnlyList<string> configDirs, IReadOnlyList<string> desktops)
{
    /// <summary>The session's, from the environment.</summary>
    public static TerminalList ForSession() =>
        new(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
                ? config
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            Split(Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS"), "/etc/xdg"),
            Split(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), string.Empty));

    /// <summary>The files to try, most important first.</summary>
    public IReadOnlyList<string> Paths =>
        [.. new[] { configHome }.Concat(configDirs).SelectMany(dir => Names.Select(name => Path.Combine(dir, name)))];

    /// <summary>The user's file a choice is written to.</summary>
    /// <remarks>
    /// The desktop-specific one if the user already has one, since that is what the readers
    /// would find first; otherwise the plain one, which other desktops' readers honor too.
    /// </remarks>
    public string UserPath =>
        Names.Select(name => Path.Combine(configHome, name)).FirstOrDefault(File.Exists)
        ?? Path.Combine(configHome, "xdg-terminals.list");

    private IEnumerable<string> Names =>
        desktops.Select(static desktop => $"{desktop.ToLowerInvariant()}-xdg-terminals.list").Append("xdg-terminals.list");

    /// <summary>The preferred terminal's entry id, or null when no file names one.</summary>
    public string? Preferred() => PreferredIds().FirstOrDefault();

    /// <summary>Every entry the files name, best first, for a caller that skips uninstalled ones.</summary>
    public IEnumerable<string> PreferredIds() =>
        Paths.SelectMany(path => Ids(ReadLines(path))).Distinct(StringComparer.Ordinal);

    /// <summary>The entries a file's lines name, in order, without the ones it excludes.</summary>
    public static IEnumerable<string> Ids(IEnumerable<string> lines) =>
        lines.Select(Parse).Where(static line => line is { Excluded: false }).Select(static line => line!.Value.Id);

    /// <summary>Puts <paramref name="id"/> first in the user's file.</summary>
    public void SetPreferred(string id)
    {
        var path = UserPath;
        var lines = WithPreferred(ReadLines(path), id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".wlrix-new";
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>A file's lines with <paramref name="id"/> moved to the top of the list.</summary>
    /// <remarks>
    /// Above the first entry rather than at line one, so a comment that heads the file stays
    /// at its head.
    /// </remarks>
    public static IReadOnlyList<string> WithPreferred(IEnumerable<string> lines, string id)
    {
        var kept = lines.Where(line => Parse(line)?.Id != id).ToList();
        var first = kept.FindIndex(static line => Parse(line) is not null);
        kept.Insert(first < 0 ? kept.Count : first, id);
        return kept;
    }

    private static (string Id, bool Excluded)? Parse(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text[0] == '#')
            return null;

        var excluded = text[0] == '-';
        if (text[0] is '+' or '-')
            text = text[1..];
        var colon = text.IndexOf(':');
        if (colon >= 0)
            text = text[..colon];
        return text.Length == 0 ? null : (text, excluded);
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] Split(string? value, string fallback) =>
        (string.IsNullOrEmpty(value) ? fallback : value).Split(':', StringSplitOptions.RemoveEmptyEntries);
}
