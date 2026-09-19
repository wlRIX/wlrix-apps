using System.Text.Json;
using System.Text.Json.Serialization;
using Wlrix.Common;
using Wlrix.Desks.Models;

namespace Wlrix.Desks.Services;

/// <summary>What the overview looked like when it was last closed.</summary>
/// <remarks>
/// Everything a person can change from the Overview menu, plus the window's size. All of it is
/// written back on close, whether it got there from a menu item or from the command line: a
/// flag is how you ask for something, not a separate kind of state.
/// </remarks>
public sealed record DesksSettings
{
    /// <summary>The schema this document was written with.</summary>
    public int Version { get; init; } = DesksSettingsStore.CurrentVersion;

    public bool ShowSnapshots { get; init; } = true;

    public bool ShowGlobalDesk { get; init; } = true;

    public WindowLabel WindowLabel { get; init; } = WindowLabel.Title;

    /// <summary>The window's size. Defaults match <c>MainWindow.axaml</c>.</summary>
    public double WindowWidth { get; init; } = DesksSettingsStore.DefaultWindowWidth;

    public double WindowHeight { get; init; } = DesksSettingsStore.DefaultWindowHeight;
}

/// <summary>Reads and writes what the overview remembers between runs.</summary>
public interface IDesksSettingsStore
{
    DesksSettings Load();

    void Save(DesksSettings settings);
}

/// <summary>Persists the overview's own state to <c>&lt;AppData&gt;/desks/ui.json</c>.</summary>
/// <remarks>
/// Deliberately not <c>wlrix-settings-daemon</c>: that owns the wlRIX <em>configuration</em> —
/// the files the compositor and the session read, and the ones a user hand-edits. Which
/// toggles one window was left on is neither, and putting it there would mean a schema entry, a
/// validation rule and a reload story for a checkbox. The Software Manager's pane layout is
/// kept next door for exactly the same reasons.
///
/// <para>
/// Nothing here throws. A read-only home, a corrupt file, a document from a newer build: each
/// degrades to the defaults, because this is a convenience and none of it is worth refusing to
/// start over.
/// </para>
/// </remarks>
public sealed class DesksSettingsStore : IDesksSettingsStore
{
    /// <summary>The schema version every document is written with.</summary>
    public const int CurrentVersion = 1;

    internal const double DefaultWindowWidth = 720;
    internal const double DefaultWindowHeight = 230;

    // A window small enough to have no usable content, or larger than any plausible desktop,
    // is not worth restoring: it would be a window the user cannot find or cannot use, from a
    // file they never asked to be honored that literally.
    private const double MinimumWindowSize = 160;
    private const double MaximumWindowSize = 16384;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // As a name rather than an ordinal: this file is small enough to read, and a person
        // looking into it should not have to count the enum's members to learn what 1 means.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Action<string>? _warn;
    private DesksSettings? _written;

    /// <param name="root">Where <c>ui.json</c> lives. Defaults to <c>&lt;AppData&gt;/desks</c>.</param>
    /// <param name="warn">Where to report a file that could not be read or written. Null discards.</param>
    public DesksSettingsStore(string? root = null, Action<string>? warn = null)
    {
        Path = System.IO.Path.Combine(root ?? System.IO.Path.Combine(ApplicationPaths.AppData, "desks"), "ui.json");
        _warn = warn;
    }

    /// <summary>The file this store reads and writes.</summary>
    public string Path { get; }

    public DesksSettings Load()
    {
        if (!File.Exists(Path))
            return new DesksSettings();

        DesksSettings? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<DesksSettings>(File.ReadAllText(Path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _warn?.Invoke($"Could not read {Path}; using the defaults. {ex.Message}");
            return new DesksSettings();
        }

        // A document from a build that came after this one is not ours to guess at.
        if (loaded is null || loaded.Version > CurrentVersion)
            return new DesksSettings();

        var settings = loaded with
        {
            WindowWidth = Size(loaded.WindowWidth, DefaultWindowWidth),
            WindowHeight = Size(loaded.WindowHeight, DefaultWindowHeight),
        };

        // Remember what is on disk, so an unchanged Save does no work.
        _written = settings;
        return settings;
    }

    public void Save(DesksSettings settings)
    {
        // Sanitized on the way out as well as on the way in, so this never writes a size it
        // would refuse to read back. Not fastidiousness: Avalonia's Width and Height are NaN
        // until something sets them, and System.Text.Json throws rather than write one -- out
        // of Save, which runs while the window is closing, and past a catch that is looking
        // for a filesystem problem.
        var document = settings with
        {
            Version = CurrentVersion,
            WindowWidth = Size(settings.WindowWidth, DefaultWindowWidth),
            WindowHeight = Size(settings.WindowHeight, DefaultWindowHeight),
        };
        if (document == _written)
            return;

        try
        {
            ApplicationPaths.EnsureDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(document, JsonOptions));
            _written = document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"Could not save {Path}. {ex.Message}");
        }
    }

    private static double Size(double value, double fallback) =>
        double.IsFinite(value) && value is >= MinimumWindowSize and <= MaximumWindowSize ? value : fallback;
}
