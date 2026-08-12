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
    /// </summary>
    public static string? Which(string program)
    {
        if (program.Length == 0)
            return null;

        if (program.Contains('/'))
            return File.Exists(program) ? program : null;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, program))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Whether <paramref name="program"/> is on the <c>PATH</c>.</summary>
    public static bool Exists(string program) => Which(program) is not null;
}
