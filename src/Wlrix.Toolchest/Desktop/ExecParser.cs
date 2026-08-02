using System.Text;

namespace Wlrix.Toolchest.Desktop;

/// <summary>
/// Turns a <c>.desktop</c> <c>Exec</c> value into a program and argument list: it honors the
/// spec's double-quote/backslash quoting and strips field codes (<c>%f %F %u %U %i %c %k</c>,
/// with <c>%%</c> → <c>%</c>), which have no meaning when launching from a menu.
/// </summary>
public static class ExecParser
{
    public static (string File, IReadOnlyList<string> Args)? Parse(string exec)
    {
        var cleaned = new List<string>();
        foreach (var token in Tokenize(exec))
            if (StripFieldCodes(token) is { } t)
                cleaned.Add(t);

        if (cleaned.Count == 0)
            return null;

        return (cleaned[0], cleaned.GetRange(1, cleaned.Count - 1));
    }

    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false, hasToken = false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < s.Length)
                {
                    sb.Append(s[++i]);
                    hasToken = true;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    sb.Append(c);
                    hasToken = true;
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                    hasToken = false;
                }
            }
            else
            {
                sb.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
            tokens.Add(sb.ToString());

        return tokens;
    }

    // Removes field codes within a token; returns null if nothing is left (a pure field-code token).
    private static string? StripFieldCodes(string token)
    {
        if (!token.Contains('%'))
            return token;

        var sb = new StringBuilder(token.Length);
        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] != '%' || i + 1 >= token.Length)
            {
                sb.Append(token[i]);
                continue;
            }

            var code = token[++i];
            if (code == '%')
                sb.Append('%');
            // All other codes (f F u U i c k d D n N v m) expand to nothing here.
        }

        return sb.Length == 0 ? null : sb.ToString();
    }
}
