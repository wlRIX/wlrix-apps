using Wlrix.Common.Progress;
using Wlrix.Files.Core.Platform;

namespace Wlrix.Files.Core.Operations;

/// <summary>How an operation ended.</summary>
/// <param name="Phase">Completed, failed or canceled.</param>
/// <param name="ItemsDone">How many items were processed.</param>
/// <param name="ItemsSkipped">How many were skipped, by a conflict rule or a retry decision.</param>
/// <param name="Errors">Every failure that was skipped rather than retried.</param>
public sealed record OperationResult(
    OperationPhase Phase,
    int ItemsDone,
    int ItemsSkipped,
    IReadOnlyList<FileOperationException> Errors)
{
    public bool Succeeded => Phase == OperationPhase.Completed && Errors.Count == 0;
}

/// <summary>
/// Runs one <see cref="FileOperation"/>: scan, plan, execute.
/// </summary>
/// <remarks>
/// The three phases are separate because progress needs a denominator. Scanning a large tree
/// can take a while on its own, so it reports a running count rather than leaving a dialog
/// blank, and only once it finishes does the fraction become meaningful.
///
/// <para>
/// Every file is written to a <c>.wlrix-part</c> beside its target and moved into place when
/// complete — the archiver's <c>.wlrix-new</c> idiom generalized. A copy interrupted by a
/// crash or a pulled cable then leaves an obvious partial file rather than a plausible-looking
/// truncated one at the real name.
/// </para>
/// </remarks>
public sealed class OperationJob
{
    /// <summary>The suffix an in-progress transfer is written under.</summary>
    public const string PartSuffix = ".wlrix-part";

    private const int BufferSize = 128 * 1024;

    private readonly IFileSystemProvider _provider;
    private readonly IConflictResolver _conflicts;
    private readonly IRetryPolicy _retries;
    private readonly MountTable? _mounts;

    private ConflictDecision? _conflictForAll;
    private bool _skipAllErrors;

    /// <summary>Told how many bytes of the current item have been read, while one is copying.</summary>
    /// <remarks>
    /// A field rather than a parameter threaded through every step between the loop and the
    /// read, because only the loop knows what the count is out of and only the read knows the
    /// count. A job runs one operation, one item at a time, so there is only ever one.
    /// </remarks>
    private Action<long>? _itemBytes;

    /// <summary>Held while the user has paused the operation; null when nothing can pause it.</summary>
    private PauseGate? _pause;

    /// <summary>The operation's token, for the waits inside a read that has no token of its own.</summary>
    private CancellationToken _token;

    public OperationJob(
        IFileSystemProvider provider,
        IConflictResolver? conflicts = null,
        IRetryPolicy? retries = null,
        MountTable? mounts = null)
    {
        _provider = provider;
        _conflicts = conflicts ?? FixedConflictResolver.SkipAll;
        _retries = retries ?? new DefaultRetryPolicy();
        _mounts = mounts;
    }

    /// <summary>Runs the operation to completion, cancellation or failure.</summary>
    /// <param name="pause">The gate the user's Pause button closes, or null for no pausing.</param>
    public async Task<OperationResult> RunAsync(
        FileOperation operation,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        PauseGate? pause = null)
    {
        _pause = pause;
        _token = cancellationToken;
        var throttle = new ProgressThrottle<OperationProgress>(progress);
        var errors = new List<FileOperationException>();
        var done = 0;
        var skipped = 0;

        try
        {
            // The operations with nothing to walk answer immediately rather than going
            // through a scan that would find one item.
            switch (operation.Kind)
            {
                case OperationKind.CreateDirectory:
                    await CreateDirectoryAsync(operation, cancellationToken).ConfigureAwait(false);
                    return Finish(OperationPhase.Completed, 1, 0, errors, throttle);
                case OperationKind.Rename:
                    await RenameAsync(operation, cancellationToken).ConfigureAwait(false);
                    return Finish(OperationPhase.Completed, 1, 0, errors, throttle);
            }

            var plan = await ScanAsync(operation, throttle, cancellationToken).ConfigureAwait(false);

            // One archive is one item as far as success goes: a member that cannot be read
            // leaves a hole nobody would notice until extracting it, so the whole thing fails
            // rather than skipping it.
            if (operation.Kind == OperationKind.Compress)
            {
                await CompressAsync(operation, plan, throttle, cancellationToken).ConfigureAwait(false);
                return Finish(OperationPhase.Completed, plan.Items.Count, 0, errors, throttle);
            }
            var bytesDone = 0L;
            var files = new FileCount(plan);

            foreach (var item in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_pause is { } gate)
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

                // Named as it starts rather than once it is done, so the status line says what
                // is being copied now and not what finished a moment ago.
                var before = bytesDone;
                var finished = done + skipped;
                var filesBefore = files.Done;
                throttle.Report(Working(plan, finished, files, filesBefore, before, item));
                _itemBytes = read => throttle.Report(Working(plan, finished, files, filesBefore, before + Math.Min(read, item.Size), item));
                ItemOutcome outcome;
                try
                {
                    outcome = await ExecuteWithRetryAsync(operation, item, errors, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _itemBytes = null;
                }

                switch (outcome)
                {
                    case ItemOutcome.Done:
                        done++;
                        bytesDone += item.Size;
                        break;
                    case ItemOutcome.Skipped:
                        skipped++;
                        bytesDone += item.Size;
                        break;
                    case ItemOutcome.Aborted:
                        return Finish(OperationPhase.Failed, done, skipped, errors, throttle);
                }
                files.Passed(item);

                throttle.Report(Working(plan, done + skipped, files, files.Done, bytesDone, item));
            }

            // A move deletes its sources only after everything has been copied, and only if
            // everything was. A partial move that removed the originals anyway would destroy
            // exactly the files it failed to copy.
            if (operation.Kind == OperationKind.Move && skipped == 0 && errors.Count == 0)
                await DeleteMovedSourcesAsync(operation, errors, cancellationToken).ConfigureAwait(false);

            return Finish(OperationPhase.Completed, done, skipped, errors, throttle);
        }
        catch (OperationCanceledException)
        {
            return Finish(OperationPhase.Canceled, done, skipped, errors, throttle);
        }
        catch (FileOperationException ex)
        {
            errors.Add(ex);
            return Finish(OperationPhase.Failed, done, skipped, errors, throttle);
        }
    }

    /// <summary>Progress partway through a plan.</summary>
    /// <remarks>
    /// By bytes wherever there are any, because that is what takes the time: a thousand empty
    /// files are over in a moment and one disk image is not. A plan of nothing but directories
    /// and empty files has no bytes to divide by and counts items instead.
    /// </remarks>
    private static OperationProgress Working(TransferPlan plan, int finished, FileCount files, int filesDone, long bytes, PlannedItem current) =>
        new(OperationPhase.Working,
            plan.TotalBytes > 0 ? (double)bytes / plan.TotalBytes : (double)finished / Math.Max(1, plan.Items.Count),
            finished, plan.Items.Count, bytes, plan.TotalBytes, current.Source.Name,
            filesDone, files.Total, current.Source);

    /// <summary>The files in a plan, and how many of them are behind us.</summary>
    /// <remarks>
    /// The total is counted once, because the progress line asks ten times a second and a plan
    /// can be a hundred thousand items long.
    /// </remarks>
    private sealed class FileCount(TransferPlan plan)
    {
        public int Total { get; } = plan.Items.Count(item => !item.IsDirectory);

        public int Done { get; private set; }

        public void Passed(PlannedItem item)
        {
            if (!item.IsDirectory)
                Done++;
        }
    }

    private static OperationResult Finish(
        OperationPhase phase, int done, int skipped,
        List<FileOperationException> errors, ProgressThrottle<OperationProgress> throttle)
    {
        // ReportNow, not Report: the last report must not be the one the throttle swallows,
        // or a progress bar is left sitting at 98%.
        throttle.ReportNow(new OperationProgress(phase, 1.0, done + skipped, done + skipped));
        return new OperationResult(phase, done, skipped, errors);
    }

    // --- scan ------------------------------------------------------------

    /// <summary>Walks the sources, depth first, producing the full list of work.</summary>
    /// <remarks>
    /// Directories come before their contents, which is what lets execute create each one
    /// before writing into it and lets a move delete them afterwards in reverse.
    /// </remarks>
    internal async Task<TransferPlan> ScanAsync(
        FileOperation operation,
        ProgressThrottle<OperationProgress>? throttle,
        CancellationToken cancellationToken)
    {
        var items = new List<PlannedItem>();
        var totalBytes = 0L;

        foreach (var source in operation.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A compress's target is the archive file, which has no children to plan into.
            var target = operation.Kind == OperationKind.Compress ? null : operation.Target?.Child(source.Name);

            // A link to a directory is one link, so the tree below it is not walked -- and
            // must not be, or linking a home directory would plan a million items and then
            // fill the target with a mirror of the source's shape. It is also the one kind
            // that needs no stat: a link may point at something that is not there.
            if (operation.Kind == OperationKind.Link)
            {
                items.Add(new PlannedItem(source, target, false, 0));
                Tick();
                continue;
            }

            var fs = await _provider.GetAsync(source, cancellationToken).ConfigureAwait(false);
            var stat = await fs.StatAsync(source, cancellationToken).ConfigureAwait(false);

            if (stat.SymlinkTarget is { } linkTarget)
            {
                items.Add(LinkItem(source, target, stat.IsDirectory, stat.Size, linkTarget, stat.Modified, stat.UnixMode));
                Tick();
                continue;
            }

            if (!stat.IsDirectory)
            {
                items.Add(new PlannedItem(source, target, false, stat.Size, Modified: stat.Modified, UnixMode: stat.UnixMode));
                totalBytes += stat.Size;
                Tick();
                continue;
            }

            items.Add(new PlannedItem(source, target, true, 0, Modified: stat.Modified, UnixMode: stat.UnixMode));
            Tick();
            totalBytes += await ScanDirectoryAsync(fs, source, target, items, cancellationToken).ConfigureAwait(false);
        }

        return new TransferPlan(items, totalBytes);

        void Tick() => throttle?.Report(new OperationProgress(
            OperationPhase.Scanning, null, items.Count, 0, totalBytes, 0,
            items.Count > 0 ? items[^1].Source.Name : null));
    }

    /// <remarks>
    /// A link is never walked into, whatever it points at. The local filesystem lists a link to
    /// a directory as a directory, and following one that points at an ancestor plans forever,
    /// growing the item list until memory runs out, before a single byte has been written. A
    /// link that is not walked is also the right copy: it is reproduced as a link, as
    /// <c>cp -R</c> does, rather than as a second copy of whatever it pointed at.
    /// </remarks>
    private async Task<long> ScanDirectoryAsync(
        IFileSystem fs, Location directory, Location? target,
        List<PlannedItem> into, CancellationToken cancellationToken)
    {
        var bytes = 0L;
        await foreach (var entry in fs.EnumerateAsync(directory, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childTarget = target?.Child(entry.Name);

            if (entry.IsSymlink)
            {
                // The listing does not carry the link's text; a stat of the link does.
                var linkTarget = entry.SymlinkTarget;
                if (linkTarget is null)
                {
                    try
                    {
                        linkTarget = (await fs.StatAsync(entry.Location, cancellationToken).ConfigureAwait(false)).SymlinkTarget;
                    }
                    catch (FileOperationException)
                    {
                        // The link is still one item. Copying it will ask again, and report
                        // the error then, where it can be retried or skipped.
                    }
                }

                var item = LinkItem(entry.Location, childTarget, entry.IsDirectory, entry.Size, linkTarget,
                    entry.Modified, entry.UnixMode);
                into.Add(item);
                bytes += item.Size;
                continue;
            }

            if (entry.IsDirectory)
            {
                into.Add(new PlannedItem(entry.Location, childTarget, true, 0,
                    Modified: entry.Modified, UnixMode: entry.UnixMode));
                bytes += await ScanDirectoryAsync(fs, entry.Location, childTarget, into, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                into.Add(new PlannedItem(entry.Location, childTarget, false, entry.Size,
                    Modified: entry.Modified, UnixMode: entry.UnixMode));
                bytes += entry.Size;
            }
        }
        return bytes;
    }

    /// <summary>Plans a symbolic link as the one item it is.</summary>
    /// <remarks>
    /// Sized as the file it points at, because that is what a destination that cannot hold
    /// links receives instead. A link to a directory is sized zero, like a directory.
    /// </remarks>
    private static PlannedItem LinkItem(
        Location source, Location? target, bool toDirectory, long size, string? linkTarget,
        DateTimeOffset? modified = null, int? unixMode = null) =>
        new(source, target, false, toDirectory ? 0 : size, IsLink: true, LinkTarget: linkTarget,
            Modified: modified, UnixMode: unixMode);

    // --- execute ---------------------------------------------------------

    private enum ItemOutcome
    {
        Done,
        Skipped,
        Aborted
    }

    private async Task<ItemOutcome> ExecuteWithRetryAsync(
        FileOperation operation, PlannedItem item,
        List<FileOperationException> errors, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ExecuteAsync(operation, item, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FileOperationException ex)
            {
                if (_skipAllErrors)
                {
                    errors.Add(ex);
                    return ItemOutcome.Skipped;
                }

                var decision = await _retries
                    .OnErrorAsync(new OperationError(item, ex, attempt), cancellationToken)
                    .ConfigureAwait(false);

                switch (decision.Action)
                {
                    case RetryAction.Retry:
                        if (decision.Delay > TimeSpan.Zero)
                            await Task.Delay(decision.Delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    case RetryAction.SkipAll:
                        // Latched, so the rest of a failing operation does not ask again per
                        // item -- which for a disconnected share would be once per file.
                        _skipAllErrors = true;
                        errors.Add(ex);
                        return ItemOutcome.Skipped;
                    case RetryAction.Abort:
                        errors.Add(ex);
                        return ItemOutcome.Aborted;
                    default:
                        errors.Add(ex);
                        return ItemOutcome.Skipped;
                }
            }
        }
    }

    private async Task<ItemOutcome> ExecuteAsync(
        FileOperation operation, PlannedItem item, CancellationToken cancellationToken)
    {
        switch (operation.Kind)
        {
            case OperationKind.Trash:
                // Directories are trashed whole, so their contents are not walked into.
                if (IsUnderAnother(item, operation))
                    return ItemOutcome.Done;
                Trash.Send(item.Source);
                return ItemOutcome.Done;

            case OperationKind.Delete:
                if (IsUnderAnother(item, operation))
                    return ItemOutcome.Done;
                var deleteFs = await _provider.GetAsync(item.Source, cancellationToken).ConfigureAwait(false);
                await deleteFs.DeleteAsync(item.Source, recursive: true, cancellationToken).ConfigureAwait(false);
                return ItemOutcome.Done;

            case OperationKind.Copy:
            case OperationKind.Move:
                return await TransferAsync(operation, item, cancellationToken).ConfigureAwait(false);

            case OperationKind.Link:
                return await LinkAsync(item, cancellationToken).ConfigureAwait(false);

            default:
                throw new FileOperationException(FileErrorKind.Unsupported, item.Source,
                    $"cannot execute {operation.Kind} per item");
        }
    }

    /// <summary>Whether a planned item is inside one of the operation's own sources.</summary>
    /// <remarks>
    /// The scan walks into directories so the byte total is right, but a delete or a trash
    /// acts on the top-level source and takes the contents with it. Acting per item as well
    /// would try to delete children that are already gone.
    /// </remarks>
    private static bool IsUnderAnother(PlannedItem item, FileOperation operation)
    {
        foreach (var source in operation.Sources)
        {
            if (source.Contains(item.Source))
                return true;
        }
        return false;
    }

    private async Task<ItemOutcome> TransferAsync(
        FileOperation operation, PlannedItem item, CancellationToken cancellationToken)
    {
        if (item.Target is not { } target)
            throw new FileOperationException(FileErrorKind.Unsupported, item.Source, "no target");

        var sourceFs = await _provider.GetAsync(item.Source, cancellationToken).ConfigureAwait(false);
        var targetFs = await _provider.GetAsync(target, cancellationToken).ConfigureAwait(false);

        if (item.IsDirectory)
        {
            if (!await targetFs.ExistsAsync(target, cancellationToken).ConfigureAwait(false))
                await targetFs.CreateDirectoryAsync(target, cancellationToken).ConfigureAwait(false);
            return ItemOutcome.Done;
        }

        // A move within one filesystem is a rename, which is O(1) whatever the file's size.
        // Checked up front rather than by attempting it and catching EXDEV: some backends
        // report that indistinguishably from a real error.
        if (operation.Kind == OperationKind.Move && CanRename(sourceFs, targetFs, item.Source, target))
        {
            var resolved = await ResolveConflictAsync(targetFs, item, target, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
                return ItemOutcome.Skipped;
            target = resolved;

            if (await targetFs.ExistsAsync(target, cancellationToken).ConfigureAwait(false))
                await targetFs.DeleteAsync(target, recursive: false, cancellationToken).ConfigureAwait(false);
            await sourceFs.RenameAsync(item.Source, target, cancellationToken).ConfigureAwait(false);
            return ItemOutcome.Done;
        }

        var final = await ResolveConflictAsync(targetFs, item, target, cancellationToken).ConfigureAwait(false);
        if (final is null)
            return ItemOutcome.Skipped;

        if (item.IsLink)
            return await CopyLinkAsync(sourceFs, targetFs, item, final, cancellationToken).ConfigureAwait(false);

        await CopyBytesAsync(sourceFs, targetFs, item, final, cancellationToken).ConfigureAwait(false);
        return ItemOutcome.Done;
    }

    /// <summary>Reproduces a symbolic link at <paramref name="target"/>.</summary>
    /// <remarks>
    /// The link's text is copied as it is, relative or absolute, so a relative link inside a
    /// copied tree points into the copy. Where the link cannot be made -- a remote or a phone
    /// cannot hold one, and a remote cannot say what a link holds -- a link to a file is copied
    /// as the file, which is what anything that follows it would have read. A link to a
    /// directory is refused instead: copying the directory is the walk that never ends when the
    /// link points at its own ancestor.
    /// </remarks>
    private async Task<ItemOutcome> CopyLinkAsync(
        IFileSystem sourceFs, IFileSystem targetFs, PlannedItem item, Location target,
        CancellationToken cancellationToken)
    {
        if (item.LinkTarget is { } linkTarget && targetFs.Capabilities.HasFlag(FileSystemCapabilities.SymLink))
        {
            if (await targetFs.ExistsAsync(target, cancellationToken).ConfigureAwait(false))
                await targetFs.DeleteAsync(target, recursive: false, cancellationToken).ConfigureAwait(false);
            await targetFs.CreateSymlinkAsync(target, linkTarget, cancellationToken).ConfigureAwait(false);
            return ItemOutcome.Done;
        }

        var pointsAt = await sourceFs.StatAsync(item.Source, cancellationToken).ConfigureAwait(false);
        if (pointsAt.IsDirectory)
            throw new FileOperationException(FileErrorKind.Unsupported, item.Source, "cannot copy a link to a directory here");

        await CopyBytesAsync(sourceFs, targetFs, item, target, cancellationToken).ConfigureAwait(false);
        return ItemOutcome.Done;
    }

    private bool CanRename(IFileSystem source, IFileSystem target, Location from, Location to)
    {
        if (!ReferenceEquals(source, target) || !source.Capabilities.HasFlag(FileSystemCapabilities.Rename))
            return false;
        if (!string.Equals(from.MountKey, to.MountKey, StringComparison.Ordinal))
            return false;

        // One mount key can still span filesystems: every local path shares "file://", but
        // /home and /mnt are different devices and a rename between them fails.
        if (from.IsLocal && _mounts is not null)
            return _mounts.IsSameFilesystem(from, to);

        return true;
    }

    /// <summary>
    /// Settles what to do about an existing target, returning where to write or null to skip.
    /// </summary>
    private async Task<Location?> ResolveConflictAsync(
        IFileSystem targetFs, PlannedItem item, Location target, CancellationToken cancellationToken)
    {
        if (!await targetFs.ExistsAsync(target, cancellationToken).ConfigureAwait(false))
            return target;

        var decision = _conflictForAll ?? await AskAsync().ConfigureAwait(false);

        switch (decision.Action)
        {
            case ConflictAction.Overwrite:
                return target;
            case ConflictAction.Skip:
                return null;
            case ConflictAction.Rename:
                var name = decision.NewName ?? ConflictNaming.NextName(target.Name);
                var parent = target.Parent ?? target;
                // The renamed target can itself be taken, which is what happens when a
                // directory is copied into its own parent twice.
                var candidate = parent.Child(name);
                while (await targetFs.ExistsAsync(candidate, cancellationToken).ConfigureAwait(false))
                    candidate = parent.Child(ConflictNaming.NextName(candidate.Name));
                return candidate;
            case ConflictAction.OverwriteIfNewer:
                var existing = await targetFs.StatAsync(target, cancellationToken).ConfigureAwait(false);
                var sourceFs = await _provider.GetAsync(item.Source, cancellationToken).ConfigureAwait(false);
                var incoming = await sourceFs.StatAsync(item.Source, cancellationToken).ConfigureAwait(false);
                return incoming.Modified > existing.Modified ? target : null;
            default:
                throw new OperationCanceledException(cancellationToken);
        }

        async Task<ConflictDecision> AskAsync()
        {
            var existing = await targetFs.StatAsync(target, cancellationToken).ConfigureAwait(false);
            var sourceFs = await _provider.GetAsync(item.Source, cancellationToken).ConfigureAwait(false);
            var incoming = await sourceFs.StatAsync(item.Source, cancellationToken).ConfigureAwait(false);

            var answer = await _conflicts.ResolveAsync(new ConflictContext(
                item.Source, target, incoming.Size, existing.Size,
                incoming.Modified, existing.Modified, item.IsDirectory), cancellationToken).ConfigureAwait(false);

            // Cached so a copy of a thousand colliding files asks once. Rename is never
            // cached: each one needs its own free name.
            if (answer.ApplyToAll && answer.Action != ConflictAction.Rename)
                _conflictForAll = answer;
            return answer;
        }
    }

    /// <summary>Points a new symlink at one source.</summary>
    /// <remarks>
    /// The link is written with the source's <i>absolute</i> path rather than a path relative
    /// to the target directory. Relative would survive the pair being moved together, which
    /// almost never happens; absolute survives the link itself being moved, which happens all
    /// the time — and a link into a directory the user just dragged onto is far more likely to
    /// be filed away later than to stay put beside its target.
    ///
    /// <para>
    /// Refused rather than approximated when the backend cannot make links. Copying the file
    /// instead would answer "make me a reference" with a second copy, and the user would not
    /// find out until the two drifted apart.
    /// </para>
    /// </remarks>
    private async Task<ItemOutcome> LinkAsync(PlannedItem item, CancellationToken cancellationToken)
    {
        if (item.Target is not { } target)
            throw new FileOperationException(FileErrorKind.Unsupported, item.Source, "no target");

        var targetFs = await _provider.GetAsync(target, cancellationToken).ConfigureAwait(false);
        if (!targetFs.Capabilities.HasFlag(FileSystemCapabilities.SymLink))
            throw new FileOperationException(FileErrorKind.Unsupported, target, "no symlinks here");

        // A link across filesystems is fine -- it holds a path, not a device reference -- but
        // a link to a remote location is not: the path it would hold means nothing locally.
        if (!item.Source.TryGetLocalPath(out var sourcePath))
            throw new FileOperationException(FileErrorKind.Unsupported, item.Source, "not a local path");

        var final = await ResolveConflictAsync(targetFs, item, target, cancellationToken).ConfigureAwait(false);
        if (final is null)
            return ItemOutcome.Skipped;

        if (await targetFs.ExistsAsync(final, cancellationToken).ConfigureAwait(false))
            await targetFs.DeleteAsync(final, recursive: false, cancellationToken).ConfigureAwait(false);
        await targetFs.CreateSymlinkAsync(final, sourcePath, cancellationToken).ConfigureAwait(false);
        return ItemOutcome.Done;
    }

    /// <summary>Copies one file's bytes through a part file.</summary>
    private async Task CopyBytesAsync(
        IFileSystem sourceFs, IFileSystem targetFs, PlannedItem item, Location target,
        CancellationToken cancellationToken)
    {
        var parent = target.Parent ?? target;
        var part = parent.Child(target.Name + PartSuffix);

        try
        {
            await using (var input = Counted(await sourceFs.OpenReadAsync(item.Source, cancellationToken).ConfigureAwait(false)))
            await using (var output = await targetFs.OpenWriteAsync(part, WriteMode.Overwrite, item.Size, cancellationToken).ConfigureAwait(false))
            {
                await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
            }

            // Only now does the real name appear, and it appears complete.
            if (await targetFs.ExistsAsync(target, cancellationToken).ConfigureAwait(false))
                await targetFs.DeleteAsync(target, recursive: false, cancellationToken).ConfigureAwait(false);
            await targetFs.RenameAsync(part, target, cancellationToken).ConfigureAwait(false);

            if (targetFs.Capabilities.HasFlag(FileSystemCapabilities.PreservesMTime))
            {
                var stat = await sourceFs.StatAsync(item.Source, cancellationToken).ConfigureAwait(false);
                if (stat.Modified is { } modified)
                    await targetFs.SetModifiedAsync(target, modified, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // A part file from a failed or canceled copy is swept immediately rather than
            // left for a later run to puzzle over.
            await TryDeleteAsync(targetFs, part).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Deletes the sources of a move that copied everything successfully.</summary>
    /// <remarks>
    /// Deferred to the end, and skipped entirely if any item was skipped or failed. A
    /// half-copied tree is recoverable because both copies exist; a half-copied tree whose
    /// original has been deleted is not, and the files lost would be precisely the ones that
    /// could not be copied. The caller reports the move as incomplete and leaves both in
    /// place for the user to sort out.
    /// </remarks>
    private async Task DeleteMovedSourcesAsync(
        FileOperation operation,
        List<FileOperationException> errors, CancellationToken cancellationToken)
    {
        foreach (var source in operation.Sources)
        {
            try
            {
                var fs = await _provider.GetAsync(source, cancellationToken).ConfigureAwait(false);
                if (await fs.ExistsAsync(source, cancellationToken).ConfigureAwait(false))
                    await fs.DeleteAsync(source, recursive: true, cancellationToken).ConfigureAwait(false);
            }
            catch (FileOperationException ex)
            {
                // The copy succeeded; failing to tidy up the source is worth reporting but
                // must not be reported as the move having failed.
                errors.Add(ex);
            }
        }
    }

    /// <summary>Writes every planned item into one archive, through a part file.</summary>
    /// <remarks>
    /// Members are named relative to the directory the archive is made in, so compressing
    /// search results keeps the subdirectories they were found in rather than flattening them
    /// into names that may collide. A source from anywhere else is named from its own parent.
    ///
    /// <para>
    /// The archive never overwrites. The caller picked a free name, and something appearing
    /// there in the meantime is somebody else's file.
    /// </para>
    /// </remarks>
    private async Task CompressAsync(
        FileOperation operation, TransferPlan plan,
        ProgressThrottle<OperationProgress> throttle, CancellationToken cancellationToken)
    {
        var archive = operation.Target
            ?? throw new FileOperationException(FileErrorKind.Unsupported, Location.FromLocalPath("/"), "no target");
        var format = operation.Format
            ?? throw new FileOperationException(FileErrorKind.Unsupported, archive, "no archive format");
        var home = archive.Parent ?? archive;
        var targetFs = await _provider.GetAsync(archive, cancellationToken).ConfigureAwait(false);
        var part = home.Child(archive.Name + PartSuffix);

        try
        {
            await using (var output = await targetFs.OpenWriteAsync(part, WriteMode.Overwrite, null, cancellationToken).ConfigureAwait(false))
            await using (var writer = ArchiveWriter.Create(format, output))
            {
                var bytesDone = 0L;
                var done = 0;
                var files = new FileCount(plan);
                foreach (var planned in plan.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = planned;
                    var name = MemberName(item.Source, operation.Sources, home);
                    var sourceFs = await _provider.GetAsync(item.Source, cancellationToken).ConfigureAwait(false);
                    // A local listing leaves the mode out, since it costs a syscall per entry
                    // that the details view never shows. An archive does need it, or every
                    // script inside a directory comes back out no longer executable.
                    if (item is { UnixMode: null, IsLink: false }
                        && sourceFs.Capabilities.HasFlag(FileSystemCapabilities.PosixMode))
                    {
                        var stat = await sourceFs.StatAsync(item.Source, cancellationToken).ConfigureAwait(false);
                        item = item with { UnixMode = stat.UnixMode };
                    }
                    if (_pause is { } gate)
                        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var before = bytesDone;
                    var current = item;
                    var filesBefore = files.Done;
                    throttle.Report(Working(plan, done, files, filesBefore, before, current));
                    _itemBytes = read => throttle.Report(Working(plan, done, files, filesBefore, before + Math.Min(read, current.Size), current));
                    try
                    {
                        await writer.WriteAsync(sourceFs, item, name, Counted, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _itemBytes = null;
                    }

                    done++;
                    bytesDone += item.Size;
                    files.Passed(item);
                    throttle.Report(Working(plan, done, files, files.Done, bytesDone, item));
                }
            }

            if (await targetFs.ExistsAsync(archive, cancellationToken).ConfigureAwait(false))
                throw new FileOperationException(FileErrorKind.AlreadyExists, archive, "the archive name was taken");
            await targetFs.RenameAsync(part, archive, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException and not FileOperationException or InvalidDataException)
        {
            // The streams between here and the disk -- gzip, the tar and zip writers -- throw
            // their own exceptions, which would otherwise reach the user without saying which
            // file they were about.
            await TryDeleteAsync(targetFs, part).ConfigureAwait(false);
            throw new FileOperationException(FileErrorKind.Unknown, archive, ex.Message, ex);
        }
        catch
        {
            await TryDeleteAsync(targetFs, part).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The path a planned item has inside the archive.</summary>
    internal static string MemberName(Location item, IReadOnlyList<Location> sources, Location home)
    {
        var source = sources.FirstOrDefault(s => s == item || s.Contains(item)) ?? item;
        var root = home.Contains(source) ? home : source.Parent ?? source;
        if (root.IsRoot)
            return item.Path.TrimStart('/');
        return item.Path[(root.Path.Length + 1)..];
    }

    private async Task CreateDirectoryAsync(FileOperation operation, CancellationToken cancellationToken)
    {
        var target = operation.Target
            ?? throw new FileOperationException(FileErrorKind.Unsupported, Location.FromLocalPath("/"), "no target");
        var fs = await _provider.GetAsync(target, cancellationToken).ConfigureAwait(false);
        await fs.CreateDirectoryAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private async Task RenameAsync(FileOperation operation, CancellationToken cancellationToken)
    {
        var source = operation.Sources[0];
        var target = operation.Target
            ?? throw new FileOperationException(FileErrorKind.Unsupported, source, "no target");
        var fs = await _provider.GetAsync(source, cancellationToken).ConfigureAwait(false);
        await fs.RenameAsync(source, target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The source stream, counting what is read from it when anything is listening.</summary>
    private Stream Counted(Stream input) =>
        _itemBytes is not null || _pause is not null
            ? new ProgressReadStream(input, _itemBytes, _pause, _token)
            : input;

    private static async Task TryDeleteAsync(IFileSystem fs, Location location)
    {
        try
        {
            if (await fs.ExistsAsync(location, CancellationToken.None).ConfigureAwait(false))
                await fs.DeleteAsync(location, recursive: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (FileOperationException)
        {
            // Best effort during failure handling.
        }
    }
}
