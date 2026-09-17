using System.Text.Json.Serialization;

namespace Wlrix.Files.Core.State;

/// <summary>Whether opening a directory makes a window or replaces the one you are in.</summary>
public enum NavigationMode
{
    /// <summary>Dolphin: the directory opens in place. The default for a new user.</summary>
    Modern,
    /// <summary>IRIX fm: the directory opens as its own window, raising one that already shows it.</summary>
    Classic
}

/// <summary>A saved location in the sidebar.</summary>
/// <param name="Uri">The canonical URI. A string, because that is what is written to disk.</param>
/// <param name="Label">What the rail shows. Empty means the location's own name.</param>
/// <param name="Icon">An icon name to override the default with, or null.</param>
public sealed record Bookmark(string Uri, string Label = "", string? Icon = null);

/// <summary>
/// A remembered remote share.
/// </summary>
/// <remarks>
/// <b>No password field, and there must never be one.</b> The saved-share list is not a
/// secret and lives in plain JSON precisely so that bookmarks keep working when the keyring
/// is locked; credentials go to the Secret Service or nowhere. An obfuscated password on
/// disk would be worse than an honest transient store, because it would look safe.
/// </remarks>
public sealed record SavedShare
{
    public required string Scheme { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; } = -1;
    public string Share { get; init; } = "";
    public string Username { get; init; } = "";
    public bool Anonymous { get; init; }
    public string Label { get; init; } = "";
}

/// <summary>One tab, as it is written to the session file.</summary>
/// <param name="Uri">Where the tab was looking.</param>
/// <summary>One tab in a restored window.</summary>
/// <param name="Uri">Where the first pane was.</param>
/// <param name="SecondUri">Where the second pane was, or null if the tab was not split.</param>
/// <param name="ActivePane">Which of them the window was acting on.</param>
/// <remarks>
/// The two extra members have defaults so a session written before splits existed still reads:
/// a missing property leaves them null and zero, which is exactly an unsplit tab.
/// </remarks>
public sealed record TabSession(string Uri, string? SecondUri = null, int ActivePane = 0);

/// <summary>
/// One window, as it is written to the session file.
/// </summary>
/// <remarks>
/// Size and maximized state only. There is no position here because Wayland does not give a
/// client one — <c>WindowImplBase.Position</c> is <c>default</c> and <c>PointToScreen</c> is
/// the identity — so the compositor places windows and a saved position would be a number we
/// could write and never use.
/// </remarks>
public sealed record WindowSession
{
    public double Width { get; init; }
    public double Height { get; init; }
    public bool Maximized { get; init; }
    public double SidebarWidth { get; init; }
    public int ActiveTab { get; init; }
    public IReadOnlyList<TabSession> Tabs { get; init; } = [];
}

/// <summary>One directory's remembered view, with the counter that decides what gets evicted.</summary>
/// <param name="Uri">The directory.</param>
/// <param name="Used">
/// A monotonic counter, not a timestamp: eviction has to be deterministic and a clock that
/// steps backwards would make the wrong entry the oldest.
/// </param>
public sealed record ViewStateEntry(string Uri, long Used, DirectoryViewState State);

// --- the five documents ------------------------------------------------------
//
// Each carries its own Version from the first release. A file whose version is missing or
// newer than this build understands degrades to the defaults rather than throwing, so a
// downgrade loses settings instead of refusing to start.

public sealed record BookmarksDocument
{
    public int Version { get; init; } = FilesStateStore.CurrentVersion;
    public IReadOnlyList<Bookmark> Bookmarks { get; init; } = [];
}

public sealed record SharesDocument
{
    public int Version { get; init; } = FilesStateStore.CurrentVersion;
    public IReadOnlyList<SavedShare> Shares { get; init; } = [];
}

public sealed record ViewStateDocument
{
    public int Version { get; init; } = FilesStateStore.CurrentVersion;
    public long NextUse { get; init; } = 1;
    public IReadOnlyList<ViewStateEntry> Directories { get; init; } = [];
}

public sealed record SessionDocument
{
    public int Version { get; init; } = FilesStateStore.CurrentVersion;
    public IReadOnlyList<WindowSession> Windows { get; init; } = [];
}

/// <summary>
/// What the Options menu sets and this application alone cares about.
/// </summary>
/// <remarks>
/// <c>NavigationMode</c> and <c>IconTheme</c> used to live here and now live in
/// <c>files.toml</c>, written by <c>wlrix-settings-daemon</c> — they are the two somebody would
/// look for in a settings panel, and a setting with two homes has two answers. Everything left
/// is per application and per window: nothing outside this program has an opinion about
/// whether previews are on.
///
/// <para>
/// A <c>preferences.json</c> written before that move still carries the two keys. They are
/// ignored rather than migrated: the file is read into this record, and a JSON property with
/// nothing to bind to is dropped. Migrating would mean writing a config file from here, which
/// is the one thing the daemon exists to stop.
/// </para>
/// </remarks>
public sealed record FilesPreferences
{
    public int Version { get; init; } = FilesStateStore.CurrentVersion;

    public bool ShowHidden { get; init; }
    public bool ThumbnailsEnabled { get; init; } = true;

    /// <summary>The largest file worth thumbnailing, in bytes.</summary>
    public long ThumbnailMaxBytes { get; init; } = 64 * 1024 * 1024;

    public bool ConfirmDelete { get; init; } = true;
    public bool SingleClickOpen { get; init; }

    /// <summary>How often a remote directory is re-stat-ed, in seconds.</summary>
    public int PollIntervalSeconds { get; init; } = 10;
}

/// <summary>The source-generated serializer for everything above.</summary>
/// <remarks>
/// Source-generated rather than reflection-based so the app keeps working under trimming,
/// which is where a settings file silently reading back empty would be hardest to diagnose.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(BookmarksDocument))]
[JsonSerializable(typeof(SharesDocument))]
[JsonSerializable(typeof(ViewStateDocument))]
[JsonSerializable(typeof(SessionDocument))]
[JsonSerializable(typeof(FilesPreferences))]
internal sealed partial class FilesJson : JsonSerializerContext;
