namespace Wlrix.Files.Core.Mime;

/// <summary>One line of <c>globs2</c>: a pattern, the type it names, and how much it counts.</summary>
/// <param name="Weight">Higher wins a tie. 50 is the default the spec assumes.</param>
/// <param name="MimeType">The type this pattern names.</param>
/// <param name="Pattern">The glob, e.g. <c>*.txt</c> or a literal name like <c>Makefile</c>.</param>
/// <param name="CaseSensitive">
/// Set by the <c>cs</c> flag. Rare and load-bearing where it appears: <c>*.gs</c> is Genie
/// source, but a case-insensitive match would also claim <c>.GS</c>.
/// </param>
internal readonly record struct MimeGlob(int Weight, string MimeType, string Pattern, bool CaseSensitive)
{
    /// <summary>
    /// The kind of pattern this is, which decides the order it is consulted in.
    /// </summary>
    /// <remarks>
    /// The spec's precedence is literal, then extension, then anything else — not simply
    /// "whatever matches". <c>core</c> is a literal naming a core dump, and it must not be
    /// beaten by some <c>*e</c> pattern that also happens to match.
    /// </remarks>
    public MimeGlobKind Kind
    {
        get
        {
            if (Pattern.StartsWith("*.", StringComparison.Ordinal)
                && Pattern.AsSpan(2).IndexOfAny('*', '?', '[') < 0)
                return MimeGlobKind.Extension;
            return Pattern.AsSpanIndexOfAnyWildcard() < 0 ? MimeGlobKind.Literal : MimeGlobKind.Other;
        }
    }

    /// <summary>The suffix an <see cref="MimeGlobKind.Extension"/> pattern matches, including the dot.</summary>
    public string Suffix => Pattern[1..];
}

internal enum MimeGlobKind
{
    Literal,
    Extension,
    Other
}

internal static class GlobPatternExtensions
{
    public static int AsSpanIndexOfAnyWildcard(this string pattern) => pattern.AsSpan().IndexOfAny('*', '?', '[');
}
