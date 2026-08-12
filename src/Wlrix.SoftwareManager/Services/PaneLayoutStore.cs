using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Wlrix.Common;
using ZLogger;

namespace Wlrix.SoftwareManager.Services;

/// <summary>Which panes the user has left showing. Every one defaults to visible.</summary>
public sealed record PaneLayout
{
    public bool AvailableSoftware { get; init; } = true;
    public bool SoftwareInventory { get; init; } = true;
    public bool StatusDiskSpace { get; init; } = true;
    public bool Command { get; init; } = true;
    public bool Log { get; init; } = true;
}

/// <summary>Reads and writes the Panes menu's state.</summary>
public interface IPaneLayoutStore
{
    PaneLayout Load();

    void Save(PaneLayout layout);
}

/// <summary>
/// Persists the pane layout to <c>&lt;AppData&gt;/software-manager/panes.json</c>, beside the
/// Toolchest's application catalog cache.
///
/// Deliberately not <c>wlrix-settings-daemon</c>: that owns the wlRIX <em>configuration</em>
/// files — the ones the compositor and the session read, and the ones a user hand-edits. Which
/// panes one window happens to have open is neither, and putting it there would mean a schema
/// entry, a validation rule and a reload story for a checkbox.
/// </summary>
public sealed class PaneLayoutStore(ILogger<PaneLayoutStore> logger) : IPaneLayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static string Path =>
        System.IO.Path.Combine(ApplicationPaths.AppData, "software-manager", "panes.json");

    public PaneLayout Load()
    {
        var path = Path;
        if (!File.Exists(path))
            return new PaneLayout();

        try
        {
            return JsonSerializer.Deserialize<PaneLayout>(File.ReadAllText(path), JsonOptions)
                ?? new PaneLayout();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // A layout that will not parse is not worth troubling the user over; the defaults
            // are a perfectly good window, and the next Save overwrites the bad file.
            logger.ZLogWarning(ex, $"Could not read the pane layout; using the defaults.");
            return new PaneLayout();
        }
    }

    public void Save(PaneLayout layout)
    {
        var path = Path;
        try
        {
            ApplicationPaths.EnsureDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(layout, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.ZLogWarning(ex, $"Could not save the pane layout.");
        }
    }
}
