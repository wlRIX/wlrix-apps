using System.Diagnostics;
using System.Text.Json;

namespace Wlrix.Settings.Network.Services;

/// <summary>
/// The Wi-Fi password dialog, <c>wlrix-network-prompt</c> -- the same one <c>wlrix-network</c>
/// puts up when a connection needs a key, so changing a password here looks like typing one in
/// the first time.
/// </summary>
/// <remarks>
/// One line of JSON to it, one line back, and Cancel is an exit with nothing written (see that
/// program's <c>PromptRequest</c>). The password exists in this process only on its way to
/// NetworkManager.
/// </remarks>
internal static class PasswordPrompt
{
    private const string Program = "wlrix-network-prompt";

    /// <summary>The wire name of a security type, or null for one the dialog cannot ask about.</summary>
    internal static string? WireName(NetworkSecurity security) => security switch
    {
        NetworkSecurity.WpaPsk => "wpa-psk",
        NetworkSecurity.Sae => "sae",
        NetworkSecurity.Wep => "wep",
        _ => null,
    };

    /// <summary>The request line for a network.</summary>
    internal static string Request(string ssid, NetworkSecurity security)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("mode", "password");
            writer.WriteString("ssid", ssid);
            writer.WriteString("security", WireName(security) ?? "wpa-psk");
            writer.WriteBoolean("retry", false);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The password from the dialog's answer, or null for anything that is not one.</summary>
    internal static string? Password(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return null;
        try
        {
            using var document = JsonDocument.Parse(answer);
            return document.RootElement.TryGetProperty("password", out var password)
                && password.ValueKind == JsonValueKind.String
                    ? password.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Ask for a new password. Null for Cancel, or a dialog that would not start.</summary>
    public static async Task<string?> AskAsync(string ssid, NetworkSecurity security)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("WLRIX_NETWORK_PROMPT") ?? Program)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{Program} did not start");
        await process.StandardInput.WriteLineAsync(Request(ssid, security)).ConfigureAwait(false);
        process.StandardInput.Close();
        var answer = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode == 0 ? Password(answer) : null;
    }
}
