// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using Wlrix.Common;

namespace Wlrix.Writer.Models;

/// <summary>What the editor remembers between runs, as it is written to disk.</summary>
public sealed record WriterStateDocument
{
    public int Version { get; init; } = WriterState.CurrentVersion;

    /// <summary>Recently opened files, most recent first.</summary>
    public List<string> Recent { get; init; } = [];

    /// <summary>What a new document's Tabs settings start as.</summary>
    public TabSettings DefaultTabs { get; init; } = TabSettings.Default;

    /// <summary>Preferences ▸ Show Line Numbers, which applies to every editor at once.</summary>
    public bool ShowLineNumbers { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WriterStateDocument))]
internal sealed partial class WriterJson : JsonSerializerContext;

/// <summary>
/// The editor's state file: <c>&lt;AppData&gt;/writer/state.json</c>.
/// </summary>
/// <remarks>
/// Not <c>wlrix-settings-daemon</c>, for the reason Wlrix.Files gives for its own state: a
/// recent-files list is a convenience, not configuration. Nothing here throws. A missing, corrupt
/// or newer file reads as the defaults and a failed write is reported and forgotten, because
/// none of it is worth refusing to start or losing an edit over.
/// </remarks>
public sealed class WriterState
{
    public const int CurrentVersion = 1;

    private readonly string _path;
    private readonly Action<string>? _warn;

    /// <param name="path">The state file. Defaults to <c>&lt;AppData&gt;/writer/state.json</c>.</param>
    /// <param name="warn">Where to report a file that could not be read or written.</param>
    public WriterState(string? path = null, Action<string>? warn = null)
    {
        _path = path ?? Path.Combine(ApplicationPaths.AppData, "writer", "state.json");
        _warn = warn;
        Document = Read();
    }

    public WriterStateDocument Document { get; private set; }

    /// <summary>Replaces the state and writes it.</summary>
    public void Update(Func<WriterStateDocument, WriterStateDocument> change)
    {
        Document = change(Document) with { Version = CurrentVersion };
        Write();
    }

    private WriterStateDocument Read()
    {
        try
        {
            if (!File.Exists(_path))
                return new WriterStateDocument();

            using var stream = File.OpenRead(_path);
            var document = JsonSerializer.Deserialize(stream, WriterJson.Default.WriterStateDocument);
            if (document is null || document.Version > CurrentVersion)
                return new WriterStateDocument();

            return document with
            {
                Recent = document.Recent ?? [],
                DefaultTabs = (document.DefaultTabs ?? TabSettings.Default).Normalized(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _warn?.Invoke($"could not read {_path}: {ex.Message}");
            return new WriterStateDocument();
        }
    }

    private void Write()
    {
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = File.Create(temporary))
                JsonSerializer.Serialize(stream, Document, WriterJson.Default.WriterStateDocument);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"could not write {_path}: {ex.Message}");
        }
    }
}
