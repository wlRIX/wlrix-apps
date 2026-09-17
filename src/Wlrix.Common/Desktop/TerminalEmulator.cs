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
    /// <c>$TERMINAL</c> first, which is the convention for saying so, and only then a search.
    /// </remarks>
    public static string? Resolve()
    {
        if (Environment.GetEnvironmentVariable("TERMINAL") is { Length: > 0 } configured
            && Executables.Which(configured) is { } chosen)
        {
            return chosen;
        }

        foreach (var candidate in Candidates)
        {
            if (Executables.Which(candidate) is { } path)
                return path;
        }
        return null;
    }

    /// <summary>
    /// The command line for running a program inside a terminal.
    /// </summary>
    /// <remarks>
    /// <c>-e</c>, which every terminal in <see cref="Candidates"/> accepts, though they
    /// disagree about whether anything may follow it. Best effort is the honest description:
    /// there is no portable way to do this, which is why the specification says a terminal
    /// application is the desktop's problem rather than the entry's.
    /// </remarks>
    public static IReadOnlyList<string> Wrap(string program, IReadOnlyList<string> arguments) =>
        ["-e", program, .. arguments];
}
