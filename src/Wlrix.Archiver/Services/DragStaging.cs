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
    {
        _registry = registry;
        _root = Path.Combine(ApplicationPaths.AppData, "archiver", "drag",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Extracts <paramref name="entryPaths"/> and returns the files to offer to the drop target.
    /// </summary>
    /// <remarks>
    /// Full paths are preserved, so dragging <c>src/main.c</c> out hands over a <c>src</c>
    /// directory containing it rather than a bare <c>main.c</c> — which is what the receiving
    /// side would have got by extracting the archive itself.
    /// </remarks>
    public async Task<IReadOnlyList<string>> StageAsync(OpenArchive archive,
        IReadOnlyList<string> entryPaths, CancellationToken cancellationToken = default)
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
            flatten: false, progress: null, cancellationToken).ConfigureAwait(false);

        // Offer the topmost thing each selected entry produced, not every file underneath it:
        // dragging a directory should drop one directory, not a flat pile of its contents.
        return entryPaths
            .Select(entry => Path.Combine(directory, TopComponent(entry)))
            .Distinct(StringComparer.Ordinal)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToList();
    }

    private static string TopComponent(string entryPath)
    {
        var normalized = ArchiveTree.Normalize(entryPath);
        var slash = normalized.IndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
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
