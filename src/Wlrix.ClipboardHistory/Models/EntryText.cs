// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.ClipboardHistory.Models;

/// <summary>
/// How a text entry is shown in one row, and whether it matches a search.
/// </summary>
/// <remarks>
/// Kept apart from the view model so the rules are tested without a window.
/// </remarks>
internal static class EntryText
{
    /// <summary>
    /// The most of a line a row shows. Anything longer is cut here rather than left to the
    /// TextBlock's ellipsis, which would still lay out — and hold in memory as glyph runs —
    /// a whole minified file copied by accident.
    /// </summary>
    internal const int MaxPreviewLength = 200;

    /// <summary>
    /// The first line with anything on it, trimmed, and whether there is more after it.
    /// </summary>
    /// <remarks>
    /// "More" is what the ↵ in the row stands for, as in KDE's history: a second line, or a
    /// trailing line break, which matters when pasting into a terminal. Leading blank lines are
    /// skipped so that an entry starting with a newline does not show as an empty row.
    /// </remarks>
    internal static (string Line, bool More) Preview(string text)
    {
        var rest = text.AsSpan().TrimStart(['\r', '\n']);
        var end = rest.IndexOfAny('\r', '\n');
        var line = (end < 0 ? rest : rest[..end]).Trim();
        var more = end >= 0;
        if (line.Length > MaxPreviewLength)
        {
            line = line[..MaxPreviewLength];
            more = true;
        }

        return (line.ToString().Replace('\t', ' '), more);
    }

    /// <summary>Whether <paramref name="text"/> matches a search, ignoring case.</summary>
    /// <remarks>An empty or blank search matches everything.</remarks>
    internal static bool Matches(string text, string? search) =>
        string.IsNullOrWhiteSpace(search)
        || text.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase);
}
