using System.Globalization;
using Wlrix.Common;
using Wlrix.Files.Core.Operations;

namespace Wlrix.Files.Core.Dnd;

/// <summary>
/// Materializes remote files on disk so they can be dragged into another application.
/// </summary>
/// <remarks>
/// A drag hands the other side a list of files, and a file on an SMB share is not one: it has
/// no path, and the application receiving the drop has no way to fetch it. Offering the
/// <c>smb://</c> URI instead would be purer and useless — nothing but another wlRIX window
/// would know what to do with it. So the selection is copied to a scratch directory first and
/// those paths are what get offered, which is what every file manager does when dragging out
/// of a share.
///
/// <para>
/// Local sources are passed straight through. Copying a local file to hand a local path back
/// would double every drag on the machine's own disk for no benefit at all.
/// </para>
///
/// <para>
/// Scratch lives under the app's data directory rather than <c>/tmp</c>, which is a tmpfs on
/// this system: staging a large directory there would fill RAM to complete a drag.
/// </para>
/// </remarks>
public sealed class DragStaging : IDisposable
{
    /// <summary>The most that will be staged for one drag, in bytes.</summary>
    /// <remarks>
    /// A guard rather than a preference. Without it, dragging a share's root out of the
    /// window would begin an unbounded download with the only feedback being a cursor, and
    /// the user's first sign of trouble would be a full disk.
    /// </remarks>
    public const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;

    private readonly IFileSystemProvider _provider;
    private readonly string _root;
    private int _sequence;

    public DragStaging(IFileSystemProvider provider, string? root = null)
    {
        _provider = provider;
        _root = root ?? Path.Combine(ApplicationPaths.AppData, "files", "drag",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The most this instance will stage for one drag.</summary>
    public long MaxBytes { get; init; } = DefaultMaxBytes;

    /// <summary>Whether any of these would have to be copied before they can be dragged.</summary>
    /// <remarks>
    /// Worth asking before starting, because staging is slow enough to want a progress
    /// indicator and passing local files through is instant.
    /// </remarks>
    public static bool NeedsStaging(IReadOnlyList<Location> sources)
    {
        foreach (var source in sources)
        {
            if (!source.TryGetLocalPath(out _))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns a local path for each source, copying the ones that do not have one.
    /// </summary>
    /// <remarks>
    /// Order is preserved and every source is answered, so the caller can hand the result
    /// straight to the drag without pairing it back up.
    /// </remarks>
    /// <exception cref="FileOperationException">
    /// With <see cref="FileErrorKind.NoSpace"/> if the selection is larger than
    /// <see cref="MaxBytes"/>, before anything has been copied.
    /// </exception>
    public async Task<IReadOnlyList<string>> StageAsync(
        IReadOnlyList<Location> sources,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var remote = sources.Where(source => !source.TryGetLocalPath(out _)).ToList();
        if (remote.Count == 0)
            return [.. sources.Select(LocalPathOf)];

        // A fresh subdirectory per drag. Two drags of files with the same name from different
        // shares must not overwrite each other, and the second must certainly not hand over
        // the first one's contents.
        var directory = Path.Combine(_root, (++_sequence).ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var into = Location.FromLocalPath(directory);

        var operation = FileOperation.Copy(remote, into);
        var job = new OperationJob(_provider, FixedConflictResolver.OverwriteAll);

        // Scanned before running, and yes that walks the remote tree twice. The guard is only
        // worth anything if it fires before the bytes move, and a scan is metadata where the
        // copy it is protecting against is gigabytes.
        var plan = await job.ScanAsync(operation, null, cancellationToken).ConfigureAwait(false);
        if (plan.TotalBytes > MaxBytes)
        {
            throw new FileOperationException(FileErrorKind.NoSpace, remote[0],
                $"a drag of {plan.TotalBytes} bytes is over the {MaxBytes} byte staging limit");
        }

        var result = await job.RunAsync(operation, progress, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw result.Errors.Count > 0
                ? result.Errors[0]
                : new FileOperationException(FileErrorKind.Unknown, remote[0], "staging did not complete");
        }

        return [.. sources.Select(source => source.TryGetLocalPath(out var path)
            ? path
            : Path.Combine(directory, source.Name))];
    }

    private static string LocalPathOf(Location source) =>
        source.TryGetLocalPath(out var path) ? path : throw new FileOperationException(
            FileErrorKind.Unsupported, source, "not a local path");

    /// <summary>Removes everything this instance staged.</summary>
    /// <remarks>
    /// Best effort, and on the way out rather than after each drag: the receiving application
    /// may still be reading the files it was given, and a drop that copies a large directory
    /// is not finished when the drag ends.
    /// </remarks>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Scratch outliving the process is untidy, not broken, and the next run uses a
            // different pid. Not worth failing a shutdown over.
        }
    }
}
