using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Wlrix.Packages.Processes;

/// <summary>A finished process: what it printed, and how it ended.</summary>
/// <param name="ExitCode">The exit status.</param>
/// <param name="StandardOutput">Everything on stdout.</param>
/// <param name="StandardError">Everything on stderr.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Whether the process reported success.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>One line of output, and which stream it came from.</summary>
/// <param name="Text">The line, without its terminator.</param>
/// <param name="IsError">Whether it arrived on stderr.</param>
public record OutputLine(string Text, bool IsError);

/// <summary>Starting package-manager processes and reading what they say.</summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="program"/> to completion and returns everything it printed.</summary>
    Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="program"/> and yields its output a line at a time, as it arrives.
    /// The final line is always a <see cref="ProcessExited"/>.
    /// </summary>
    IAsyncEnumerable<OutputLine> StreamAsync(string program, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The only place in this library that starts a process.
///
/// Two things are non-negotiable here and are the reason it is a single class rather than a
/// call at each site.
///
/// <b>Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never a joined string.</b>
/// A package name arrives from a search field; a joined command line would make quoting the only
/// thing standing between that field and the shell. There is no shell in this path at all.
///
/// <b>Every child runs in the C locale.</b> Package managers translate their output, and this
/// library reads that output. On a Japanese system <c>pacman -Qi</c> answers with
/// <c>名前</c> where the parser expects <c>Name</c>, and every field comes back empty — with no
/// error, because nothing failed. <c>LC_ALL=C</c> makes the output the one the parsers were
/// written against, on every machine.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        using var process = Start(program, arguments);

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

    public async IAsyncEnumerable<OutputLine> StreamAsync(string program,
        IReadOnlyList<string> arguments,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var process = Start(program, arguments);

        // Both streams feed one channel, so the caller sees output in something close to the
        // order the process produced it. Reading them into separate buffers and concatenating
        // would put every error after every success, which is exactly wrong for a log the user
        // reads to find where a transaction went bad.
        var channel = Channel.CreateUnbounded<OutputLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        var pump = Task.WhenAll(
            PumpAsync(process.StandardOutput, isError: false, channel.Writer, cancellationToken),
            PumpAsync(process.StandardError, isError: true, channel.Writer, cancellationToken));

        _ = Task.Run(async () =>
        {
            try
            {
                await pump.ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                channel.Writer.TryWrite(new ProcessExited(process.ExitCode));
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return line;
        }
        finally
        {
            if (!process.HasExited)
                Terminate(process);
        }
    }

    private static async Task PumpAsync(StreamReader reader, bool isError,
        ChannelWriter<OutputLine> writer, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            writer.TryWrite(new OutputLine(line, isError));
    }

    private static System.Diagnostics.Process Start(string program, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = program,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Nothing here is interactive; a child that decided to prompt would otherwise
            // inherit our stdin and wait for an answer that is never coming.
            RedirectStandardInput = true,
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        // See the class comment: this is what makes the parsers work on a non-English system.
        info.Environment["LC_ALL"] = "C";
        info.Environment["LANG"] = "C";
        info.Environment["LANGUAGE"] = "C";

        try
        {
            var process = System.Diagnostics.Process.Start(info)
                ?? throw new PackageProcessException($"Could not start {program}.");
            process.StandardInput.Close();
            return process;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new PackageProcessException($"Could not start {program}.", ex);
        }
    }

    private static void Terminate(System.Diagnostics.Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or Win32Exception or AggregateException)
        {
            // Already gone, or gone between the check and the kill. Either way there is nothing
            // left to stop.
        }
    }
}

/// <summary>The final line of a <see cref="IProcessRunner.StreamAsync"/> sequence.</summary>
/// <param name="ExitCode">How the process ended.</param>
public sealed record ProcessExited(int ExitCode)
    : OutputLine($"exited with {ExitCode}", ExitCode != 0);

/// <summary>A package-manager process could not be started or did not survive being run.</summary>
public sealed class PackageProcessException : Exception
{
    public PackageProcessException(string message) : base(message)
    {
    }

    public PackageProcessException(string message, Exception inner) : base(message, inner)
    {
    }
}
