using System.Text;

namespace Wlrix.Common.Desktop;

/// <summary>
/// Environment variables carried on an <c>Exec=</c> line, as <c>env NAME=value … program</c>.
/// </summary>
/// <remarks>
/// The desktop entry spec has no key for a program's environment, and <c>env</c> in front of the
/// command is what every launcher already understands: wlRIX's, other desktops', a shell's. So
/// an editor that offers environment variables as a list of their own writes them this way, and
/// reads them back out of a line written so -- by itself or by hand.
///
/// <para>
/// Works on the <c>Exec</c> value as it is in the file, quoting and field codes included; the
/// command after the variables is handed back exactly as written.
/// </para>
/// </remarks>
public static class ExecEnvironment
{
    /// <summary>
    /// Splits a leading <c>env NAME=value …</c> off <paramref name="exec"/>.
    /// </summary>
    /// <returns>
    /// The variables in order, and the command that follows them. A line with no such prefix is
    /// all command and no variables; so is an <c>env</c> with options, which is someone's own
    /// arrangement and is left whole rather than half understood.
    /// </returns>
    public static (IReadOnlyList<KeyValuePair<string, string>> Variables, string Command) Split(string exec)
    {
        var tokens = Tokens(exec);
        if (tokens.Count < 2 || tokens[0].Text is not ("env" or "/usr/bin/env" or "/bin/env"))
            return ([], exec.Trim());

        var variables = new List<KeyValuePair<string, string>>();
        var index = 1;
        for (; index < tokens.Count; index++)
        {
            var text = tokens[index].Text;
            var equals = text.IndexOf('=');
            if (equals <= 0 || !IsValidName(text[..equals]))
                break;
            variables.Add(new(text[..equals], text[(equals + 1)..]));
        }

        if (variables.Count == 0 || index >= tokens.Count)
            return ([], exec.Trim());
        return (variables, exec[tokens[index].Start..].Trim());
    }

    /// <summary>
    /// <paramref name="command"/> with <paramref name="variables"/> set in front of it, or the
    /// command alone when there are none.
    /// </summary>
    public static string Join(IReadOnlyList<KeyValuePair<string, string>> variables, string command)
    {
        command = command.Trim();
        if (variables.Count == 0)
            return command;

        var line = new StringBuilder("env");
        foreach (var (name, value) in variables)
            line.Append(' ').Append(name).Append('=').Append(Quote(value));
        return line.Append(' ').Append(command).ToString();
    }

    /// <summary>Whether <paramref name="name"/> can be an environment variable's name.</summary>
    public static bool IsValidName(string name) =>
        name.Length > 0
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    // A value with nothing a launcher would split on or interpret goes as it is; anything else is
    // double-quoted, with the four characters the spec reserves inside quotes escaped.
    private static string Quote(string value)
    {
        if (value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:,+@%".Contains(c)))
            return value;
        var quoted = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c is '"' or '`' or '$' or '\\')
                quoted.Append('\\');
            quoted.Append(c);
        }
        return quoted.Append('"').ToString();
    }

    private readonly record struct Token(string Text, int Start);

    // The same quoting rules as ExecParser's tokenizer, keeping where each token began so the
    // command after the variables can be handed back exactly as written.
    private static List<Token> Tokens(string s)
    {
        var tokens = new List<Token>();
        var text = new StringBuilder();
        var start = -1;
        var inQuotes = false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < s.Length)
                    text.Append(s[++i]);
                else if (c == '"')
                    inQuotes = false;
                else
                    text.Append(c);
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (start >= 0)
                {
                    tokens.Add(new Token(text.ToString(), start));
                    text.Clear();
                    start = -1;
                }
                continue;
            }

            if (start < 0)
                start = i;
            if (c == '"')
                inQuotes = true;
            else
                text.Append(c);
        }

        if (start >= 0)
            tokens.Add(new Token(text.ToString(), start));
        return tokens;
    }
}
