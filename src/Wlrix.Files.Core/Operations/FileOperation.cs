namespace Wlrix.Files.Core.Operations;

/// <summary>What an operation does.</summary>
public enum OperationKind
{
    Copy,
    Move,
    /// <summary>Delete permanently.</summary>
    Delete,
    /// <summary>Move to the freedesktop trash, recoverably.</summary>
    Trash,
    CreateDirectory,
    Rename,
    /// <summary>Create a symbolic link — IRIX's "Make Reference".</summary>
    Link
}

/// <summary>A unit of work: what to do, to what, and where.</summary>
/// <remarks>
/// Immutable, so the same description can be shown in a progress list, retried, and logged
/// without any of those seeing a different operation than the one that ran.
/// </remarks>
public sealed class FileOperation
{
    public required OperationKind Kind { get; init; }

    /// <summary>What is being operated on.</summary>
    public required IReadOnlyList<Location> Sources { get; init; }

    /// <summary>
    /// The destination directory for a copy or move, or the new location for a rename.
    /// Null for delete and trash.
    /// </summary>
    public Location? Target { get; init; }

    /// <summary>Identifies this operation across its whole life, including a resumed retry.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    public static FileOperation Copy(IReadOnlyList<Location> sources, Location target) =>
        new() { Kind = OperationKind.Copy, Sources = sources, Target = target };

    public static FileOperation Move(IReadOnlyList<Location> sources, Location target) =>
        new() { Kind = OperationKind.Move, Sources = sources, Target = target };

    public static FileOperation Delete(IReadOnlyList<Location> sources) =>
        new() { Kind = OperationKind.Delete, Sources = sources };

    public static FileOperation SendToTrash(IReadOnlyList<Location> sources) =>
        new() { Kind = OperationKind.Trash, Sources = sources };

    public static FileOperation Rename(Location source, Location target) =>
        new() { Kind = OperationKind.Rename, Sources = [source], Target = target };

    /// <summary>Links each source into the target directory, under its own name.</summary>
    public static FileOperation Link(IReadOnlyList<Location> sources, Location target) =>
        new() { Kind = OperationKind.Link, Sources = sources, Target = target };

    public static FileOperation NewDirectory(Location target) =>
        new() { Kind = OperationKind.CreateDirectory, Sources = [], Target = target };
}

/// <summary>Which stage an operation has reached.</summary>
public enum OperationPhase
{
    Waiting,
    /// <summary>Walking the sources to find out how much there is. Progress has no denominator yet.</summary>
    Scanning,
    Working,
    Completed,
    Failed,
    Canceled
}

/// <summary>How far along an operation is.</summary>
/// <remarks>
/// A readonly record struct, shaped after the archiver's <c>ArchiveProgress</c> for the same
/// reason: these are reported ten times a second and allocating one each time would be a
/// steady drip of garbage under an operation that is already working hard.
/// </remarks>
public readonly record struct OperationProgress(
    OperationPhase Phase,
    /// <summary>
    /// How far through, or null when there is genuinely nothing to divide by — which is the
    /// whole of the scan.
    /// </summary>
    double? Fraction = null,
    int ItemsDone = 0,
    int ItemsTotal = 0,
    long BytesDone = 0,
    long BytesTotal = 0,
    /// <summary>What is being worked on right now, for the status line.</summary>
    string? Current = null);

/// <summary>What a scan found, so progress has a denominator.</summary>
/// <param name="Items">Every file and directory to be processed, sources first.</param>
/// <param name="TotalBytes">The sum of the file sizes.</param>
public sealed record TransferPlan(IReadOnlyList<PlannedItem> Items, long TotalBytes);

/// <summary>One thing a plan will do.</summary>
/// <param name="Source">Where it is now.</param>
/// <param name="Target">Where it is going, or null for a delete.</param>
/// <param name="IsDirectory">Directories are created before their contents and deleted after.</param>
/// <param name="Size">Bytes, for progress. Zero for a directory.</param>
public sealed record PlannedItem(Location Source, Location? Target, bool IsDirectory, long Size);
