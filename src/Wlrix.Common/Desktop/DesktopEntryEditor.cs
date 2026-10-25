using System.Text;

namespace Wlrix.Common.Desktop;

/// <summary>
/// Changes keys in a <c>.desktop</c> file's <c>[Desktop Entry]</c> group and leaves every other
/// line as it was.
/// </summary>
/// <remarks>
/// Line by line rather than parse-and-rewrite: a file carries comments, translations into
/// dozens of languages, <c>[Desktop Action …]</c> groups and keys from other desktops, and an
/// edit to one launch option has no business reordering or dropping any of them. A key that is
/// already there is changed where it stands; a new one goes at the end of the group.
/// </remarks>
public sealed class DesktopEntryEditor
{
    private const string MainGroup = "[Desktop Entry]";

    private readonly List<string> _lines;

    public DesktopEntryEditor(IEnumerable<string> lines) => _lines = [.. lines];

    /// <summary>The file as edited so far.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>
    /// Sets <paramref name="key"/> (with <paramref name="locale"/>, for a localized one) to a
    /// value already in the file's form, or removes it when <paramref name="raw"/> is null.
    /// </summary>
    /// <remarks>
    /// Raw, because <c>Exec</c> is: its quoting is its own and nothing reads it back through the
    /// string escapes. Use <see cref="SetString"/> for an ordinary text value such as a name.
    /// </remarks>
    public void SetRaw(string key, string? raw, string locale = "")
    {
        var name = locale.Length == 0 ? key : $"{key}[{locale}]";
        var (start, end) = GroupBounds();
        if (start < 0)
        {
            if (raw is null)
                return;
            // No [Desktop Entry] at all is not a desktop entry; give it one rather than fail.
            _lines.Insert(0, MainGroup);
            (start, end) = (0, 1);
        }

        for (var i = start + 1; i < end; i++)
        {
            if (KeyOf(_lines[i]) != name)
                continue;
            if (raw is null)
                _lines.RemoveAt(i);
            else
                _lines[i] = $"{name}={raw}";
            return;
        }

        if (raw is null)
            return;
        // After the group's last key, not before the blank lines and comments that separate it
        // from the next group.
        var at = end;
        while (at > start + 1 && KeyOf(_lines[at - 1]) is null)
            at--;
        _lines.Insert(at, $"{name}={raw}");
    }

    /// <summary><see cref="SetRaw"/> for a string value, escaped as the spec requires.</summary>
    public void SetString(string key, string? value, string locale = "") =>
        SetRaw(key, value is null ? null : Escape(value), locale);

    /// <summary>The spec's escapes for a string value: backslash, newline, tab, carriage return.</summary>
    public static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            escaped.Append(c switch
            {
                '\\' => @"\\",
                '\n' => @"\n",
                '\t' => @"\t",
                '\r' => @"\r",
                _ => c.ToString(),
            });
        }
        // A leading space would be trimmed by every reader; \s is how the spec keeps one.
        return escaped.Length > 0 && escaped[0] == ' ' ? @"\s" + escaped.ToString(1, escaped.Length - 1) : escaped.ToString();
    }

    // The [Desktop Entry] header's line, and the line after the group's last.
    private (int Start, int End) GroupBounds()
    {
        var start = _lines.FindIndex(line => line.Trim() == MainGroup);
        if (start < 0)
            return (-1, -1);
        var end = start + 1;
        while (end < _lines.Count && !_lines[end].TrimStart().StartsWith('['))
            end++;
        return (start, end);
    }

    // The key a line sets, or null for a blank line or a comment.
    private static string? KeyOf(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#')
            return null;
        var equals = trimmed.IndexOf('=');
        return equals <= 0 ? null : trimmed[..equals].TrimEnd();
    }
}
