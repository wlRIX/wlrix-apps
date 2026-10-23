// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using Wlrix.Common;

namespace Wlrix.IconCatalog.Models;

/// <summary>What an icon on a page stands for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ItemKind>))]
public enum ItemKind
{
    /// <summary>An installed application, by desktop-file id.</summary>
    Application,

    /// <summary>A file the user dropped on a page, by absolute path: a launcher, a program, anything.</summary>
    File,
}

/// <summary>One icon a page keeps, as it is written to disk.</summary>
/// <param name="Kind">What <paramref name="Target"/> is.</param>
/// <param name="Target">A desktop-file id, or an absolute path.</param>
public sealed record ItemRef(ItemKind Kind, string Target)
{
    /// <summary>One string per item, unique across both kinds: a desktop id never starts with '/'.</summary>
    [JsonIgnore]
    public string Key => Target;

    public static ItemRef Application(string desktopId) => new(ItemKind.Application, desktopId);

    public static ItemRef File(string path) => new(ItemKind.File, path);
}

/// <summary>A page the user made.</summary>
public sealed class CustomPage
{
    /// <summary>Stable across renames, so the last-viewed page survives one.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>The icons, in the order they were added.</summary>
    public List<ItemRef> Items { get; set; } = [];
}

/// <summary>What the user changed on one of the system pages.</summary>
public sealed class SystemPageEdits
{
    /// <summary>Icons dropped onto the page, beyond what the installed applications put there.</summary>
    public List<ItemRef> Added { get; set; } = [];

    /// <summary>
    /// Keys of installed applications removed from the page. Dropping one back un-hides it.
    /// </summary>
    public List<string> Hidden { get; set; } = [];
}

/// <summary>What the icon catalog remembers between runs.</summary>
/// <remarks>
/// Mutable on purpose: every change is one small edit to one page, made through the methods
/// below so the rules -- a drop un-hides, a page holds an item once -- live in one tested place.
/// </remarks>
public sealed class CatalogDocument
{
    public int Version { get; set; } = CatalogState.CurrentVersion;

    /// <summary>The user's pages, in the order they were made.</summary>
    public List<CustomPage> CustomPages { get; set; } = [];

    /// <summary>Edits to system pages, keyed by page id.</summary>
    public Dictionary<string, SystemPageEdits> SystemPages { get; set; } = [];

    /// <summary>The page showing at exit, so the catalog reopens where it was left.</summary>
    public string? LastPage { get; set; }

    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    public CustomPage? Custom(string id) => CustomPages.FirstOrDefault(page => page.Id == id);

    /// <summary>Makes a new, empty page.</summary>
    public CustomPage AddPage(string name)
    {
        var page = new CustomPage { Name = name };
        CustomPages.Add(page);
        return page;
    }

    /// <summary>
    /// Puts <paramref name="item"/> on a page. True if the page shows something it did not
    /// before; false if it was already there.
    /// </summary>
    /// <param name="installedThere">
    /// Whether the installed applications already put this item on the (system) page, in which
    /// case dropping it there only un-hides it.
    /// </param>
    public bool AddItem(string pageId, bool isSystem, ItemRef item, bool installedThere)
    {
        if (!isSystem)
        {
            if (Custom(pageId) is not { } page || page.Items.Any(i => i.Key == item.Key))
                return false;
            page.Items.Add(item);
            return true;
        }

        if (SystemPages.TryGetValue(pageId, out var existing))
        {
            if (existing.Hidden.Remove(item.Key))
            {
                Tidy(pageId);
                return true;
            }
            if (existing.Added.Any(i => i.Key == item.Key))
                return false;
        }

        if (installedThere)
            return false;
        Edits(pageId).Added.Add(item);
        return true;
    }

    /// <summary>Takes an item off a page: removed outright if the user put it there, else hidden.</summary>
    public void RemoveItem(string pageId, bool isSystem, string key)
    {
        if (!isSystem)
        {
            Custom(pageId)?.Items.RemoveAll(i => i.Key == key);
            return;
        }

        var edits = Edits(pageId);
        if (edits.Added.RemoveAll(i => i.Key == key) == 0 && !edits.Hidden.Contains(key))
            edits.Hidden.Add(key);
        Tidy(pageId);
    }

    private SystemPageEdits Edits(string pageId)
    {
        if (!SystemPages.TryGetValue(pageId, out var edits))
            SystemPages[pageId] = edits = new SystemPageEdits();
        return edits;
    }

    // An entry with nothing in it is noise in the file.
    private void Tidy(string pageId)
    {
        if (SystemPages.TryGetValue(pageId, out var edits) && edits.Added.Count == 0 && edits.Hidden.Count == 0)
            SystemPages.Remove(pageId);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CatalogDocument))]
internal sealed partial class CatalogJson : JsonSerializerContext;

/// <summary>
/// The catalog's state file: <c>&lt;AppData&gt;/icon-catalog/state.json</c>.
/// </summary>
/// <remarks>
/// Not <c>wlrix-settings-daemon</c>, for the reason Wlrix.Writer gives for its own state: this
/// is what one application remembers, not configuration the desktop shares. Nothing here throws.
/// A missing, corrupt or newer file reads as the defaults and a failed write is reported and
/// forgotten.
/// </remarks>
public sealed class CatalogState
{
    public const int CurrentVersion = 1;

    private readonly string _path;
    private readonly Action<string>? _warn;

    /// <param name="path">The state file. Defaults to <c>&lt;AppData&gt;/icon-catalog/state.json</c>.</param>
    /// <param name="warn">Where to report a file that could not be read or written.</param>
    public CatalogState(string? path = null, Action<string>? warn = null)
    {
        _path = path ?? Path.Combine(ApplicationPaths.AppData, "icon-catalog", "state.json");
        _warn = warn;
        Document = Read();
    }

    public CatalogDocument Document { get; private set; }

    /// <summary>Changes the state and writes it.</summary>
    public void Change(Action<CatalogDocument> change)
    {
        change(Document);
        Document.Version = CurrentVersion;
        Write();
    }

    private CatalogDocument Read()
    {
        try
        {
            if (!File.Exists(_path))
                return new CatalogDocument();

            using var stream = File.OpenRead(_path);
            var document = JsonSerializer.Deserialize(stream, CatalogJson.Default.CatalogDocument);
            if (document is null || document.Version > CurrentVersion)
                return new CatalogDocument();

            // A hand-edited file can null out any list; every reader below assumes they exist.
            document.CustomPages = [.. (document.CustomPages ?? []).Where(page => page is not null)];
            foreach (var page in document.CustomPages)
            {
                page.Name ??= string.Empty;
                page.Items = [.. (page.Items ?? []).Where(Valid)];
            }

            document.SystemPages = (document.SystemPages ?? [])
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var edits in document.SystemPages.Values)
            {
                edits.Added = [.. (edits.Added ?? []).Where(Valid)];
                edits.Hidden ??= [];
            }

            return document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _warn?.Invoke($"could not read {_path}: {ex.Message}");
            return new CatalogDocument();
        }

        static bool Valid(ItemRef? item) => item is { Target.Length: > 0 };
    }

    private void Write()
    {
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = File.Create(temporary))
                JsonSerializer.Serialize(stream, Document, CatalogJson.Default.CatalogDocument);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"could not write {_path}: {ex.Message}");
        }
    }
}
