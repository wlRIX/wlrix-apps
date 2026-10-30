using System.Diagnostics;

namespace Wlrix.Common;

/// <summary>Locating programs on the user's <c>PATH</c>.</summary>
public static class Executables
{
    /// <summary>
    /// The full path to <paramref name="program"/>, or <c>null</c> if it is not installed.
    ///
    /// A name containing a separator is taken as a path and only checked for existence — the
    /// same rule <c>which(1)</c> follows, and what makes it safe to pass a configured value
    /// (<c>$TERMINAL</c>, a package manager path) straight in.
    ///
    /// <paramref name="searchPath"/> is searched instead of the <c>PATH</c> when given. Only
    /// absolute directories count, and only a file with an execute bit, which are the two ways a
    /// search could otherwise land on something in the current directory or a stray data file.
    /// </summary>
    public static string? Which(string program, string? searchPath = null)
    {
        if (program.Length == 0)
            return null;

        if (program.Contains('/'))
            return File.Exists(program) ? program : null;

        var path = searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathRooted)
            .Select(dir => Path.Combine(dir, program))
            .FirstOrDefault(IsExecutableFile);
    }

    /// <summary>
    /// How to start <paramref name="program"/>: the only way this solution makes a
    /// <see cref="ProcessStartInfo"/>, which <c>BannedSymbols.txt</c> enforces.
    /// </summary>
    /// <remarks>
    /// <see cref="Process.Start(ProcessStartInfo)"/> given a bare name looks beside this program,
    /// then in the <em>current directory</em>, and only then on the <c>PATH</c>, unlike
    /// <c>execvp</c> and every shell. So a file manager started from a downloaded directory would
    /// run a <c>pdftoppm</c> it found there, and a <c>PATH</c> set on the child changes nothing.
    /// This resolves the name first, so <c>Process.Start</c> only ever sees an absolute path.
    ///
    /// <para>
    /// A program that is not installed gets a path that cannot exist rather than an exception here:
    /// callers already handle the <see cref="System.ComponentModel.Win32Exception"/>
    /// <c>Process.Start</c> throws for a missing program, and falling back on the bare name is the
    /// one thing this must not do.
    /// </para>
    /// </remarks>
    public static ProcessStartInfo StartInfoFor(string program, string? searchPath = null)
    {
#pragma warning disable RS0030 // The one place allowed to.
        return new ProcessStartInfo(Which(program, searchPath) ?? Path.Combine(NotInstalled, program));
#pragma warning restore RS0030
    }

    private const string NotInstalled = "/nonexistent";

    private static bool IsExecutableFile(string path)
    {
        const UnixFileMode anyExecute =
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        try
        {
            return File.Exists(path)
                   && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & anyExecute) != 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="program"/> is on the <c>PATH</c>.</summary>
    public static bool Exists(string program) => Which(program) is not null;
}
