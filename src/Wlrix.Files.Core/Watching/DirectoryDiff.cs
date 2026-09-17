namespace Wlrix.Files.Core.Watching;

/// <summary>What happened to one entry in a directory.</summary>
public enum DirectoryChangeKind
{
    Added,
    Removed,
    /// <summary>Still there, but its size or timestamp moved.</summary>
    Changed
}

/// <summary>One thing that changed.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Location">Which entry.</param>
/// <param name="Entry">
/// The entry as it is now. Null for a removal, because there is nothing left to describe.
/// </param>
public sealed record DirectoryChange(DirectoryChangeKind Kind, Location Location, FileEntry? Entry = null);

/// <summary>
/// What a directory listing looked like, reduced to what is worth comparing.
/// </summary>
/// <remarks>
/// Name, size and modification time and nothing else. A hundred thousand of these is the cost
/// of watching a large directory, so it holds the three fields a change can be detected from
/// rather than the whole <see cref="FileEntry"/> with its location object.
/// </remarks>
public sealed class DirectorySnapshot
{
    private readonly Dictionary<string, Mark> _entries;

    private DirectorySnapshot(Dictionary<string, Mark> entries) => _entries = entries;

    /// <summary>Nothing seen yet. Diffing against this reports every entry as added.</summary>
    public static DirectorySnapshot Empty { get; } = new([]);

    public int Count => _entries.Count;

    /// <summary>Reduces a listing to a snapshot.</summary>
    public static DirectorySnapshot Of(IReadOnlyList<FileEntry> entries)
    {
        var marks = new Dictionary<string, Mark>(entries.Count, StringComparer.Ordinal);
        foreach (var entry in entries)
            marks[entry.Name] = new Mark(entry.Size, entry.Modified, entry.Kind);
        return new DirectorySnapshot(marks);
    }

    /// <summary>
    /// What changed between this snapshot and a new listing.
    /// </summary>
    /// <remarks>
    /// A diff rather than a translation of filesystem events, and that is the design rather
    /// than a shortcut. inotify hands over names and flags that have to be turned into "what
    /// does the listing look like now" anyway; it also drops events under load, renames arrive
    /// as two unrelated halves, and a queue overflow means starting again regardless. Making
    /// the events a <i>trigger</i> to re-read and compare gets all of that right once, and is
    /// the same code the remote poller needs.
    ///
    /// <para>
    /// The cost is re-reading a directory when one file in it changes. Coalescing bounds how
    /// often that happens, and a hundred thousand entries read in about a tenth of a second —
    /// measured in the M0.3 spike — so the trade is worth it well past the sizes that matter.
    /// </para>
    /// </remarks>
    /// <param name="directory">
    /// Where these entries live. Needed because a removed entry is by definition not in the
    /// new listing, so its location cannot be read out of one.
    /// </param>
    public IReadOnlyList<DirectoryChange> DiffTo(
        Location directory, IReadOnlyList<FileEntry> now, out DirectorySnapshot updated)
    {
        var changes = new List<DirectoryChange>();
        var seen = new HashSet<string>(now.Count, StringComparer.Ordinal);

        foreach (var entry in now)
        {
            seen.Add(entry.Name);
            var mark = new Mark(entry.Size, entry.Modified, entry.Kind);
            if (!_entries.TryGetValue(entry.Name, out var before))
                changes.Add(new DirectoryChange(DirectoryChangeKind.Added, entry.Location, entry));
            else if (before != mark)
                changes.Add(new DirectoryChange(DirectoryChangeKind.Changed, entry.Location, entry));
        }

        foreach (var (name, _) in _entries)
        {
            if (!seen.Contains(name))
                changes.Add(new DirectoryChange(DirectoryChangeKind.Removed, directory.Child(name)));
        }

        updated = Of(now);
        return changes;
    }

    private readonly record struct Mark(long Size, DateTimeOffset? Modified, FileKind Kind);
}
