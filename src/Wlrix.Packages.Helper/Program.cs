using System.ComponentModel;
using System.Diagnostics;
using Wlrix.Packages.Privileged;

namespace Wlrix.Packages.Helper;

/// <summary>
/// The privileged half of the Software Manager: the program <c>pkexec</c> runs as root.
///
/// It is deliberately short enough to read end to end, because that is the only way anyone can
/// satisfy themselves about what it will and will not do. The whole of it is:
///
/// <list type="number">
///   <item>parse argv into a verb, a backend and some targets, refusing anything else;</item>
///   <item>look the invocation up in a fixed table (<see cref="HelperCommands"/>);</item>
///   <item>run it, and copy what it says to stdout with a tag on each line.</item>
/// </list>
///
/// There is no verb that takes a command, no shell anywhere, and no path by which a string from
/// the application becomes anything but one element of an argument array. The validation in
/// <see cref="HelperRequest"/> runs <em>here</em>, on this side of the privilege boundary, even
/// though the application ran it too — the caller is not trusted, and "the caller is our own
/// application" stops being true the moment somebody runs this by hand.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Long enough for a big upgrade on a slow mirror, short enough that a package manager
    /// waiting for an answer nobody is going to give does not sit as root for ever.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromHours(2);

    private static int Main(string[] args)
    {
        // Line-buffered by hand: the application reads this stream as the transaction runs, and
        // a block-buffered stdout would deliver an entire package installation at once, at the
        // end, which is the opposite of a log pane.
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

        var check = args is [_, ..] && args[0] is "--check";
        var rest = check ? args[1..] : args;

        if (rest.Length == 0 || rest[0] is "-h" or "--help")
        {
            Usage();
            return rest.Length == 0 ? 2 : 0;
        }

        if (!HelperRequest.TryParse(rest, fileMustExist: !check, out var request, out var error))
            return Fail(error);

        if (HelperCommands.For(request) is not { } command)
            return Fail($"{request.Backend} has no way to {request.Verb.ToString().ToLowerInvariant()}");

        // --check validates and prints what would run, without running it. It is how this is
        // exercised without root, and the first thing to reach for when a transaction does
        // something unexpected: what it prints is the command itself, not a description of one.
        Console.WriteLine(new HelperFrame(FrameKind.Command, command.ToString()));
        if (check)
            return 0;

        return Run(command);
    }

    private static int Run(HelperCommand command)
    {
        var info = new ProcessStartInfo
        {
            FileName = command.Program,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        foreach (var argument in command.Arguments)
            info.ArgumentList.Add(argument);

        foreach (var (key, value) in command.Environment)
            info.Environment[key] = value;

        // The same reason the read side pins it: this output is read by a program, and a
        // package manager will happily translate it. Here it also reaches the user's Log pane,
        // where English is the lesser evil against a parser that silently sees nothing.
        info.Environment["LC_ALL"] = "C";
        info.Environment["LANG"] = "C";

        // A pinned PATH rather than an inherited one. pkexec does hand this process a minimal
        // known environment, so in the ordinary case the two agree -- but this program also runs
        // as root, resolves a program name through it, and has no business depending on somebody
        // else's idea of where the system's binaries are. Naming it is one line; discovering
        // later that it mattered is not.
        info.Environment["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

        using var process = Start(info);
        if (process is null)
            return 1;

        // stdin closed rather than inherited: a maintainer script that decides to prompt gets
        // EOF and gives up, instead of waiting for ever on a terminal nobody is watching.
        process.StandardInput.Close();

        process.OutputDataReceived += (_, e) => Write(FrameKind.Log, e.Data);
        process.ErrorDataReceived += (_, e) => Write(FrameKind.Error, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            Kill(process);
            return Fail($"{command.Program} did not finish within {Timeout.TotalHours:0} hours");
        }

        // The parameterless overload after the timed one: it is what waits for the redirected
        // streams to drain, so the last lines of a failed transaction are not lost.
        process.WaitForExit();

        Console.WriteLine(new HelperFrame(FrameKind.Done, process.ExitCode.ToString()));
        return process.ExitCode;
    }

    private static Process? Start(ProcessStartInfo info)
    {
        try
        {
            if (Process.Start(info) is { } process)
                return process;

            Fail($"could not start {info.FileName}");
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Fail($"could not start {info.FileName}: {ex.Message}");
            return null;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or Win32Exception or AggregateException)
        {
            // Already gone. Nothing left to stop.
        }
    }

    private static void Write(FrameKind kind, string? line)
    {
        if (line is not null)
            Console.WriteLine(new HelperFrame(kind, line));
    }

    private static int Fail(string message)
    {
        Console.WriteLine(new HelperFrame(FrameKind.Failed, message));
        return 1;
    }

    private static void Usage()
    {
        Console.Error.WriteLine("""
            wlrix-pkg-helper -- the privileged half of the wlRIX Software Manager.

            Usage: wlrix-pkg-helper [--check] <verb> <backend> [target...]

              backend   pacman | apt | zypper
              verb      install | installfile | remove | refresh | upgrade
                        repoadd | reporemove | repoenable | repodisable
              --check   validate and print the command that would run, without running it

            Normally started by the Software Manager through pkexec. Output is one tagged line
            each: CMD, LOG, ERR, DONE or FAIL.
            """);
    }
}
