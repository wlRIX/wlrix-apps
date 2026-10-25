namespace Wlrix.Common.Desktop;

/// <summary>
/// Finding a terminal to run a console application in.
/// </summary>
/// <remarks>
/// A desktop entry with <c>Terminal=true</c> is not something to start directly. Doing so
/// attaches it to whatever standard input the launching application happened to have — which
/// for a file manager started from a session is nothing at all, or worse, the console of
/// whatever started that. The application appears to launch and is simply not there.
///
/// <para>
/// Shared because there are now two callers: the Toolchest launching a console application
/// from a menu, and the file manager opening a file with one.
/// </para>
/// </remarks>
public static class TerminalEmulator
{
    /// <summary>
    /// Terminals to look for, in order.
    /// </summary>
    /// <remarks>
    /// wlRIX ships none of its own, so this is a search of what the machine has. Ordered
    /// Wayland-native first, because an X11 terminal under this compositor works but goes
    /// through XWayland to do it.
    /// </remarks>
    public static IReadOnlyList<string> Candidates { get; } =
        ["alacritty", "foot", "kitty", "wezterm", "konsole", "gnome-terminal", "xterm"];

    /// <summary>The terminal to use, or null if the machine has none.</summary>
    /// <remarks>
    /// The user's choice first, from <c>xdg-terminals.list</c> (see <see cref="TerminalList"/>),
    /// which the Desktop Utilities panel writes and <c>xdg-terminal-exec</c> reads. Then
    /// <c>$TERMINAL</c>, which is the older convention for saying so, and only then a search.
    /// </remarks>
    public static ResolvedTerminal? Resolve() =>
        Resolve(TerminalList.ForSession(), static () => new DesktopEntryScanner().Scan());

    /// <summary>The terminal to use, from the given list and entries.</summary>
    /// <param name="list">The preference files.</param>
    /// <param name="entries">
    /// The installed entries, asked for only when the list names something: scanning every
    /// application is the expensive part, and most machines have no list.
    /// </param>
    public static ResolvedTerminal? Resolve(TerminalList list, Func<IReadOnlyList<DesktopEntry>> entries)
    {
        var ids = list.PreferredIds().ToList();
        if (ids.Count > 0)
        {
            var byId = new Dictionary<string, DesktopEntry>(StringComparer.Ordinal);
            foreach (var entry in entries())
                byId.TryAdd(entry.Id, entry);

            // An entry that is named but no longer installed is skipped, as the specification
            // says, rather than ending the search.
            foreach (var id in ids)
            {
                if (byId.TryGetValue(id, out var entry) && FromEntry(entry) is { } chosen)
                    return chosen;
            }
        }

        if (Environment.GetEnvironmentVariable("TERMINAL") is { Length: > 0 } configured
            && Executables.Which(configured) is { } named)
        {
            return new ResolvedTerminal(named, []);
        }

        foreach (var candidate in Candidates)
        {
            if (Executables.Which(candidate) is { } path)
                return new ResolvedTerminal(path, []);
        }
        return null;
    }

    /// <summary>A terminal's entry as a command, or null if it cannot be run.</summary>
    public static ResolvedTerminal? FromEntry(DesktopEntry entry)
    {
        if (entry.Exec is not { } exec || ExecParser.Parse(exec) is not { File.Length: > 0 } parsed)
            return null;
        if (Executables.Which(parsed.File) is not { } path)
            return null;
        return new ResolvedTerminal(
            path,
            parsed.Args,
            entry.TerminalExecArgument is { Length: > 0 } argument ? argument : ResolvedTerminal.DefaultExecArgument);
    }
}

/// <summary>A terminal emulator, ready to run.</summary>
/// <param name="File">The program.</param>
/// <param name="Arguments">What its entry's <c>Exec</c> passes it, before anything else.</param>
/// <param name="ExecArgument">The option that runs a command in it.</param>
public sealed record ResolvedTerminal(
    string File,
    IReadOnlyList<string> Arguments,
    string ExecArgument = ResolvedTerminal.DefaultExecArgument)
{
    /// <summary>
    /// <c>-e</c>, which every terminal in <see cref="TerminalEmulator.Candidates"/> accepts,
    /// though they disagree about whether anything may follow it.
    /// </summary>
    public const string DefaultExecArgument = "-e";

    /// <summary>
    /// The arguments for running a program inside this terminal.
    /// </summary>
    /// <remarks>
    /// Best effort is the honest description: there is no portable way to do this, which is
    /// why the xdg-terminal-exec specification lets an entry name its own option, and why the
    /// desktop entry specification says a terminal application is the desktop's problem rather
    /// than the entry's.
    /// </remarks>
    public IReadOnlyList<string> Wrap(string program, IReadOnlyList<string> arguments) =>
        [.. Arguments, ExecArgument, program, .. arguments];
}
