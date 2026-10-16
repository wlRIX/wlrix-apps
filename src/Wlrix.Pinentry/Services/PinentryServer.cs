using System.Globalization;
using System.Text;

namespace Wlrix.Pinentry.Services;

/// <summary>Everything gpg-agent has said about the dialog it is about to ask for.</summary>
internal sealed record DialogText
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Prompt { get; init; }
    public string? Error { get; init; }
    public string? Ok { get; init; }
    public string? Cancel { get; init; }
    public string? NotOk { get; init; }

    /// <summary>Set by SETREPEAT: ask twice, with this label on the second field.</summary>
    public string? Repeat { get; init; }

    public string? RepeatError { get; init; }

    /// <summary>Seconds before the dialog gives up on its own; 0 waits for ever.</summary>
    public int Timeout { get; init; }
}

/// <summary>How a dialog ended.</summary>
internal enum DialogOutcome
{
    Ok,
    NotOk,
    Canceled,
    TimedOut,
}

/// <summary>What a passphrase dialog came to. <see cref="Pin"/> is set only for <see cref="DialogOutcome.Ok"/>.</summary>
internal sealed record PinResult(DialogOutcome Outcome, string? Pin = null, bool Repeated = false);

/// <summary>The dialogs the server asks for: the window, or a test double.</summary>
internal interface IPinentryUi
{
    Task<PinResult> GetPinAsync(DialogText text);

    /// <summary>A question (OK / Not OK / Cancel), or, with <paramref name="oneButton"/>, a message to acknowledge.</summary>
    Task<DialogOutcome> ConfirmAsync(DialogText text, bool oneButton);
}

/// <summary>
/// The pinentry side of gpg-agent's Assuan conversation, over the process's stdin and stdout.
///
/// gpg-agent starts a pinentry for each question, says what it should look like (SETDESC,
/// SETPROMPT, SETOK, the OPTION defaults in the user's language), then asks: GETPIN for a
/// passphrase, CONFIRM for a yes/no, MESSAGE for a notice. Each answer is <c>OK</c>, data lines
/// (<c>D …</c>) then <c>OK</c>, or <c>ERR code text</c>. The command set and the error codes are
/// those of GnuPG's own pinentry (<c>pinentry/pinentry.c</c>), which every agent expects.
///
/// Text in arguments arrives percent-escaped (<c>%0A</c> for a newline), and data lines go back
/// escaped the same way, so a passphrase containing <c>%</c> or a newline survives the trip.
/// </summary>
internal sealed class PinentryServer(TextReader input, TextWriter output, IPinentryUi ui)
{
    /// <summary>libgpg-error's codes, with the pinentry source (5) in the top byte.</summary>
    private const int Source = 5 << 24;

    internal const int Canceled = Source | 99;
    internal const int NotConfirmed = Source | 114;
    internal const int Timeout = Source | 62;
    internal const int UnknownCommand = Source | 275;
    internal const int Parameter = Source | 280;

    /// <summary>Assuan's line limit, which data lines are split to fit.</summary>
    private const int MaximumLine = 1000;

    private DialogText _text = new();
    private readonly Dictionary<string, string> _defaults = new(StringComparer.Ordinal);

    /// <summary>The <c>touch-file</c> option: touched after each dialog closes, so gpg-agent can notice.</summary>
    private string? _touchFile;

    /// <summary>Talks until gpg-agent says BYE or closes the pipe.</summary>
    public async Task RunAsync()
    {
        await SendAsync("OK Pleased to meet you").ConfigureAwait(false);

        while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            var space = line.IndexOf(' ');
            var command = (space < 0 ? line : line[..space]).ToUpperInvariant();
            var argument = space < 0 ? string.Empty : Unescape(line[(space + 1)..]);

            if (command == "BYE")
            {
                await SendAsync("OK closing connection").ConfigureAwait(false);
                return;
            }

            await HandleAsync(command, argument).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(string command, string argument)
    {
        switch (command)
        {
            case "SETDESC":
                _text = _text with { Description = argument };
                break;
            case "SETPROMPT":
                _text = _text with { Prompt = argument };
                break;
            case "SETTITLE":
                _text = _text with { Title = argument };
                break;
            case "SETERROR":
                _text = _text with { Error = argument };
                break;
            case "SETOK":
                _text = _text with { Ok = argument };
                break;
            case "SETCANCEL":
                _text = _text with { Cancel = argument };
                break;
            case "SETNOTOK":
                _text = _text with { NotOk = argument };
                break;
            case "SETREPEAT":
                _text = _text with { Repeat = argument.Length > 0 ? argument : Default("default-repeat") ?? string.Empty };
                break;
            case "SETREPEATERROR":
                _text = _text with { RepeatError = argument };
                break;
            case "SETTIMEOUT":
                _text = _text with
                {
                    Timeout = int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                        ? seconds
                        : 0,
                };
                break;
            case "OPTION":
                Option(argument);
                break;
            case "RESET":
                _text = new DialogText();
                break;
            case "GETINFO":
                await GetInfoAsync(argument).ConfigureAwait(false);
                return;
            case "GETPIN":
                await GetPinAsync().ConfigureAwait(false);
                return;
            case "CONFIRM":
                await ConfirmAsync(oneButton: argument.Contains("--one-button", StringComparison.Ordinal))
                    .ConfigureAwait(false);
                return;
            case "MESSAGE":
                await ConfirmAsync(oneButton: true).ConfigureAwait(false);
                return;
            case "SETKEYINFO" or "SETQUALITYBAR" or "SETQUALITYBAR_TT" or "SETGENPIN" or "SETGENPIN_TT"
                or "SETREPEATOK" or "SETCONSTRAINTS" or "CLEARPASSPHRASE" or "NOP":
                // Accepted and not acted on: the quality bar, generated passphrases, constraint
                // hints and the external password cache are things this pinentry does not offer.
                break;
            default:
                await SendAsync($"ERR {UnknownCommand} Unknown IPC command <Pinentry>").ConfigureAwait(false);
                return;
        }

        await SendAsync("OK").ConfigureAwait(false);
    }

    /// <summary>
    /// <c>OPTION name[=value]</c>. The <c>default-*</c> ones are gpg-agent's labels in the user's
    /// language; the rest (tty, display, locale) are for terminal pinentries and accepted as is.
    /// </summary>
    private void Option(string argument)
    {
        var (name, value) = argument.Split('=', 2) is [var n, var v] ? (n.Trim(), v) : (argument.Trim(), string.Empty);
        if (name.StartsWith("default-", StringComparison.Ordinal))
            _defaults[name] = value;
        else if (name == "touch-file")
            _touchFile = value;
    }

    private string? Default(string name) => _defaults.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    /// <summary>The text as the dialog should show it: what was set, else gpg-agent's defaults.</summary>
    private DialogText Effective() => _text with
    {
        Prompt = _text.Prompt ?? Default("default-prompt"),
        Ok = _text.Ok ?? Default("default-ok"),
        Cancel = _text.Cancel ?? Default("default-cancel"),
    };

    private async Task GetInfoAsync(string what)
    {
        string? answer = what switch
        {
            "flavor" => "wlrix",
            "version" => "0.1.0",
            "pid" => Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "ttyinfo" => "- - - - 0/0 -",
            _ => null,
        };

        if (answer is null)
        {
            await SendAsync($"ERR {Parameter} IPC parameter error <Pinentry>").ConfigureAwait(false);
            return;
        }

        await SendDataAsync(answer).ConfigureAwait(false);
        await SendAsync("OK").ConfigureAwait(false);
    }

    private async Task GetPinAsync()
    {
        var result = await ui.GetPinAsync(Effective()).ConfigureAwait(false);
        // An error is shown once, for the attempt it describes.
        _text = _text with { Error = null };
        Touch();

        switch (result.Outcome)
        {
            case DialogOutcome.Ok:
                if (result.Repeated)
                    await SendAsync("S PIN_REPEATED").ConfigureAwait(false);
                if (result.Pin is { Length: > 0 } pin)
                    await SendDataAsync(pin).ConfigureAwait(false);
                await SendAsync("OK").ConfigureAwait(false);
                break;
            case DialogOutcome.TimedOut:
                await SendAsync($"ERR {Timeout} Timeout <Pinentry>").ConfigureAwait(false);
                break;
            default:
                await SendAsync($"ERR {Canceled} Operation cancelled <Pinentry>").ConfigureAwait(false);
                break;
        }
    }

    private async Task ConfirmAsync(bool oneButton)
    {
        var outcome = await ui.ConfirmAsync(Effective(), oneButton).ConfigureAwait(false);
        _text = _text with { Error = null };
        Touch();

        var reply = outcome switch
        {
            DialogOutcome.Ok => "OK",
            DialogOutcome.NotOk => $"ERR {NotConfirmed} Not confirmed <Pinentry>",
            DialogOutcome.TimedOut => $"ERR {Timeout} Timeout <Pinentry>",
            _ => $"ERR {Canceled} Operation cancelled <Pinentry>",
        };
        await SendAsync(reply).ConfigureAwait(false);
    }

    private void Touch()
    {
        if (_touchFile is not { Length: > 0 } path)
            return;
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Only a hint to gpg-agent; nothing depends on it.
        }
    }

    /// <summary><c>D</c> lines, escaped and split to Assuan's line length.</summary>
    private async Task SendDataAsync(string data)
    {
        var escaped = Escape(data);
        const int room = MaximumLine - 3;
        var start = 0;
        while (start < escaped.Length)
        {
            var length = Math.Min(room, escaped.Length - start);
            // Never split an escape sequence across two lines.
            while (length > 2 && start + length < escaped.Length
                   && (escaped[start + length - 1] == '%' || escaped[start + length - 2] == '%'))
                length--;
            await SendAsync("D " + escaped.Substring(start, length)).ConfigureAwait(false);
            start += length;
        }
    }

    private async Task SendAsync(string line)
    {
        await output.WriteAsync(line + "\n").ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Assuan's escaping for data: <c>%</c>, CR and LF as <c>%XX</c>.</summary>
    internal static string Escape(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '%': result.Append("%25"); break;
                case '\r': result.Append("%0D"); break;
                case '\n': result.Append("%0A"); break;
                default: result.Append(c); break;
            }
        }

        return result.ToString();
    }

    /// <summary>Decodes <c>%XX</c> in an argument, as UTF-8 bytes.</summary>
    internal static string Unescape(string text)
    {
        if (!text.Contains('%'))
            return text;

        var bytes = new List<byte>(text.Length);
        var source = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '%' && i + 2 < source.Length
                && byte.TryParse(Encoding.ASCII.GetString(source, i + 1, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var value))
            {
                bytes.Add(value);
                i += 2;
            }
            else
            {
                bytes.Add(source[i]);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
