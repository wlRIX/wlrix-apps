namespace Wlrix.Files.Core.Metadata;

/// <summary>How much a tree holds, so far.</summary>
/// <param name="Files">Files counted, links included.</param>
/// <param name="Directories">Directories counted, the ones asked about excluded.</param>
/// <param name="Bytes">Apparent size: the sum of the files' lengths.</param>
/// <param name="Unreadable">Directories that could not be listed, usually for want of permission.</param>
/// <param name="Complete">Whether the walk finished. False while it is still running.</param>
public readonly record struct DirectorySize(
    long Files,
    long Directories,
    long Bytes,
    int Unreadable,
    bool Complete)
{
    public long Items => Files + Directories;
}

/// <summary>
/// Adds up what a directory contains.
/// </summary>
/// <remarks>
/// A properties dialog is the only place this is needed, and it needs it because
/// <c>stat</c> does not answer it: a directory's own size is the size of the *index*, which is
/// why a folder of ten gigabytes reports 4096 bytes. The only way to the real number is to walk
/// the tree, so this reports as it goes and is cancelable at every step — the dialog opens with
/// the number still counting and stays usable, rather than waiting for a total that on a home
/// directory takes a minute.
///
/// <para>
/// Deliberately separate from the operations engine's scan, which builds a list of planned work
/// and holds every item in memory. Here nothing is retained but the running totals, so a tree
/// of any size costs the same.
/// </para>
/// </remarks>
public static class DirectoryUsage
{
    /// <summary>
    /// Walks <paramref name="roots"/>, reporting the running total and returning the final one.
    /// </summary>
    /// <param name="progress">
    /// Told the running total, throttled by the caller. Never told a
    /// <see cref="DirectorySize.Complete"/> total: that is the return value, so a caller cannot
    /// mistake the last progress report for the answer.
    /// </param>
    /// <remarks>
    /// A directory that cannot be listed is counted in
    /// <see cref="DirectorySize.Unreadable"/> and skipped, rather than aborting the walk. The
    /// total is then honestly a lower bound, and the dialog says so — which is the useful
    /// answer for a tree with one unreadable corner, where failing outright would give none.
    /// </remarks>
    public static async Task<DirectorySize> MeasureAsync(
        IFileSystemProvider provider,
        IReadOnlyList<Location> roots,
        IProgress<DirectorySize>? progress,
        CancellationToken cancellationToken)
    {
        var files = 0L;
        var directories = 0L;
        var bytes = 0L;
        var unreadable = 0;

        // Explicit rather than recursive: a deep tree would otherwise be bounded by the
        // stack, and an async recursion allocates a state machine per level.
        var pending = new Stack<Location>();

        foreach (var root in roots)
        {
            var fs = await provider.GetAsync(root, cancellationToken).ConfigureAwait(false);
            var stat = await fs.StatAsync(root, cancellationToken).ConfigureAwait(false);

            if (stat.IsDirectory && stat.SymlinkTarget is null)
            {
                pending.Push(root);
                continue;
            }

            files++;
            bytes += stat.Size;
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            IFileSystem fs;
            try
            {
                fs = await provider.GetAsync(directory, cancellationToken).ConfigureAwait(false);
            }
            catch (FileOperationException)
            {
                unreadable++;
                continue;
            }

            var listed = false;
            try
            {
                await foreach (var entry in fs.EnumerateAsync(directory, cancellationToken).ConfigureAwait(false))
                {
                    listed = true;

                    // A link is one entry and is never followed: following would count a
                    // target twice when it is inside the tree anyway, and never finish at all
                    // when two directories point at each other.
                    if (entry.IsDirectory && entry.SymlinkTarget is null)
                    {
                        directories++;
                        pending.Push(entry.Location);
                    }
                    else
                    {
                        files++;
                        bytes += entry.Size;
                    }

                    progress?.Report(new DirectorySize(files, directories, bytes, unreadable, false));
                }
            }
            catch (FileOperationException)
            {
                // Partway through a listing is still partway: what was already counted stays
                // counted, because throwing it away would report less than we know.
                if (!listed)
                    unreadable++;
            }
        }

        return new DirectorySize(files, directories, bytes, unreadable, true);
    }
}
