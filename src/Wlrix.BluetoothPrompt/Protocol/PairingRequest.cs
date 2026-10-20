using System.Text.Json;

namespace Wlrix.BluetoothPrompt.Protocol;

/// <summary>What bluetoothd asked, through <c>wlrix-bluetooth</c>'s agent.</summary>
internal enum PairingMode
{
    /// <summary>Numeric comparison: does the device show the same number?</summary>
    Confirm,

    /// <summary>Type this code on the device -- a keyboard being paired. Nothing to answer.</summary>
    Display,

    /// <summary>The PIN a legacy device wants.</summary>
    Pin,

    /// <summary>The six-digit passkey the device shows.</summary>
    Passkey,

    /// <summary>A paired, untrusted device wants to connect, or to use a service.</summary>
    Authorize,
}

/// <summary>
/// The request <c>wlrix-bluetooth</c> writes to stdin, one line of JSON.
/// </summary>
/// <remarks>
/// <code>
/// {"mode":"confirm","device":"WH-1000XM4","passkey":"012345"}
/// {"mode":"display","device":"K380","code":"123456"}
/// {"mode":"pin","device":"Old Keyboard"}
/// {"mode":"passkey","device":"Old Keyboard"}
/// {"mode":"authorize","device":"Pixel 8","service":"audio"}
/// </code>
/// The answer goes back as one line on stdout (<see cref="PairingAnswer"/>), and Cancel is an exit
/// code of 1 with nothing written, which the agent passes on to bluetoothd as a refusal. A
/// <c>display</c> dialog never answers: the daemon closes it when the pairing is over.
/// </remarks>
internal sealed record PairingRequest(PairingMode Mode, string Device, string Code, string? Service)
{
    public static PairingRequest Parse(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException err)
        {
            throw new FormatException($"the request is not JSON: {err.Message}", err);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("the request is not a JSON object");

            var mode = Text(root, "mode") switch
            {
                "confirm" => PairingMode.Confirm,
                "display" => PairingMode.Display,
                "pin" => PairingMode.Pin,
                "passkey" => PairingMode.Passkey,
                "authorize" => PairingMode.Authorize,
                var other => throw new FormatException($"unknown mode {other ?? "(none)"}"),
            };
            var device = Text(root, "device") is { Length: > 0 } name
                ? name
                : throw new FormatException("a request needs a device");
            var code = mode switch
            {
                PairingMode.Confirm => Text(root, "passkey"),
                PairingMode.Display => Text(root, "code"),
                _ => string.Empty,
            } ?? throw new FormatException($"a {mode} request needs its code");
            return new PairingRequest(mode, device, code, Text(root, "service"));
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// The answer, written to stdout as one line of JSON: <c>{"accept":true}</c>, <c>{"pin":"0000"}</c>
/// or <c>{"passkey":123456}</c>.
/// </summary>
internal sealed record PairingAnswer(bool? Accept = null, string? Pin = null, uint? Passkey = null)
{
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (Accept is { } accept)
                writer.WriteBoolean("accept", accept);
            if (Pin is { } pin)
                writer.WriteString("pin", pin);
            if (Passkey is { } passkey)
                writer.WriteNumber("passkey", passkey);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>What a PIN or a passkey may be.</summary>
internal static class Codes
{
    /// <summary>Legacy PINs are 1 to 16 characters, and nearly always digits.</summary>
    public static bool PinFits(string pin) => pin.Length is >= 1 and <= 16;

    /// <summary>A passkey is up to six digits.</summary>
    public static uint? Passkey(string text) =>
        text.Length is >= 1 and <= 6 && text.All(char.IsAsciiDigit) ? uint.Parse(text) : null;

    /// <summary>A six-digit code as a device shows it, in two groups of three: <c>012 345</c>.</summary>
    public static string Grouped(string code) =>
        code.Length == 6 && code.All(char.IsAsciiDigit) ? $"{code[..3]} {code[3..]}" : code;
}
