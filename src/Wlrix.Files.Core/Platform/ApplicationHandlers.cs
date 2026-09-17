using Wlrix.Common.Desktop;

namespace Wlrix.Files.Core.Platform;

/// <summary>
/// Which applications can open a MIME type, in the order a menu should list them.
/// </summary>
/// <remarks>
/// Three sources, and the freedesktop association specification says how they combine:
/// <c>mimeapps.list</c>'s <c>[Default Applications]</c> names the one that runs on a
/// double-click, its <c>[Added Associations]</c> adds candidates the entries themselves did
/// not claim, and <c>mimeinfo.cache</c> — which <c>update-desktop-database</c> builds from
/// every entry's <c>MimeType=</c> — supplies the rest. <c>[Removed Associations]</c> takes
/// away.
///
/// <para>
/// Read from disk each time rather than cached. The user can change the default from another
/// application, install something new, or hand-edit the file, and an Open With menu listing
/// what was true at startup is worse than one that costs a few milliseconds.
/// </para>
/// </remarks>
public sealed class ApplicationHandlers(
    IReadOnlyList<DesktopEntry> entries,
    string? mimeAppsPath = null,
    SharedMime? subclasses = null)
{
    private readonly Dictionary<string, DesktopEntry> _byId = Index(entries);

    /// <summary>Where the user's own associations live.</summary>
    public string MimeAppsPath { get; } = mimeAppsPath ?? MimeAppsList.UserPath;

    private static Dictionary<string, DesktopEntry> Index(IReadOnlyList<DesktopEntry> entries)
    {
        var byId = new Dictionary<string, DesktopEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            // An entry earlier in XDG_DATA_DIRS wins, which is how a user's own copy in
            // ~/.local/share overrides the packaged one of the same name.
            byId.TryAdd(entry.Id, entry);
        }
        return byId;
    }

    /// <summary>The entry a double-click should run, or null if nothing claims the type.</summary>
    public DesktopEntry? DefaultFor(string mimeType) => For(mimeType).FirstOrDefault();

    /// <summary>
    /// Everything that can open the type, best first.
    /// </summary>
    /// <remarks>
    /// The order is the whole value of this class: the configured default, then anything the
    /// user explicitly added, then whatever registered itself. Duplicates are dropped keeping
    /// the earliest position, so an application that is both the default and registered does
    /// not appear twice.
    /// </remarks>
    public IReadOnlyList<DesktopEntry> For(string mimeType)
    {
        var list = Read();
        var found = new List<DesktopEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Every type this one inherits from, so a C source file offers the text editors.
        // Ordered, so the exact type's handlers come before the general ones.
        foreach (var type in Family(mimeType))
        {
            var removed = Removed(list, type);
            foreach (var id in Preferred(list, type).Concat(Registered(type)))
            {
                if (removed.Contains(id) || !seen.Add(id))
                    continue;
                if (_byId.TryGetValue(id, out var entry) && Runnable(entry))
                    found.Add(entry);
            }
        }
        return found;
    }

    /// <summary>Makes an entry the default for a type, writing the user's associations.</summary>
    public void SetDefault(string mimeType, DesktopEntry entry)
    {
        var list = MimeAppsList.Read(MimeAppsPath);
        list.SetDefault(mimeType, entry.Id);
        list.Save(MimeAppsPath);
    }

    /// <summary>Whether an entry is worth offering.</summary>
    /// <remarks>
    /// <c>NoDisplay</c> and <c>Hidden</c> both mean "do not show this to the user", and an
    /// entry with no <c>Exec</c> cannot be run at all. Offering any of the three puts a line
    /// in the menu that does nothing when clicked.
    /// </remarks>
    private static bool Runnable(DesktopEntry entry) =>
        !entry.NoDisplay && !entry.Hidden && !string.IsNullOrWhiteSpace(entry.Exec);

    /// <summary>The type and everything it inherits from, most specific first.</summary>
    private IReadOnlyList<string> Family(string mimeType) =>
        subclasses is null ? [mimeType] : subclasses.Ancestry(mimeType);

    private MimeAppsList Read() => MimeAppsList.Read(MimeAppsPath);

    private static IEnumerable<string> Preferred(MimeAppsList list, string mimeType) =>
        list.EntriesFor(MimeAppsList.DefaultApplications, mimeType)
            .Concat(list.EntriesFor(MimeAppsList.AddedAssociations, mimeType));

    private static HashSet<string> Removed(MimeAppsList list, string mimeType) =>
        [.. list.EntriesFor(MimeAppsList.RemovedAssociations, mimeType)];

    /// <summary>
    /// What <c>update-desktop-database</c> recorded, from every entry's own <c>MimeType=</c>.
    /// </summary>
    /// <remarks>
    /// Read out of the entries rather than out of <c>mimeinfo.cache</c>, and deliberately: the
    /// cache is a derived file that is stale whenever somebody has installed an entry without
    /// running the tool, which on a hand-built desktop is often. The entries are the source of
    /// truth and are already loaded.
    /// </remarks>
    private IEnumerable<string> Registered(string mimeType) =>
        _byId.Values
            .Where(entry => entry.MimeType.Contains(mimeType, StringComparer.Ordinal))
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .Select(entry => entry.Id);
}

/// <summary>What a MIME type inherits from.</summary>
/// <remarks>
/// A seam rather than a direct dependency on <c>SharedMimeDatabase</c>, so the handler list
/// can be tested with a couple of made-up types instead of against whatever version of
/// shared-mime-info the machine happens to have.
/// </remarks>
public abstract class SharedMime
{
    /// <summary>The type, then what it inherits from, most specific first.</summary>
    public abstract IReadOnlyList<string> Ancestry(string mimeType);

    /// <summary>The real database, behind the seam.</summary>
    public static SharedMime From(Mime.SharedMimeDatabase database) => new Database(database);

    private sealed class Database(Mime.SharedMimeDatabase database) : SharedMime
    {
        public override IReadOnlyList<string> Ancestry(string mimeType) => database.Ancestry(mimeType);
    }
}
