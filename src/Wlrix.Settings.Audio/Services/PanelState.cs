// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using Wlrix.Common;

namespace Wlrix.Settings.Audio.Services;

/// <summary>How the panel shows one device, as opposed to how the device is set.</summary>
/// <param name="Grouped">Whether its left and right sliders move together.</param>
/// <param name="Metering">Whether its meters are on (inputs only).</param>
/// <param name="Visible">Whether its column is shown, from its View menu toggle.</param>
public sealed record ColumnState(bool Grouped = true, bool Metering = false, bool Visible = true);

/// <summary>The panel's file, as it is written.</summary>
internal sealed class PanelSettings
{
    public Dictionary<string, ColumnState> Columns { get; set; } = [];
    public bool ShowDefaultInput { get; set; } = true;
    public bool ShowDefaultOutput { get; set; } = true;
    public bool ShowQuickHelp { get; set; } = true;
}

/// <summary>
/// What the panel remembers between runs: per device, whether its sliders are grouped, whether
/// its meters are on and whether it is shown; and the View and Options toggles. None of it is
/// the sound server's business, so it lives in the panel's own file,
/// <c>~/.local/share/wlrix/settings-audio.json</c>. It is written on every change, so closing
/// the panel any way at all, a crash included, leaves it as it was last seen.
///
/// A device never seen before is shown: the View menu is for putting away what is not wanted,
/// and a newly plugged-in device that stayed hidden would look like one the panel had missed.
/// </summary>
public class PanelState
{
    private readonly string? _path;
    private readonly PanelSettings _settings;

    /// <summary>A state that is never saved, for <c>--demo</c> and the tests.</summary>
    public PanelState() => _settings = new PanelSettings();

    private PanelState(string path, PanelSettings settings)
    {
        _path = path;
        _settings = settings;
    }

    public static PanelState Load() =>
        Load(Path.Combine(ApplicationPaths.AppData, "settings-audio.json"));

    /// <summary>The state kept in <paramref name="path"/>, saved back there on every change.</summary>
    internal static PanelState Load(string path)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize(File.ReadAllText(path), PanelStateJson.Default.PanelSettings)
                    is { } settings)
            {
                return new PanelState(path, settings);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged or unreadable file costs the remembered toggles, nothing more.
        }
        return new PanelState(path, new PanelSettings());
    }

    public ColumnState Get(string key) => _settings.Columns.GetValueOrDefault(key) ?? new ColumnState();

    public void Set(string key, ColumnState state)
    {
        _settings.Columns[key] = state;
        Save();
    }

    /// <summary>View → Default Input: always show whichever input is the default.</summary>
    public bool ShowDefaultInput
    {
        get => _settings.ShowDefaultInput;
        set
        {
            _settings.ShowDefaultInput = value;
            Save();
        }
    }

    /// <summary>View → Default Output: always show whichever output is the default.</summary>
    public bool ShowDefaultOutput
    {
        get => _settings.ShowDefaultOutput;
        set
        {
            _settings.ShowDefaultOutput = value;
            Save();
        }
    }

    /// <summary>Options → Show Quick Help: the hint line along the bottom.</summary>
    public bool ShowQuickHelp
    {
        get => _settings.ShowQuickHelp;
        set
        {
            _settings.ShowQuickHelp = value;
            Save();
        }
    }

    private void Save()
    {
        if (_path is null)
            return;
        try
        {
            ApplicationPaths.EnsureDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_settings, PanelStateJson.Default.PanelSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembered this time; the panel still works.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PanelSettings))]
internal sealed partial class PanelStateJson : JsonSerializerContext;
