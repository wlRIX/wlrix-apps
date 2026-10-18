using System.Text;
using System.Text.Json;

namespace Wlrix.NetworkPrompt.Protocol;

/// <summary>What the dialog is for.</summary>
internal enum PromptMode
{
    /// <summary>A known network wants its password.</summary>
    Password,

    /// <summary>Join Other Network: a hidden network, named and described by hand.</summary>
    Other,
}

/// <summary>A network's security, by the name NetworkManager's <c>key-mgmt</c> uses.</summary>
internal enum Security
{
    None,
    WpaPsk,
    Sae,
    Wep,
}

/// <summary>
/// The request <c>wlrix-network</c> writes to stdin, one line of JSON.
/// </summary>
/// <remarks>
/// <code>
/// {"mode":"password","ssid":"HomeNet","security":"wpa-psk","retry":false}
/// {"mode":"other"}
/// </code>
/// The answer goes back as one line on stdout (<see cref="PromptAnswer"/>), and Cancel is an exit
/// code of 1 with nothing written -- so a password never leaves this process except down the
/// pipe that asked for it. JSON rather than an Assuan conversation like pinentry's: there is one
/// question and one answer, and both ends are wlRIX's.
/// </remarks>
internal sealed record PromptRequest(PromptMode Mode, string Ssid, Security Security, bool Retry)
{
    public static PromptRequest Parse(string line)
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
                "password" => PromptMode.Password,
                "other" => PromptMode.Other,
                var other => throw new FormatException($"unknown mode {other ?? "(none)"}"),
            };
            var ssid = Text(root, "ssid") ?? string.Empty;
            if (mode == PromptMode.Password && ssid.Length == 0)
                throw new FormatException("a password request needs an ssid");
            var security = Text(root, "security") is { } name
                ? SecurityNames.Parse(name) ?? throw new FormatException($"unknown security {name}")
                : Security.WpaPsk;
            var retry = root.TryGetProperty("retry", out var flag) && flag.ValueKind == JsonValueKind.True;
            return new PromptRequest(mode, ssid, security, retry);
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>What the person entered.</summary>
internal sealed record PromptAnswer(string Ssid, Security Security, string Password)
{
    /// <summary>One line of JSON, with no line break inside it whatever the password holds.</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("ssid", Ssid);
            writer.WriteString("security", SecurityNames.Name(Security));
            writer.WriteString("password", Password);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>The wire spelling of <see cref="Security"/>.</summary>
internal static class SecurityNames
{
    public static Security? Parse(string name) => name switch
    {
        "none" => Security.None,
        "wpa-psk" => Security.WpaPsk,
        "sae" => Security.Sae,
        "wep" => Security.Wep,
        _ => null,
    };

    public static string Name(Security security) => security switch
    {
        Security.None => "none",
        Security.WpaPsk => "wpa-psk",
        Security.Sae => "sae",
        Security.Wep => "wep",
        _ => throw new ArgumentOutOfRangeException(nameof(security)),
    };
}

/// <summary>Why a key cannot be right, before NetworkManager is asked to try it.</summary>
internal enum KeyProblem
{
    None,
    Empty,
    TooShort,
    TooLong,
    NotAscii,
    WepLength,
}

/// <summary>
/// What each kind of network accepts as a key -- checked here so a typo in the length is
/// caught in the dialog rather than after a failed handshake and another dialog.
/// </summary>
internal static class Keys
{
    public static KeyProblem Check(Security security, string key)
    {
        switch (security)
        {
            case Security.None:
                return KeyProblem.None;
            case Security.WpaPsk:
                // 8 to 63 printable ASCII characters, or the 256-bit key itself as 64 hex digits.
                if (key.Length == 64 && key.All(Uri.IsHexDigit))
                    return KeyProblem.None;
                if (key.Length == 0)
                    return KeyProblem.Empty;
                if (!key.All(c => c is >= ' ' and <= '~'))
                    return KeyProblem.NotAscii;
                return key.Length switch
                {
                    < 8 => KeyProblem.TooShort,
                    > 63 => KeyProblem.TooLong,
                    _ => KeyProblem.None,
                };
            case Security.Sae:
                // WPA3's SAE takes a password of any length and any characters.
                return key.Length == 0 ? KeyProblem.Empty : KeyProblem.None;
            case Security.Wep:
                // 40- or 104-bit keys, as 5 or 13 characters or 10 or 26 hex digits.
                if (key.Length == 0)
                    return KeyProblem.Empty;
                var hex = key.All(Uri.IsHexDigit);
                return key.Length is 5 or 13 || (hex && key.Length is 10 or 26)
                    ? KeyProblem.None
                    : KeyProblem.WepLength;
            default:
                return KeyProblem.None;
        }
    }

    /// <summary>An SSID is at most 32 bytes -- bytes, so a name in Japanese runs out sooner.</summary>
    public static bool NameFits(string ssid) =>
        ssid.Length > 0 && Encoding.UTF8.GetByteCount(ssid) <= 32;
}
