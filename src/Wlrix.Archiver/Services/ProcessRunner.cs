using System.ComponentModel;
using System.Diagnostics;
using Wlrix.Common;

namespace Wlrix.Archiver.Services;

/// <summary>A finished process: what it printed, and how it ended.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Starting external archive tools and reading what they say.</summary>
public interface IProcessRunner
{
    /// <param name="program">What to run.</param>
    /// <param name="arguments">Its arguments, one element each.</param>
    /// <param name="standardInput">
    /// What to write to its standard input before closing it, or null to close it at once. This
    /// is where a secret goes: anything in the arguments can be read from <c>/proc</c> by every
    /// other user on the machine for as long as the process runs.
    /// </param>
    /// <param name="cancellationToken">Stops the program.</param>
    Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments,
        string? standardInput = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// The only place in this application that starts a process.
///
/// Two rules, both borrowed from <c>Wlrix.Packages.Processes.ProcessRunner</c>, which learned
/// them the hard way.
///
/// <b>Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never a joined string.</b>
/// Every argument here is a path out of an archive or a file dialog, which is to say attacker
/// input; a joined command line would put quoting between that and the shell. There is no shell
/// in this path at all.
///
/// <b>Every child runs in the C locale.</b> <c>7z l</c> on a Japanese system answers with
/// translated column headings, and the listing parser reads column headings. <c>LC_ALL=C</c>
/// makes the output the one the parser was written against, on every machine. Note this
/// concerns the tool's *messages* only — the filenames it prints are still whatever the archive
/// stored, which is the problem <see cref="Encodings.FilenameDecoder"/> exists for.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments,
        string? standardInput = null, CancellationToken cancellationToken = default)
    {
        using var process = Start(program, arguments, standardInput);

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Terminate(process);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private static Process Start(string program, IReadOnlyList<string> arguments, string? standardInput)
    {
        var info = Executables.StartInfoFor(program);
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        // 7z prompts on a name collision and waits forever for an answer that is never
        // coming. The commands below pass an overwrite switch so it should not ask, but
        // closing stdin turns "hangs" into "fails" if one ever does.
        info.RedirectStandardInput = true;
        // UTF-8 whatever the locale, and no byte order mark, which would become part of a password.
        info.StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        info.Environment["LC_ALL"] = "C";
        info.Environment["LANG"] = "C";
        info.Environment["LANGUAGE"] = "C";

        try
        {
            var process = Process.Start(info)
                ?? throw new Archives.ArchiveException($"Could not start {program}.");
            if (standardInput is not null)
                process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
            return process;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new Archives.ArchiveException($"Could not start {program}.", ex);
        }
    }

    private static void Terminate(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or Win32Exception or AggregateException)
        {
            // Already gone, or gone between the check and the kill. Nothing left to stop.
        }
    }
}
