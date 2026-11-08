namespace Wlrix.Files.Core;

/// <summary>Shell-style wildcard matching of a file name, as <c>fnmatch(3)</c> does it.</summary>
/// <remarks>
/// <para>
/// Supports <c>*</c>, <c>?</c>, bracket expressions such as <c>[0-9]</c> or <c>[!a-z]</c>,
/// and a backslash escaping the next character. A name is matched whole, with no special
/// treatment of <c>/</c> or a leading dot, because what is matched is always a single name.
/// </para>
/// <para>
/// .NET's own <c>FileSystemName.MatchesSimpleExpression</c> knows only <c>*</c> and
/// <c>?</c> and takes a bracket literally, which is not enough for what arrives here.
/// Chromium writes every filter it sends the file chooser portal case-insensitively, as
/// <c>*.[pP][nN][gG]</c>, and shared-mime-info has patterns such as <c>*.[1-9]</c> for
/// manual pages.
/// </para>
/// </remarks>
public static class Glob
{
    /// <summary>Whether <paramref name="name"/> matches <paramref name="pattern"/> in full.</summary>
    public static bool IsMatch(string pattern, string name, bool ignoreCase = false)
    {
        int p = 0, n = 0;
        // Where to resume after the most recent star: the pattern just past it, and the
        // name position it is currently standing in for. One is enough, because a later star
        // can absorb anything an earlier one would have.
        int starP = -1, starN = 0;

        while (n < name.Length)
        {
            if (p < pattern.Length)
            {
                var c = pattern[p];
                if (c == '*')
                {
                    starP = ++p;
                    starN = n;
                    continue;
                }

                if (c == '?')
                {
                    p++;
                    n++;
                    continue;
                }

                if (c == '[' && TryClass(pattern, p, name[n], ignoreCase, out var matched, out var end))
                {
                    if (matched)
                    {
                        p = end;
                        n++;
                        continue;
                    }
                }
                else
                {
                    // An unterminated bracket is a literal one, as fnmatch has it.
                    if (c == '\\' && p + 1 < pattern.Length)
                        c = pattern[++p];
                    if (Same(c, name[n], ignoreCase))
                    {
                        p++;
                        n++;
                        continue;
                    }
                }
            }

            if (starP < 0)
                return false;
            // Let the last star take one more character, and try the rest again from there.
            p = starP;
            n = ++starN;
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    /// <summary>Reads the bracket expression starting at <paramref name="start"/>.</summary>
    /// <returns>False if the bracket is never closed, and so is not an expression.</returns>
    private static bool TryClass(string pattern, int start, char ch, bool ignoreCase, out bool matched, out int end)
    {
        matched = false;
        end = start;
        var i = start + 1;
        var negated = i < pattern.Length && pattern[i] is '!' or '^';
        if (negated)
            i++;

        var first = true;
        var hit = false;
        while (i < pattern.Length)
        {
            var low = pattern[i];
            // A ] straight after the opening is a member, not the close: []] and [!]] work.
            if (low == ']' && !first)
            {
                matched = hit != negated;
                end = i + 1;
                return true;
            }

            first = false;
            if (low == '\\' && i + 1 < pattern.Length)
                low = pattern[++i];
            i++;

            var high = low;
            // A - before the close is a member too: [a-] is a or -.
            if (i + 1 < pattern.Length && pattern[i] == '-' && pattern[i + 1] != ']')
            {
                var j = i + 1;
                if (pattern[j] == '\\' && j + 1 < pattern.Length)
                    j++;
                high = pattern[j];
                i = j + 1;
            }

            hit |= InRange(ch, low, high)
                || (ignoreCase && (InRange(char.ToLowerInvariant(ch), low, high)
                    || InRange(char.ToUpperInvariant(ch), low, high)));
        }

        return false;
    }

    private static bool InRange(char ch, char low, char high) => ch >= low && ch <= high;

    private static bool Same(char a, char b, bool ignoreCase) =>
        a == b || (ignoreCase && char.ToUpperInvariant(a) == char.ToUpperInvariant(b));
}
