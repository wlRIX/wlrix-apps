using System.IO.Enumeration;

namespace Wlrix.Files.Core.Search;

/// <summary>What to look for.</summary>
/// <param name="Text">
/// A substring, or a glob when it contains a wildcard. Which of the two is decided by the text
/// itself rather than by a checkbox: somebody typing <c>*.cs</c> means a glob, and somebody
/// typing <c>report</c> means "has report in the name" — and asking them to say which is asking
/// them to know what a glob is.
/// </param>
/// <param name="MatchCase">Whether case matters. Off suits a person; on suits a script.</param>
/// <param name="IncludeHidden">Whether dotfiles and the directories under them are searched.</param>
/// <param name="MaxResults">
/// Where to stop. A search for <c>e</c> from the root of a filesystem matches millions of
/// files, and the honest answer to that is the first few thousand and a note saying so — not a
/// window that fills memory until it dies.
/// </param>
public readonly record struct SearchQuery(
    string Text,
    bool MatchCase = false,
    bool IncludeHidden = false,
    int MaxResults = 5000)
{
    /// <summary>Whether this query can match anything at all.</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Text);

    /// <summary>Whether the text is a glob rather than a substring.</summary>
    public bool IsGlob => Text.AsSpan().IndexOfAny('*', '?') >= 0;

    /// <summary>Whether <paramref name="name"/> is a hit.</summary>
    /// <remarks>
    /// A glob is anchored — <c>*.cs</c> means the name ends in <c>.cs</c>, not that it contains
    /// something ending in <c>.cs</c> — because that is what a glob means everywhere else on a
    /// Unix system, and a file manager that quietly meant something looser would be lying about
    /// a familiar notation.
    /// </remarks>
    public bool Matches(string name) =>
        IsGlob
            ? FileSystemName.MatchesSimpleExpression(Text, name, ignoreCase: !MatchCase)
            : name.Contains(
                Text,
                MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
