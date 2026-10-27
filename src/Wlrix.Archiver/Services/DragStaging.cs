using Wlrix.Archiver.Models;
using Wlrix.Archiver.Services.Archives;
using Wlrix.Common;

namespace Wlrix.Archiver.Services;

/// <summary>Materializes archive entries on disk so they can be dragged out.</summary>
/// <remarks>
/// A drag hands the other application a list of file URIs, and an entry inside an archive has no
/// URI — there is no path that names it. So the selection is extracted to a scratch directory
/// first and those paths are what get offered. Ark does the same thing, and so does every file
/// manager dragging out of a mounted archive.
///
/// The alternative is XDS (<c>XdndDirectSave</c>), where the *target* names a destination and the
/// source writes straight into it, avoiding the copy. It is X11-only and has no Wayland
/// equivalent, so it is not an option here.
///
/// The scratch directory is per-process and cleared on the way out. It lives under the app's own
/// data directory rather than <c>/tmp</c> because an extracted archive can be large and
/// <c>/tmp</c> is a tmpfs on this system — filling RAM to complete a drag is not a good trade.
/// </remarks>
public sealed class DragStaging : IDisposable
{
    private readonly IArchiveBackendRegistry _registry;
    private readonly string _root;
    private int _sequence;

    public DragStaging(IArchiveBackendRegistry registry)
        : this(registry, Path.Combine(ApplicationPaths.AppData, "archiver", "drag",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
    {
    }

    /// <summary>Stages under <paramref name="root"/> instead; for tests.</summary>
    internal DragStaging(IArchiveBackendRegistry registry, string root)
    {
        _registry = registry;
        _root = root;
    }

    /// <summary>
    /// Extracts <paramref name="entryPaths"/> and returns the files to offer to the drop target.
    /// </summary>
    /// <remarks>
    /// What is handed over is the selected entry itself: dragging <c>src/main.c</c> out drops a
    /// bare <c>main.c</c>, and dragging <c>src/lib</c> drops a <c>lib</c> directory with
    /// everything under it. The archive's own folders above the entry are what the user
    /// browsed through to reach it, not part of what they picked up.
    /// </remarks>
    /// <param name="password">
    /// Whatever the window already knows, if the archive is encrypted. A drag cannot stop to ask
    /// -- the compositor needs the drop handler back before the transfer finishes -- so an
    /// encrypted archive that has not been unlocked yet fails the drag rather than prompting
    /// mid-gesture. Opening or extracting it first is what gets the password known.
    /// </param>
    public async Task<IReadOnlyList<string>> StageAsync(OpenArchive archive,
        IReadOnlyList<string> entryPaths, string? password = null,
        CancellationToken cancellationToken = default)
    {
        if (entryPaths.Count == 0)
            return [];

        var backend = _registry.For(archive.Format)
            ?? throw new ArchiveException($"Nothing can read {archive.Format} archives.");

        // A fresh subdirectory per drag: two drags of different entries with the same name must
        // not overwrite each other, and the second must not silently hand over the first's file.
        var directory = Path.Combine(_root,
            (++_sequence).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);

        await backend.ExtractAsync(archive.Path, archive.Format, entryPaths, directory,
            flatten: false, password, progress: null, cancellationToken).ConfigureAwait(false);

        // Extracted with full paths so names cannot collide inside the scratch directory, then
        // each selected entry is offered from where it landed. An entry inside another selected
        // directory is already carried by that directory, and offering it again would drop it
        // twice: once in place and once loose beside it.
        var selected = entryPaths
            .Select(ArchiveTree.Normalize)
            .Where(entry => entry.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return selected
            .Where(entry => !selected.Any(other => entry.StartsWith(other + "/", StringComparison.Ordinal)))
            .Select(entry => Path.Combine(directory, entry))
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToList();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Scratch files outliving the process is untidy, not broken, and the next run uses
            // a different pid anyway. Not worth failing a shutdown over.
        }
    }
}
