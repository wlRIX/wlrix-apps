// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using Wlrix.Common;

namespace Wlrix.Settings.Audio.Services;

/// <summary>How the panel shows one device, as opposed to how the device is set.</summary>
public sealed record ColumnState(bool Grouped = true, bool Metering = false);

/// <summary>
/// What the panel remembers between runs, per device: whether its sliders are grouped and
/// whether its meters are on. None of it is the sound server's business, so it lives in the
/// panel's own file, <c>~/.local/share/wlrix/settings-audio.json</c>.
/// </summary>
public class PanelState
{
    private readonly string? _path;
    private readonly Dictionary<string, ColumnState> _columns;

    /// <summary>A state that is never saved, for <c>--demo</c> and the tests.</summary>
    public PanelState() => _columns = [];

    private PanelState(string path, Dictionary<string, ColumnState> columns)
    {
        _path = path;
        _columns = columns;
    }

    public static PanelState Load()
    {
        var path = Path.Combine(ApplicationPaths.AppData, "settings-audio.json");
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize(File.ReadAllText(path), PanelStateJson.Default.DictionaryStringColumnState)
                    is { } columns)
            {
                return new PanelState(path, columns);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged or unreadable file costs the remembered toggles, nothing more.
        }
        return new PanelState(path, []);
    }

    public ColumnState Get(string key) => _columns.GetValueOrDefault(key) ?? new ColumnState();

    public void Set(string key, ColumnState state)
    {
        _columns[key] = state;
        if (_path is null)
            return;
        try
        {
            ApplicationPaths.EnsureDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_columns, PanelStateJson.Default.DictionaryStringColumnState));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembered this time; the panel still works.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, ColumnState>))]
internal sealed partial class PanelStateJson : JsonSerializerContext;
