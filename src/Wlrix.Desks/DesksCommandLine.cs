using System.Reflection;
using CommandLine;
using CommandLine.Text;

namespace Wlrix.Desks;

/// <summary>
/// Turns <c>argv</c> into <see cref="DesksOptions"/>, or into a usage message and an exit code.
/// </summary>
/// <remarks>
/// The help text is English and is not localized, deliberately and in line with
/// <c>wlrix-packages-helper</c>: <c>--help</c> is read by people writing scripts as much as by
/// people, and output whose shape follows <c>LANG</c> is a hazard for both.
/// </remarks>
internal static class DesksCommandLine
{
    /// <summary>What the program calls itself when it complains.</summary>
    private const string ProgramName = "wlrix-desks";

    /// <summary>
    /// The short forms from the man page, each mapped to the long option it stands for.
    /// </summary>
    /// <remarks>
    /// CommandLineParser 2.9.1 gives an option one long name and at most a one-character short
    /// name; it has no notion of an alias. Declaring a second hidden option per flag would
    /// work, but it doubles the options class and leaves every reader having to remember to OR
    /// each pair together — a mistake that never announces itself. Rewriting the tokens first
    /// keeps the alias list in one visible place and the options class at one property per
    /// flag.
    /// </remarks>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--nown"] = "--noWindowName",
        ["--hss"] = "--hideSnapShots",
        ["--ngd"] = "--noGlobalDesk",
        ["-h"] = "--help",
    };

    // Case-insensitive because the man page's spellings are camel-case and nobody types that
    // capital S in `hideSnapShots` correctly the first time; with five options and no prefix
    // matching there is nothing for the relaxed comparison to collide with. HelpWriter is null
    // because one writer cannot send `--help` to stdout and a typo's complaint to stderr, and
    // this file wants both.
    private static readonly Parser Parser = new(settings =>
    {
        settings.CaseSensitive = false;
        settings.HelpWriter = null;
    });

    /// <summary>
    /// Rewrites the man page's short forms into their long equivalents, leaving every other
    /// token alone.
    /// </summary>
    /// <remarks>
    /// Whole tokens only, never a prefix: a near miss like <c>--now</c> reaches the parser
    /// unexpanded and comes back as an unknown option, which is the right answer.
    ///
    /// <para>
    /// Rewriting before parsing is safe here because Desks takes no positional arguments and no
    /// option takes a value, so there is no position in which <c>--hss</c> could mean anything
    /// but the flag. That stops being true the day an option grows an argument.
    /// </para>
    /// </remarks>
    internal static string[] ExpandAliases(string[] args) =>
        [.. args.Select(argument => Aliases.TryGetValue(argument, out var expanded) ? expanded : argument)];

    /// <summary>Parses the command line, writing to the process console.</summary>
    internal static bool TryParse(string[] args, out DesksOptions options, out int exitCode) =>
        TryParse(args, Console.Out, Console.Error, out options, out exitCode);

    /// <summary>
    /// Parses the command line.
    /// </summary>
    /// <returns>
    /// True when there are options to run with. False when the program should stop and return
    /// <paramref name="exitCode"/> — which is 0 for <c>--help</c> and <c>--version</c>, and 2
    /// for anything the parser rejected.
    /// </returns>
    /// <remarks>
    /// The writers are parameters so the tests can read what this says without capturing the
    /// process console.
    ///
    /// <para>
    /// Exit 2 for an option this build does not know, rather than ignoring it, is the rule
    /// every wlRIX program follows; the reasoning is written out in
    /// <c>Wlrix.Files/Program.cs</c>, where a flag silently treated as a filename once let the
    /// settings daemon read "this config is fine" out of a build that had never heard of
    /// <c>--check-config</c>.
    /// </para>
    /// </remarks>
    internal static bool TryParse(
        string[] args, TextWriter output, TextWriter error, out DesksOptions options, out int exitCode)
    {
        var result = Parser.ParseArguments<DesksOptions>(args);
        if (result is Parsed<DesksOptions> parsed)
        {
            options = parsed.Value;
            exitCode = 0;
            return true;
        }

        options = new DesksOptions();
        var errors = ((NotParsed<DesksOptions>)result).Errors.ToList();

        if (errors.IsHelp())
        {
            output.WriteLine(Usage());
            exitCode = 0;
            return false;
        }

        if (errors.IsVersion())
        {
            output.WriteLine($"{ProgramName} {Version()}");
            exitCode = 0;
            return false;
        }

        // Name the offending token the way the other programs do, then say what was allowed.
        // The parser's own error block is not reused: it goes through AutoBuild, which would
        // print the whole usage a second time.
        var sentences = SentenceBuilder.Create();
        foreach (var problem in errors)
        {
            error.WriteLine(problem is TokenError token
                ? $"{ProgramName}: unknown option: {Dashed(token.Token)}"
                : $"{ProgramName}: {sentences.FormatError(problem)}");
        }

        error.WriteLine();
        error.WriteLine(Usage());
        exitCode = 2;
        return false;
    }

    /// <summary>The usage block, built once from the options themselves.</summary>
    /// <remarks>
    /// Built by hand rather than through <c>HelpText.AutoBuild</c>'s customization callback,
    /// which only runs for a result that failed to parse — so the same call would come out
    /// styled one way for <c>--help</c> and another for a typo. Parsing an empty command line
    /// is simply the cheapest way to hand <c>AddOptions</c> a result to reflect over.
    /// </remarks>
    private static string Usage()
    {
        var help = new HelpText
        {
            Heading = $"{ProgramName} {Version()}",
            Copyright = string.Empty,
            AdditionalNewLineAfterOption = false,
            AddDashesToOption = true,
            MaximumDisplayWidth = 96,
        };

        help.AddPreOptionsLine($"Usage: {ProgramName} [options]");
        help.AddOptions(Parser.ParseArguments<DesksOptions>([]));
        help.AddPostOptionsLine("Short forms: --nown, --hss, --ngd");
        return help.ToString();
    }

    // The parser reports the name it parsed, with the dashes already taken off; put back the
    // ones it would have had, so the line names something the user can recognize as what they
    // typed.
    private static string Dashed(string token) => token.Length == 1 ? $"-{token}" : $"--{token}";

    private static string Version() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
}
