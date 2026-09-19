using System.Diagnostics;
using Wlrix.Common.Desktop;

namespace Wlrix.Files.Core.Platform;

/// <summary>Starts applications: a desktop entry against a set of files, or a program itself.</summary>
/// <remarks>
/// The Toolchest has a launcher of its own, and this is deliberately not it: that one launches
/// an application with no arguments from a menu, where this one has to substitute field codes
/// and decide between one invocation and several. The shared half — parsing <c>Exec=</c> and
/// expanding the codes — is <see cref="ExecParser"/> in <c>Wlrix.Common</c>, which both use.
/// </remarks>
public sealed class ApplicationLauncher
{
    /// <summary>Raised when something could not be started, with a message to show.</summary>
    public event Action<string>? Failed;

    /// <summary>
    /// Opens files with an application.
    /// </summary>
    /// <remarks>
    /// An entry whose <c>Exec</c> takes <c>%F</c> or <c>%U</c> is handed every file at once;
    /// one that takes <c>%f</c> or <c>%u</c> gets one invocation each, because that is what
    /// the singular codes mean and passing several would silently open only the first.
    /// </remarks>
    public bool Open(DesktopEntry entry, IReadOnlyList<Location> files)
    {
        if (string.IsNullOrWhiteSpace(entry.Exec))
        {
            Failed?.Invoke($"{Label(entry)} has nothing to run");
            return false;
        }

        var paths = files
            .Select(file => file.TryGetLocalPath(out var path) ? path : file.ToUriString())
            .ToList();

        if (paths.Count == 0 || ExecParser.ExpectsMultiple(entry.Exec))
            return Start(entry, ExecFields.ForPaths(paths));

        // One at a time. Two windows of a text editor is the right answer to opening two files
        // with one that only knows how to be given one.
        var started = false;
        foreach (var path in paths)
            started |= Start(entry, ExecFields.ForPath(path));
        return started;
    }

    private bool Start(DesktopEntry entry, ExecFields fields)
    {
        if (ExecParser.Parse(entry.Exec!, fields) is not { } parsed || parsed.File.Length == 0)
        {
            Failed?.Invoke($"could not read the command for {Label(entry)}");
            return false;
        }

        var (file, arguments) = parsed;
        if (entry.Terminal)
        {
            // A console application started directly attaches to whatever standard input this
            // process has, which for a file manager is nothing -- it appears to launch and is
            // simply not there. Reported live: Micro "opened" into the console of the IDE
            // that had started the file manager.
            if (TerminalEmulator.Resolve() is not { } terminal)
            {
                Failed?.Invoke($"{Label(entry)} needs a terminal, and none is installed");
                return false;
            }
            arguments = TerminalEmulator.Wrap(file, arguments);
            file = terminal;
        }

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = file,
                // False, so the child is executed directly rather than handed back to the
                // desktop's own opener -- which is this application, and would be a loop.
                UseShellExecute = false,
                WorkingDirectory = entry.Path is { Length: > 0 } working && Directory.Exists(working)
                    ? working
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            Failed?.Invoke($"could not start {Label(entry)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Runs a program directly, rather than through a desktop entry.</summary>
    /// <remarks>
    /// For the case a desktop entry cannot describe: a file that is itself the program, an
    /// AppImage or a bare binary. There is no <c>Exec</c> line to parse and no field codes to
    /// substitute, so none of the machinery above applies — but the working directory does, and
    /// it is the file's own, which is what a program dropped in a project directory expects.
    ///
    /// <para>
    /// The caller is responsible for having asked the user first. See
    /// <see cref="ExecutablePolicy"/>: nothing here judges whether running this is a good idea.
    /// </para>
    /// </remarks>
    public bool Run(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = path,
                // As above: false, so the program is executed rather than handed back to the
                // desktop's opener, which is this application.
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path) is { Length: > 0 } directory
                    ? directory
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            Failed?.Invoke($"could not start {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The name to put in an error message, in the user's language if there is one.</summary>
    private static string Label(DesktopEntry entry) =>
        DesktopEntryParser.ResolveLocalized(entry.Name, System.Globalization.CultureInfo.CurrentCulture)
            is { Length: > 0 } name
            ? name
            : entry.Id;
}
