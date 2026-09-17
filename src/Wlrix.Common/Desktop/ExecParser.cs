using System.Text;

namespace Wlrix.Common.Desktop;

/// <summary>
/// What to substitute for the field codes in an <c>Exec</c> line.
/// </summary>
/// <param name="Paths">Local paths, for <c>%f</c> and <c>%F</c>.</param>
/// <param name="Uris">URIs, for <c>%u</c> and <c>%U</c>. Falls back to <paramref name="Paths"/> as <c>file:</c> URIs.</param>
/// <param name="Icon">The entry's <c>Icon=</c>, for <c>%i</c>.</param>
/// <param name="DisplayName">The entry's localized name, for <c>%c</c>.</param>
/// <param name="EntryPath">The entry's own file, for <c>%k</c>.</param>
public readonly record struct ExecFields(
    IReadOnlyList<string>? Paths = null,
    IReadOnlyList<string>? Uris = null,
    string? Icon = null,
    string? DisplayName = null,
    string? EntryPath = null)
{
    /// <summary>Nothing to substitute: every code expands to nothing.</summary>
    public static ExecFields None => default;

    /// <summary>Substitutes one file, by local path.</summary>
    public static ExecFields ForPath(string path) => new(Paths: [path]);

    /// <summary>Substitutes several files, by local path.</summary>
    public static ExecFields ForPaths(IReadOnlyList<string> paths) => new(Paths: paths);
}

/// <summary>
/// Turns a <c>.desktop</c> <c>Exec</c> value into a program and argument list.
/// </summary>
/// <remarks>
/// Honors the spec's double-quote and backslash quoting, and either strips the field
/// codes (<c>%f %F %u %U %i %c %k</c>) or substitutes them.
///
/// <para>
/// Stripping is right for a menu, which launches an application with no document.
/// Substituting is what "Open With" needs, and the difference is not cosmetic: an
/// entry whose <c>Exec</c> is <c>myviewer %f</c> launched with the code stripped opens
/// an empty window instead of the file the user double-clicked.
/// </para>
///
/// <para>
/// The singular codes (<c>%f</c>, <c>%u</c>) take only the first item, and the spec is
/// explicit that a caller wanting to open several files with such an entry must run it
/// once per file. That decision belongs to the caller, which can see how many files
/// there are; <see cref="ExpectsMultiple"/> answers it.
/// </para>
/// </remarks>
public static class ExecParser
{
    /// <summary>Parses an <c>Exec</c> line, stripping every field code.</summary>
    public static (string File, IReadOnlyList<string> Args)? Parse(string exec) =>
        Parse(exec, ExecFields.None);

    /// <summary>Parses an <c>Exec</c> line, substituting the field codes.</summary>
    public static (string File, IReadOnlyList<string> Args)? Parse(string exec, ExecFields fields)
    {
        var result = new List<string>();
        foreach (var token in Tokenize(exec))
            Expand(token, fields, result);

        if (result.Count == 0)
            return null;

        return (result[0], result.GetRange(1, result.Count - 1));
    }

    /// <summary>
    /// Whether an <c>Exec</c> line can take more than one file in a single launch —
    /// that is, whether it uses <c>%F</c> or <c>%U</c>.
    /// </summary>
    /// <remarks>
    /// The caller needs this to decide between one launch with every file and one
    /// launch per file. Guessing wrong silently opens only the first of a selection.
    /// </remarks>
    public static bool ExpectsMultiple(string exec)
    {
        for (var i = 0; i + 1 < exec.Length; i++)
        {
            if (exec[i] != '%')
                continue;
            var code = exec[i + 1];
            if (code is 'F' or 'U')
                return true;
            // Skip the escaped percent so "%%F" is not read as a list code.
            if (code == '%')
                i++;
        }
        return false;
    }

    /// <summary>Whether an <c>Exec</c> line takes a file at all.</summary>
    public static bool AcceptsFiles(string exec)
    {
        for (var i = 0; i + 1 < exec.Length; i++)
        {
            if (exec[i] != '%')
                continue;
            var code = exec[i + 1];
            if (code is 'f' or 'F' or 'u' or 'U')
                return true;
            if (code == '%')
                i++;
        }
        return false;
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

    /// <summary>
    /// Expands one token's field codes into <paramref name="into"/>.
    /// </summary>
    /// <remarks>
    /// A token can produce several arguments (<c>%F</c>) or none at all (a token that
    /// was nothing but a code with nothing to substitute), which is why this appends
    /// rather than returning a string. A token that had no codes to begin with is
    /// passed through even when empty-looking, so an intentional empty argument
    /// survives.
    /// </remarks>
    private static void Expand(string token, ExecFields fields, List<string> into)
    {
        if (!token.Contains('%'))
        {
            into.Add(token);
            return;
        }

        var sb = new StringBuilder(token.Length);
        var produced = false;

        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] != '%' || i + 1 >= token.Length)
            {
                sb.Append(token[i]);
                continue;
            }

            switch (token[++i])
            {
                case '%':
                    sb.Append('%');
                    break;

                case 'f':
                    // Singular: the spec says only the first file, and that a caller
                    // with several must launch once each.
                    if (First(fields.Paths) is { } path)
                        sb.Append(path);
                    break;

                case 'u':
                    if (First(Uris(fields)) is { } uri)
                        sb.Append(uri);
                    break;

                case 'F':
                    produced |= Flush(sb, into, fields.Paths);
                    break;

                case 'U':
                    produced |= Flush(sb, into, Uris(fields));
                    break;

                case 'i':
                    // Expands to two arguments, --icon and the name, or to nothing.
                    if (!string.IsNullOrEmpty(fields.Icon))
                    {
                        produced |= Flush(sb, into, ["--icon", fields.Icon]);
                    }
                    break;

                case 'c':
                    if (fields.DisplayName is { } display)
                        sb.Append(display);
                    break;

                case 'k':
                    if (fields.EntryPath is { } entry)
                        sb.Append(entry);
                    break;

                // Deprecated in the spec (%d %D %n %N %v %m) and anything unknown:
                // dropped, which is what the spec says to do with the deprecated ones.
            }
        }

        if (sb.Length > 0)
            into.Add(sb.ToString());
        else if (!produced && token.Length == 0)
            into.Add(string.Empty);
    }

    /// <summary>
    /// Emits whatever is buffered plus each of <paramref name="values"/> as separate
    /// arguments. Returns whether anything was emitted.
    /// </summary>
    private static bool Flush(StringBuilder sb, List<string> into, IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return false;

        // A list code is almost always a token of its own. When it is not -- "-o%F" --
        // whatever preceded it joins the first value, which is the only reading that
        // keeps the argument intact.
        foreach (var value in values)
        {
            if (sb.Length > 0)
            {
                sb.Append(value);
                into.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                into.Add(value);
            }
        }
        return true;
    }

    private static string? First(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } ? values[0] : null;

    /// <summary>
    /// The URIs to substitute: those given, or the paths turned into <c>file:</c> URIs.
    /// </summary>
    /// <remarks>
    /// The fallback matters. An application declaring <c>%u</c> handles URIs, and
    /// handing it a bare path instead would work only by accident.
    /// </remarks>
    private static IReadOnlyList<string>? Uris(ExecFields fields)
    {
        if (fields.Uris is { Count: > 0 })
            return fields.Uris;
        if (fields.Paths is not { Count: > 0 } paths)
            return null;

        var uris = new List<string>(paths.Count);
        foreach (var path in paths)
            uris.Add(new UriBuilder { Scheme = Uri.UriSchemeFile, Host = string.Empty, Path = path }.Uri.AbsoluteUri);
        return uris;
    }
}
