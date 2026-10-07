// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Wlrix.Settings.Audio.Services;

/// <summary>
/// PipeWire's graph sample rate, through the <c>settings</c> metadata object, which is what
/// <c>pw-metadata -n settings 0 clock.force-rate 44100</c> changes.
///
/// libpulse has no call for this: a PulseAudio sink's rate is fixed when its module loads. It
/// is a single key, so binding libpipewire for it would be all cost; <c>pw-metadata</c> ships
/// with PipeWire itself, so wherever there is a rate to set there is the tool to set it.
/// The rate is global, for the whole graph, which is why every column's Sample Rate menu
/// shows the same choice.
/// </summary>
internal static partial class PipeWireRates
{
    /// <summary>What the settings currently say, or null if <c>pw-metadata</c> could not be run.</summary>
    public static async Task<(IReadOnlyList<int> Allowed, int Forced)?> ReadAsync()
    {
        var output = await RunAsync("-n", "settings").ConfigureAwait(false);
        return output is null ? null : Parse(output);
    }

    /// <summary>Force the graph to <paramref name="rate"/> Hz, or 0 to let it choose.</summary>
    public static Task SetForcedAsync(int rate) =>
        RunAsync("-n", "settings", "0", "clock.force-rate",
            rate.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Reads the dump <c>pw-metadata -n settings</c> prints, one line per key:
    /// <c>update: id:0 key:'clock.allowed-rates' value:'[ 44100 48000 ]' type:''</c>.
    /// A missing allowed-rates means only the default rate, 48 kHz.
    /// </summary>
    internal static (IReadOnlyList<int> Allowed, int Forced) Parse(string output)
    {
        var allowed = new List<int>();
        var forced = 0;
        foreach (Match match in KeyValue().Matches(output))
        {
            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value;
            if (key == "clock.allowed-rates")
            {
                allowed.AddRange(Number().Matches(value)
                    .Select(number => int.Parse(number.Value, CultureInfo.InvariantCulture)));
            }
            else if (key == "clock.force-rate")
            {
                _ = int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out forced);
            }
        }

        if (allowed.Count == 0)
            allowed.Add(48000);
        return (allowed, forced);
    }

    private static async Task<string?> RunAsync(params string[] arguments)
    {
        var info = new ProcessStartInfo("pw-metadata")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return null;
            // pw-metadata dumps and exits; the timeout is for a PipeWire that is not answering.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"key:'(?<key>[^']*)'\s+value:'(?<value>[^']*)'")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();
}
