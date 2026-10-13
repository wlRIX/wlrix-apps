// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;

namespace Wlrix.Settings.Sound.Services;

/// <summary>An output a desktop sound can be played through: a PipeWire sink.</summary>
/// <param name="Name">Its <c>node.name</c>, which is what <c>sounds.toml</c>'s <c>output</c> holds.</param>
/// <param name="Description">What a person calls it.</param>
public sealed record AudioOutput(string Name, string Description);

/// <summary>
/// The two things this panel asks of the sound server: the outputs there are, and whether the
/// default one is muted.
/// </summary>
/// <remarks>
/// Through PipeWire's own command-line tools, <c>pw-dump</c> and <c>wpctl</c>, as
/// <c>Wlrix.Settings.Audio</c> reaches <c>pw-metadata</c> for the sample rate. Neither answer
/// is worth a client library in this process: the list is read when the window opens or comes
/// forward, and the mute is one flag. Every failure is an empty answer, never an exception --
/// a session without PipeWire still gets the rest of the panel.
/// </remarks>
public static class Pipewire
{
    /// <summary>What <c>wpctl</c> calls the default output, wherever it is.</summary>
    private const string DefaultSink = "@DEFAULT_AUDIO_SINK@";

    /// <summary>Every sink, by description.</summary>
    public static async Task<IReadOnlyList<AudioOutput>> ListOutputsAsync()
    {
        var json = await RunAsync("pw-dump").ConfigureAwait(false);
        return json is null ? [] : ParseSinks(json);
    }

    /// <summary>
    /// The sinks in a <c>pw-dump</c>: nodes whose <c>media.class</c> is <c>Audio/Sink</c>.
    /// Split from <see cref="ListOutputsAsync"/> so it can be tested on a saved dump.
    /// </summary>
    public static IReadOnlyList<AudioOutput> ParseSinks(string json)
    {
        var outputs = new List<AudioOutput>();
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("info", out var info)
                    || info.ValueKind != JsonValueKind.Object
                    || !info.TryGetProperty("props", out var props)
                    || props.ValueKind != JsonValueKind.Object)
                    continue;
                if (Text(props, "media.class") != "Audio/Sink" || Text(props, "node.name") is not { } name)
                    continue;
                outputs.Add(new AudioOutput(name, Text(props, "node.description") ?? Text(props, "node.nick") ?? name));
            }
        }
        catch (JsonException)
        {
            return [];
        }
        outputs.Sort((a, b) => string.Compare(a.Description, b.Description, StringComparison.CurrentCultureIgnoreCase));
        return outputs;
    }

    /// <summary>Whether the default output is muted; null when there is no answer.</summary>
    public static async Task<bool?> IsMutedAsync()
    {
        var answer = await RunAsync("wpctl", "get-volume", DefaultSink).ConfigureAwait(false);
        return answer is null ? null : ParseMuted(answer);
    }

    /// <summary><c>Volume: 0.40 [MUTED]</c> is muted; <c>Volume: 0.40</c> is not.</summary>
    public static bool? ParseMuted(string answer) =>
        answer.StartsWith("Volume:", StringComparison.Ordinal)
            ? answer.Contains("[MUTED]", StringComparison.Ordinal)
            : null;

    /// <summary>Mute or unmute the default output. Returns whether it worked.</summary>
    public static async Task<bool> SetMutedAsync(bool muted) =>
        await RunAsync("wpctl", "set-mute", DefaultSink, muted ? "1" : "0").ConfigureAwait(false) is not null;

    private static string? Text(JsonElement props, string key) =>
        props.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A command's output, or null if it would not run or failed.</summary>
    private static async Task<string?> RunAsync(string program, params string[] args)
    {
        var info = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        // A sink's description is the user's language; the command's own words are matched on.
        info.Environment["LC_ALL"] = "C";

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or OperationCanceledException or IOException)
        {
            return null;
        }
    }
}
