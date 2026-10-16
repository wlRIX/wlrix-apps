using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Wlrix.PolkitAgent.Services;

/// <summary>What the helper said next.</summary>
internal abstract record HelperMessage
{
    /// <summary>PAM wants an answer: a password (<paramref name="Echo"/> false) or a visible one.</summary>
    public sealed record Prompt(string Text, bool Echo) : HelperMessage;

    /// <summary>Something to tell the user, not ask them.</summary>
    public sealed record Info(string Text) : HelperMessage;

    /// <summary>Something that went wrong, from PAM.</summary>
    public sealed record Error(string Text) : HelperMessage;

    /// <summary>The conversation is over. On success, polkitd has been told and will answer the caller.</summary>
    public sealed record Done(bool Success) : HelperMessage;
}

/// <summary>
/// One conversation with <c>polkit-agent-helper-1</c>: the program that runs PAM as root and,
/// when the password is right, tells polkitd that the cookie is authenticated.
///
/// The protocol is polkit's own, as <c>PolkitAgentSession</c> speaks it (polkit 127,
/// <c>src/polkitagent/polkitagentsession.c</c>):
///
/// <list type="bullet">
///   <item>Socket-activated (<c>/run/polkit/agent-helper.socket</c>, the default on a systemd
///   machine): the agent writes <c>username\n</c>, then <c>cookie\n</c>.</item>
///   <item>Spawned (the setuid helper, the older arrangement): the username is the helper's
///   argument, and only <c>cookie\n</c> is written, so the cookie is not in argv for other users
///   to read.</item>
///   <item>Then the helper writes lines -- <c>PAM_PROMPT_ECHO_OFF text</c>,
///   <c>PAM_PROMPT_ECHO_ON text</c>, <c>PAM_ERROR_MSG text</c>, <c>PAM_TEXT_INFO text</c>, and
///   finally <c>SUCCESS</c> or <c>FAILURE</c> -- each C-escaped, and the agent answers each
///   prompt with one line.</item>
/// </list>
///
/// A session is good for one attempt. A wrong password ends in FAILURE, and a retry is a new
/// session; the helper is not built to take a second guess.
/// </summary>
internal sealed class HelperSession : IAsyncDisposable
{
    /// <summary>Where systemd listens for the helper (<c>polkit-agent-helper.socket</c>).</summary>
    public const string SocketPath = "/run/polkit/agent-helper.socket";

    /// <summary>The helper itself, for machines without the socket.</summary>
    public const string HelperPath = "/usr/lib/polkit-1/polkit-agent-helper-1";

    private readonly TextReader _input;
    private readonly Stream _output;
    private readonly IDisposable? _owner;

    internal HelperSession(TextReader input, Stream output, IDisposable? owner = null)
    {
        _input = input;
        _output = output;
        _owner = owner;
    }

    /// <summary>
    /// Starts authenticating <paramref name="user"/> for <paramref name="cookie"/>, through the
    /// socket when there is one and the setuid helper when there is not.
    /// </summary>
    public static async Task<HelperSession> StartAsync(string user, string cookie, CancellationToken cancellationToken)
    {
        if (File.Exists(SocketPath))
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cancellationToken)
                    .ConfigureAwait(false);
                var stream = new NetworkStream(socket, ownsSocket: true);
                var session = new HelperSession(new StreamReader(stream, Encoding.UTF8), stream, stream);
                await session.SendAsync(user, cookie, cancellationToken).ConfigureAwait(false);
                return session;
            }
            catch (SocketException)
            {
                // polkit does the same: a socket that is there but will not take a connection
                // falls back to spawning the helper.
                socket.Dispose();
            }
        }

        var info = new ProcessStartInfo(HelperPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add(user);
        var process = Process.Start(info) ?? throw new IOException($"could not start {HelperPath}");
        var spawned = new HelperSession(process.StandardOutput, process.StandardInput.BaseStream, new Spawned(process));
        await spawned.SendAsync(user: null, cookie, cancellationToken).ConfigureAwait(false);
        return spawned;
    }

    /// <summary>
    /// The opening lines: the username (socket only; a spawned helper has it in argv), then the
    /// cookie.
    /// </summary>
    internal async Task SendAsync(string? user, string cookie, CancellationToken cancellationToken)
    {
        if (user is not null)
            await WriteLineAsync(user, cancellationToken).ConfigureAwait(false);
        await WriteLineAsync(cookie, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The next thing the helper says. Anything it ends on without SUCCESS is a failure.</summary>
    public async Task<HelperMessage> ReadAsync(CancellationToken cancellationToken)
    {
        string? line;
        try
        {
            line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return new HelperMessage.Done(false);
        }

        return line is null ? new HelperMessage.Done(false) : Parse(line);
    }

    /// <summary>Answers the prompt the helper last sent.</summary>
    public Task RespondAsync(string response, CancellationToken cancellationToken) =>
        WriteLineAsync(response, cancellationToken);

    /// <summary>One line from the helper, unescaped and sorted by its prefix.</summary>
    internal static HelperMessage Parse(string line)
    {
        var text = Unescape(line);
        if (text.StartsWith("PAM_PROMPT_ECHO_OFF ", StringComparison.Ordinal))
            return new HelperMessage.Prompt(text["PAM_PROMPT_ECHO_OFF ".Length..], Echo: false);
        if (text.StartsWith("PAM_PROMPT_ECHO_ON ", StringComparison.Ordinal))
            return new HelperMessage.Prompt(text["PAM_PROMPT_ECHO_ON ".Length..], Echo: true);
        if (text.StartsWith("PAM_ERROR_MSG ", StringComparison.Ordinal))
            return new HelperMessage.Error(text["PAM_ERROR_MSG ".Length..]);
        if (text.StartsWith("PAM_TEXT_INFO ", StringComparison.Ordinal))
            return new HelperMessage.Info(text["PAM_TEXT_INFO ".Length..]);
        if (text.StartsWith("SUCCESS", StringComparison.Ordinal))
            return new HelperMessage.Done(true);

        // FAILURE, and anything unrecognized, which polkit also takes as a failed attempt.
        return new HelperMessage.Done(false);
    }

    /// <summary>
    /// GLib's <c>g_strcompress</c>, which is what polkit decodes each line with: the C escapes
    /// <c>\b \f \n \r \t \v \\ \"</c>, one to three octal digits, and any other escaped character
    /// as itself.
    /// </summary>
    internal static string Unescape(string text)
    {
        if (!text.Contains('\\'))
            return text;

        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 == text.Length)
            {
                result.Append(text[i]);
                continue;
            }

            var c = text[++i];
            if (c is >= '0' and <= '7')
            {
                var value = 0;
                var digits = 0;
                while (digits < 3 && i < text.Length && text[i] is >= '0' and <= '7')
                {
                    value = value * 8 + (text[i] - '0');
                    i++;
                    digits++;
                }

                i--;
                result.Append((char)value);
                continue;
            }

            result.Append(c switch
            {
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'v' => '\v',
                _ => c,
            });
        }

        return result.ToString();
    }

    private async Task WriteLineAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text.EndsWith('\n') ? text : text + "\n");
        try
        {
            await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The password passed through here. Not that it can be scrubbed from the string it
            // came from, but this copy at least does not outlive the write.
            Array.Clear(bytes);
        }
    }

    public ValueTask DisposeAsync()
    {
        _input.Dispose();
        _output.Dispose();
        _owner?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A spawned helper: killed if the conversation is abandoned before it finishes.</summary>
    private sealed class Spawned(Process process) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            process.Dispose();
        }
    }
}
