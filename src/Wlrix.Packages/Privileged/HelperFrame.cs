namespace Wlrix.Packages.Privileged;

/// <summary>What kind of thing a line of the helper's stdout is.</summary>
public enum FrameKind
{
    /// <summary>The exact command about to run, for the Command pane.</summary>
    Command,

    /// <summary>A line the package manager printed on stdout.</summary>
    Log,

    /// <summary>A line the package manager printed on stderr.</summary>
    Error,

    /// <summary>The package manager exited. The payload is its exit code.</summary>
    Done,

    /// <summary>The helper refused, or could not start anything. The payload is why.</summary>
    Failed,
}

/// <summary>One framed line of the helper's output.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Text">The rest of the line.</param>
public sealed record HelperFrame(FrameKind Kind, string Text)
{
    // A four-letter tag and a space. Not JSON: every line is one tag and one free-text payload,
    // the payload is arbitrary package-manager output that would need escaping, and the whole
    // stream has to stay readable when someone runs the helper by hand to find out what broke.
    private const string CommandTag = "CMD ";
    private const string LogTag = "LOG ";
    private const string ErrorTag = "ERR ";
    private const string DoneTag = "DONE ";
    private const string FailedTag = "FAIL ";

    /// <summary>The wire form: the tag, then the text.</summary>
    public override string ToString() => Kind switch
    {
        FrameKind.Command => CommandTag + Text,
        FrameKind.Log => LogTag + Text,
        FrameKind.Error => ErrorTag + Text,
        FrameKind.Done => DoneTag + Text,
        _ => FailedTag + Text,
    };

    /// <summary>
    /// Reads a line the helper printed.
    ///
    /// An unrecognized line is a <see cref="FrameKind.Log"/> rather than an error. Anything the
    /// helper's own runtime writes to stdout — a .NET startup diagnostic, a warning from a
    /// library — arrives unframed, and losing it is worse than showing it: those are exactly the
    /// lines that explain why a transaction did not start.
    /// </summary>
    public static HelperFrame Parse(string line) => line switch
    {
        _ when line.StartsWith(CommandTag, StringComparison.Ordinal) =>
            new HelperFrame(FrameKind.Command, line[CommandTag.Length..]),
        _ when line.StartsWith(LogTag, StringComparison.Ordinal) =>
            new HelperFrame(FrameKind.Log, line[LogTag.Length..]),
        _ when line.StartsWith(ErrorTag, StringComparison.Ordinal) =>
            new HelperFrame(FrameKind.Error, line[ErrorTag.Length..]),
        _ when line.StartsWith(DoneTag, StringComparison.Ordinal) =>
            new HelperFrame(FrameKind.Done, line[DoneTag.Length..]),
        _ when line.StartsWith(FailedTag, StringComparison.Ordinal) =>
            new HelperFrame(FrameKind.Failed, line[FailedTag.Length..]),
        _ => new HelperFrame(FrameKind.Log, line),
    };

    /// <summary>The exit code a <see cref="FrameKind.Done"/> carries, if it parses as one.</summary>
    public int? ExitCode =>
        Kind == FrameKind.Done && int.TryParse(Text.Trim(), out var code) ? code : null;
}
