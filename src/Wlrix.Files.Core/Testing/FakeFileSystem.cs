using System.Runtime.CompilerServices;

namespace Wlrix.Files.Core.Testing;

/// <summary>
/// An in-memory <see cref="IFileSystem"/> with injectable faults, for testing anything
/// built on the interface without touching a disk or a network.
/// </summary>
/// <remarks>
/// This exists because the interesting paths through the operations engine — a
/// conflict, a retry ladder, a cancel halfway through a copy, a move across mounts, a
/// part file left behind — are all but impossible to provoke reliably against a real
/// filesystem, and impossible at all against a remote one in CI. Here they are one
/// method call and they happen instantly.
///
/// <para>
/// It ships in Core rather than in the test project on purpose: the remote backends
/// will want it too, and so will anything downstream that consumes Core.
/// </para>
///
/// <para>
/// Not thread-safe by design. Tests are single-threaded, and a lock here would hide
/// exactly the interleaving bugs a caller might have.
/// </para>
/// </remarks>
public sealed class FakeFileSystem : IFileSystem
{
    private sealed class Node
    {
        public required string Name { get; init; }
        public required bool IsDirectory { get; init; }
        public byte[] Content { get; set; } = [];
        /// <summary>Where a symlink points, or null for anything else.</summary>
        public string? LinkTarget { get; set; }
        public DateTimeOffset Modified { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public int? Mode { get; set; }
        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
    }

    private readonly Node _root = new() { Name = "", IsDirectory = true };
    private readonly Dictionary<string, Queue<FileErrorKind>> _failOnce = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileErrorKind> _failAlways = new(StringComparer.Ordinal);

    public FakeFileSystem(string mountKey = "file://")
    {
        MountKey = mountKey;
        Capabilities = FileSystemCapabilities.Rename
            | FileSystemCapabilities.SymLink
            | FileSystemCapabilities.PosixMode
            | FileSystemCapabilities.RandomWriteAccess
            | FileSystemCapabilities.PreservesMTime
            | FileSystemCapabilities.CaseSensitive;
    }

    /// <inheritdoc/>
    public string MountKey { get; }

    /// <inheritdoc/>
    /// <remarks>Settable, so one test can watch the engine take a capability's fallback path.</remarks>
    public FileSystemCapabilities Capabilities { get; set; }

    /// <summary>An artificial delay on every operation, for exercising cancellation.</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;

    /// <summary>How many operations have been performed. Cheap assertion of "did it retry".</summary>
    public int OperationCount { get; private set; }

    // --- building a tree -------------------------------------------------

    /// <summary>Creates a directory and any missing parents.</summary>
    public FakeFileSystem AddDirectory(string path)
    {
        Walk(path, createMissing: true, out _);
        return this;
    }

    /// <summary>Creates a file, and any missing parent directories.</summary>
    public FakeFileSystem AddFile(string path, string content = "", DateTimeOffset? modified = null)
        => AddFile(path, System.Text.Encoding.UTF8.GetBytes(content), modified);

    /// <summary>Creates a file, and any missing parent directories.</summary>
    public FakeFileSystem AddFile(string path, byte[] content, DateTimeOffset? modified = null)
    {
        var normalized = Location.NormalizePath(path);
        var slash = normalized.LastIndexOf('/');
        var parent = slash == 0 ? "/" : normalized[..slash];
        var name = normalized[(slash + 1)..];
        var dir = Walk(parent, createMissing: true, out _)
                  ?? throw new InvalidOperationException($"could not create {parent}");
        var node = new Node { Name = name, IsDirectory = false, Content = content };
        if (modified is not null)
            node.Modified = modified.Value;
        dir.Children[name] = node;
        return this;
    }

    /// <summary>Adds <paramref name="count"/> generated files under a directory, for volume tests.</summary>
    public FakeFileSystem AddFiles(string directory, int count, string prefix = "file")
    {
        var dir = Walk(directory, createMissing: true, out _)!;
        for (var i = 0; i < count; i++)
        {
            var name = $"{prefix}-{i:D6}";
            dir.Children[name] = new Node { Name = name, IsDirectory = false, Content = [] };
        }
        return this;
    }

    /// <summary>Whether a path exists in the tree. For asserting the result of an operation.</summary>
    public bool Contains(string path) => Walk(path, createMissing: false, out _) is not null;

    /// <summary>The content of a file, or null if it is not there.</summary>
    public byte[]? ContentOf(string path)
    {
        var node = Walk(path, createMissing: false, out _);
        return node is { IsDirectory: false } ? node.Content : null;
    }

    /// <summary>Every path in the tree, sorted. The readable form for a whole-tree assertion.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        var paths = new List<string>();
        Collect(_root, "", paths);
        paths.Sort(StringComparer.Ordinal);
        return paths;

        static void Collect(Node node, string prefix, List<string> into)
        {
            foreach (var child in node.Children.Values)
            {
                var path = prefix + "/" + child.Name;
                // Marked, because the whole point of a link test is that the engine made a
                // reference rather than a second copy, and both would otherwise read alike.
                into.Add(child switch
                {
                    { IsDirectory: true } => path + "/",
                    { LinkTarget: { } link } => path + " -> " + link,
                    _ => path
                });
                if (child.IsDirectory)
                    Collect(child, path, into);
            }
        }
    }

    private static FileKind KindOf(Node node) => node switch
    {
        { IsDirectory: true } => FileKind.Directory,
        { LinkTarget: not null } => FileKind.Symlink,
        _ => FileKind.File
    };

    // --- injecting faults ------------------------------------------------

    /// <summary>Fails the next operation on a path, then behaves normally.</summary>
    /// <remarks>Call repeatedly to queue several: that is how a retry ladder is tested.</remarks>
    public FakeFileSystem FailOnce(string path, FileErrorKind kind)
    {
        var key = Location.NormalizePath(path);
        if (!_failOnce.TryGetValue(key, out var queue))
            _failOnce[key] = queue = new Queue<FileErrorKind>();
        queue.Enqueue(kind);
        return this;
    }

    /// <summary>Fails every operation on a path, until <see cref="ClearFaults"/>.</summary>
    public FakeFileSystem FailAlways(string path, FileErrorKind kind)
    {
        _failAlways[Location.NormalizePath(path)] = kind;
        return this;
    }

    /// <summary>Removes every injected fault.</summary>
    public FakeFileSystem ClearFaults()
    {
        _failOnce.Clear();
        _failAlways.Clear();
        return this;
    }

    // --- IFileSystem -----------------------------------------------------

    /// <inheritdoc/>
    public async Task<FileStat> StatAsync(Location location, CancellationToken cancellationToken)
    {
        await BeginAsync(location, cancellationToken).ConfigureAwait(false);
        var node = Require(location);
        return new FileStat
        {
            Location = location,
            Kind = KindOf(node),
            Size = node.IsDirectory ? 0 : node.Content.Length,
            Modified = node.Modified,
            UnixMode = node.Mode,
            SymlinkTarget = node.LinkTarget
        };
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(Location location, CancellationToken cancellationToken)
    {
        await BeginAsync(location, cancellationToken).ConfigureAwait(false);
        return Walk(location.Path, createMissing: false, out _) is not null;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileEntry> EnumerateAsync(
        Location location, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await BeginAsync(location, cancellationToken).ConfigureAwait(false);
        var node = Require(location);
        if (!node.IsDirectory)
            throw new FileOperationException(FileErrorKind.NotADirectory, location);

        foreach (var child in node.Children.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new FileEntry
            {
                Location = location.Child(child.Name),
                Name = child.Name,
                Kind = KindOf(child),
                Size = child.IsDirectory ? 0 : child.Content.Length,
                Modified = child.Modified,
                UnixMode = child.Mode,
                SymlinkTarget = child.LinkTarget,
                IsHidden = child.Name.StartsWith('.')
            };
        }
    }

    /// <inheritdoc/>
    public async Task<Stream> OpenReadAsync(Location location, CancellationToken cancellationToken)
    {
        await BeginAsync(location, cancellationToken).ConfigureAwait(false);
        var node = Require(location);
        if (node.IsDirectory)
            throw new FileOperationException(FileErrorKind.IsADirectory, location);
        return new MemoryStream(node.Content, writable: false);
    }

    /// <inheritdoc/>
    public async Task<Stream> OpenWriteAsync(Location location, WriteMode mode, long? length, CancellationToken cancellationToken)
    {
        await BeginAsync(location, cancellationToken).ConfigureAwait(false);
        var parent = Walk(location.Parent?.Path ?? "/", createMissing: false, out _)
                     ?? throw new FileOperationException(FileErrorKind.NotFound, location.Parent ?? location);

        var existing = parent.Children.GetValueOrDefault(location.Name);
        if (existing is not null && mode == WriteMode.CreateNew)
            throw new FileOperationException(FileErrorKind.AlreadyExists, location);
        if (existing is { IsDirectory: true })
            throw new FileOperationException(FileErrorKind.IsADirectory, location);

        var node = existing ?? new Node { Name = location.Name, IsDirectory = false };
        parent.Children[location.Name] = node;
        if (mode != WriteMode.Append)
            node.Content = [];
        return new NodeStream(node);
    }

    /// <inheritdoc/>
    public async Task CreateDirectoryAsync(Location location, CancellationToken cancellationToken)
    {
        await BeginAsync(location, cancellationToken).ConfigureAwait(false);
        if (Walk(location.Path, createMissing: false, out _) is not null)
            throw new FileOperationException(FileErrorKind.AlreadyExists, location);
        Walk(location.Path, createMissing: true, out _);
    }

    /// <inheritdoc/>
    public async Task CreateSymlinkAsync(Location link, string target, CancellationToken cancellationToken)
    {
        await BeginAsync(link, cancellationToken).ConfigureAwait(false);
        if (!Capabilities.HasFlag(FileSystemCapabilities.SymLink))
            throw new FileOperationException(FileErrorKind.Unsupported, link);
        if (Walk(link.Path, createMissing: false, out _) is not null)
            throw new FileOperationException(FileErrorKind.AlreadyExists, link);

        // Not followed and not validated: a dangling link is a real thing a filesystem holds,
        // and a fake that refused to make one could not test what the engine does with it.
        var parent = Walk(link.Parent?.Path ?? "/", createMissing: true, out _)!;
        parent.Children[link.Name] = new Node { Name = link.Name, IsDirectory = false, LinkTarget = target };
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(Location location, bool recursive, CancellationToken cancellationToken)
    {
        await BeginAsync(location, cancellationToken).ConfigureAwait(false);
        var node = Require(location);
        if (node.IsDirectory && node.Children.Count > 0 && !recursive)
            throw new FileOperationException(FileErrorKind.NotEmpty, location);
        var parent = Walk(location.Parent?.Path ?? "/", createMissing: false, out _)!;
        parent.Children.Remove(location.Name);
    }

    /// <inheritdoc/>
    public async Task RenameAsync(Location from, Location to, CancellationToken cancellationToken)
    {
        await BeginAsync(from, cancellationToken).ConfigureAwait(false);
        if (!Capabilities.HasFlag(FileSystemCapabilities.Rename))
            throw new FileOperationException(FileErrorKind.Unsupported, from);
        if (!string.Equals(from.MountKey, to.MountKey, StringComparison.Ordinal))
            throw new FileOperationException(FileErrorKind.CrossDevice, to);

        var node = Require(from);
        if (Walk(to.Path, createMissing: false, out _) is not null)
            throw new FileOperationException(FileErrorKind.AlreadyExists, to);

        var target = Walk(to.Parent?.Path ?? "/", createMissing: false, out _)
                     ?? throw new FileOperationException(FileErrorKind.NotFound, to.Parent ?? to);
        var source = Walk(from.Parent?.Path ?? "/", createMissing: false, out _)!;
        source.Children.Remove(from.Name);

        var moved = new Node { Name = to.Name, IsDirectory = node.IsDirectory, Content = node.Content, Modified = node.Modified, Mode = node.Mode };
        foreach (var (k, v) in node.Children)
            moved.Children[k] = v;
        target.Children[to.Name] = moved;
    }

    /// <inheritdoc/>
    public async Task SetModifiedAsync(Location location, DateTimeOffset modified, CancellationToken cancellationToken)
    {
        await BeginAsync(location, cancellationToken).ConfigureAwait(false);
        Require(location).Modified = modified;
    }

    /// <inheritdoc/>
    public async Task SetUnixModeAsync(Location location, int mode, CancellationToken cancellationToken)
    {
        await BeginAsync(location, cancellationToken).ConfigureAwait(false);
        Require(location).Mode = mode;
    }

    /// <inheritdoc/>
    public Task<FreeSpace?> GetFreeSpaceAsync(Location location, CancellationToken cancellationToken) =>
        Task.FromResult(Capabilities.HasFlag(FileSystemCapabilities.ReportsFreeSpace)
            ? new FreeSpace(1L << 40, 1L << 39)
            : (FreeSpace?)null);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // --- internals -------------------------------------------------------

    /// <summary>
    /// The gate every operation passes: counts it, honors cancellation and latency,
    /// then raises whatever fault is queued for the path.
    /// </summary>
    private async Task BeginAsync(Location location, CancellationToken cancellationToken)
    {
        OperationCount++;
        cancellationToken.ThrowIfCancellationRequested();
        if (Latency > TimeSpan.Zero)
            await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);

        var path = location.Path;
        if (_failAlways.TryGetValue(path, out var always))
            throw new FileOperationException(always, location);
        if (_failOnce.TryGetValue(path, out var queue) && queue.Count > 0)
            throw new FileOperationException(queue.Dequeue(), location);
    }

    private Node Require(Location location) =>
        Walk(location.Path, createMissing: false, out _)
        ?? throw new FileOperationException(FileErrorKind.NotFound, location);

    private Node? Walk(string path, bool createMissing, out Node? parent)
    {
        parent = null;
        var node = _root;
        var normalized = Location.NormalizePath(path);
        if (normalized == "/")
            return _root;

        foreach (var part in normalized[1..].Split('/'))
        {
            parent = node;
            if (node.Children.TryGetValue(part, out var child))
            {
                if (!child.IsDirectory && !ReferenceEquals(child, node))
                {
                    // A file in the middle of a path: only valid as the final component.
                    node = child;
                    continue;
                }
                node = child;
                continue;
            }

            if (!createMissing)
                return null;
            var created = new Node { Name = part, IsDirectory = true };
            node.Children[part] = created;
            node = created;
        }

        return node;
    }

    /// <summary>A stream that writes straight back into the node's content.</summary>
    private sealed class NodeStream(Node node) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                node.Content = ToArray();
                node.Modified = DateTimeOffset.UtcNow;
            }
            base.Dispose(disposing);
        }
    }
}
