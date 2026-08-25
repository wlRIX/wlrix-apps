namespace Wlrix.Archiver.Models;

/// <summary>One node of the entry tree: an entry plus whatever sits under it.</summary>
public sealed class ArchiveNode
{
    public ArchiveNode(ArchiveEntry entry) => Entry = entry;

    /// <summary>The entry this node stands for.</summary>
    public ArchiveEntry Entry { get; internal set; }

    /// <summary>Children, directories first and then by name, both case-insensitively.</summary>
    public List<ArchiveNode> Children { get; } = [];

    /// <summary>Every node at or below this one, this one first.</summary>
    public IEnumerable<ArchiveNode> Descend()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.Descend())
                yield return node;
        }
    }
}

/// <summary>Turns an archive's flat entry list into the tree the listing shows.</summary>
public static class ArchiveTree
{
    /// <summary>
    /// Builds the roots of the tree from <paramref name="entries"/>.
    /// </summary>
    /// <remarks>
    /// The work here is not the nesting, it is the gaps. Neither tar nor zip is obliged to store
    /// a record for a directory, and plenty of writers do not: an archive whose only entry is
    /// <c>src/main/app.c</c> is perfectly ordinary and has to display as three levels. Every
    /// missing parent is therefore synthesized (and marked, so extraction knows it was never
    /// really there).
    ///
    /// Duplicate paths are possible too — an archive may append a second copy of a file, and a
    /// real directory entry may arrive after a child already caused it to be synthesized. The
    /// last real entry wins, and a real entry always beats a synthesized one.
    /// </remarks>
    public static IReadOnlyList<ArchiveNode> Build(IEnumerable<ArchiveEntry> entries)
    {
        var roots = new List<ArchiveNode>();
        var index = new Dictionary<string, ArchiveNode>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var path = Normalize(entry.Path);
            if (path.Length == 0)
                continue;

            var node = EnsureNode(path, entry.IsDirectory, roots, index);
            // A real entry replaces the placeholder that a child may have created for it.
            if (!entry.Synthesized)
                node.Entry = entry with { Path = path };
        }

        Sort(roots);
        return roots;
    }

    /// <summary>Finds or creates the node for <paramref name="path"/>, and all its parents.</summary>
    private static ArchiveNode EnsureNode(string path, bool isDirectory, List<ArchiveNode> roots,
        Dictionary<string, ArchiveNode> index)
    {
        if (index.TryGetValue(path, out var existing))
            return existing;

        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? null : EnsureNode(path[..slash], true, roots, index);

        var node = new ArchiveNode(new ArchiveEntry(
            Path: path,
            IsDirectory: isDirectory,
            Size: null,
            CompressedSize: null,
            Modified: null,
            Mode: null,
            Owner: null,
            Group: null,
            Synthesized: true));

        index[path] = node;
        (parent?.Children ?? roots).Add(node);
        return node;
    }

    /// <summary>
    /// Strips the leading slash and any <c>./</c> prefix, collapses separators, and drops the
    /// trailing one that marks a directory.
    /// </summary>
    /// <remarks>
    /// Tar writes directories as <c>src/</c> and files as <c>src/main.c</c>; without trimming
    /// the two would key differently and every directory would appear twice. Backslashes are
    /// folded to <c>/</c> because zips written on Windows use them despite the spec.
    /// </remarks>
    internal static string Normalize(string path)
    {
        var text = path.Replace('\\', '/');
        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part != ".");
        return string.Join('/', parts);
    }

    /// <summary>Directories before files, then by name, ignoring case. Recursive.</summary>
    private static void Sort(List<ArchiveNode> nodes)
    {
        nodes.Sort(static (left, right) =>
        {
            if (left.Entry.IsDirectory != right.Entry.IsDirectory)
                return left.Entry.IsDirectory ? -1 : 1;
            return string.Compare(left.Entry.Name, right.Entry.Name,
                StringComparison.OrdinalIgnoreCase);
        });

        foreach (var node in nodes)
            Sort(node.Children);
    }
}
