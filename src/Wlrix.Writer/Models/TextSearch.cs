// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;

namespace Wlrix.Writer.Models;

/// <summary>What to look for, as the Find dialog describes it.</summary>
/// <param name="Text">The search string, or the pattern when <paramref name="RegularExpression"/> is set.</param>
public sealed record SearchQuery(
    string Text,
    bool RegularExpression = false,
    bool CaseSensitive = false,
    bool WholeWord = false,
    bool Backward = false);

/// <summary>A match: where it is, and whether the search went past the end of the document to find it.</summary>
public readonly record struct SearchMatch(int Offset, int Length, bool Wrapped);

/// <summary>Raised for a regular expression that does not parse.</summary>
public sealed class InvalidPatternException(string message, Exception inner) : Exception(message, inner);

/// <summary>Finding the next occurrence of a query in a document's text.</summary>
/// <remarks>
/// <para>
/// Every query becomes a regular expression. A plain string is escaped first, which is the
/// simplest way to give both kinds the same Whole Word and Case Sensitive handling. Whole Word
/// means the match is not touching a letter, digit or underscore on either side. That is NEdit's
/// definition, and it also works for a pattern that begins or ends with punctuation, where
/// <c>\b</c> would not.
/// </para>
/// <para>
/// The search starts from the selection: forward from its end, backward from its start, so
/// searching again moves past the match just found instead of finding it a second time. When it
/// reaches the end of the document (or the start, going backward), it wraps around, as NEdit
/// does.
/// </para>
/// </remarks>
public static class TextSearch
{
    /// <summary>A limit on any one match attempt, so a pathological pattern cannot hang the window.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>Builds the expression a query searches with.</summary>
    /// <exception cref="InvalidPatternException">The pattern does not parse.</exception>
    public static Regex Compile(SearchQuery query)
    {
        var pattern = query.RegularExpression ? query.Text : Regex.Escape(query.Text);
        if (query.WholeWord)
            pattern = $@"(?<![\w])(?:{pattern})(?![\w])";

        // Multiline, so ^ and $ mean the start and end of a line, which is what someone searching
        // an editor means by them. CultureInvariant, so case folding does not change with the
        // user's locale.
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (!query.CaseSensitive)
            options |= RegexOptions.IgnoreCase;
        if (query.Backward)
            options |= RegexOptions.RightToLeft;

        try
        {
            return new Regex(pattern, options, Timeout);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidPatternException(ex.Message, ex);
        }
    }

    /// <summary>The next match after (or, backward, before) the selection, or null if there is none anywhere.</summary>
    /// <param name="text">The document's text.</param>
    /// <param name="selectionStart">Where the selection begins, or the caret if nothing is selected.</param>
    /// <param name="selectionEnd">Where the selection ends, or the caret if nothing is selected.</param>
    /// <param name="query">What to look for.</param>
    /// <exception cref="InvalidPatternException">The pattern does not parse.</exception>
    /// <exception cref="RegexMatchTimeoutException">The pattern took too long.</exception>
    public static SearchMatch? Find(string text, int selectionStart, int selectionEnd, SearchQuery query)
    {
        if (query.Text.Length == 0)
            return null;

        var regex = Compile(query);
        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        selectionEnd = Math.Clamp(selectionEnd, selectionStart, text.Length);

        // A right-to-left expression started at an offset looks for matches ending at or before
        // it, so the same two passes serve both directions.
        var (from, wrapFrom) = query.Backward ? (selectionStart, text.Length) : (selectionEnd, 0);

        if (Next(regex, text, from, selectionStart, selectionEnd) is { } found)
            return new SearchMatch(found.Index, found.Length, false);

        // Around the end. The match already selected counts this time: when it is the only one
        // in the document, searching again finds it again, rather than reporting it missing.
        var wrapped = regex.Match(text, wrapFrom);
        return wrapped.Success ? new SearchMatch(wrapped.Index, wrapped.Length, true) : null;
    }

    private static Match? Next(Regex regex, string text, int from, int selectionStart, int selectionEnd)
    {
        for (var match = regex.Match(text, from); match.Success; match = match.NextMatch())
        {
            if (!IsCurrent(match, selectionStart, selectionEnd))
                return match;
        }

        return null;
    }

    /// <summary>
    /// Whether a match is the one already selected, which searching again must step over: an
    /// empty match at the caret (a pattern such as <c>^</c>) would otherwise be found forever.
    /// </summary>
    private static bool IsCurrent(Match match, int selectionStart, int selectionEnd) =>
        match.Index == selectionStart && match.Index + match.Length == selectionEnd;
}
