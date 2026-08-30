using Wlrix.Archiver.Models;
using Wlrix.Archiver.Services.Archives;
using Wlrix.Archiver.Services.Encodings;
using Wlrix.Archiver.ViewModels;
using Xunit;

namespace Wlrix.Archiver.Tests;

public class ProgressTests
{
    private static SharpCompressBackend Backend() => new(new FilenameDecoder());

    /// <summary>Collects reports without needing a synchronization context.</summary>
    private sealed class Recorder : IProgress<ArchiveProgress>
    {
        public List<ArchiveProgress> Reports { get; } = [];

        public void Report(ArchiveProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task ReadingACompressedTarReportsDecompressionBeforeItReportsEntries()
    {
        // The order matters because it is the whole point: on a large gzip the decompression is
        // essentially all of the wait, so it has to be the thing the status line talks about
        // first rather than an unexplained pause before the counting starts.
        var recorder = new Recorder();

        await Backend().OpenAsync(Fixture.Path("modes.tar.gz"), ArchiveFormat.TarGz, recorder);

        var phases = recorder.Reports.Select(report => report.Phase).ToList();
        Assert.Contains(ArchivePhase.Decompressing, phases);
        Assert.Contains(ArchivePhase.Reading, phases);
        Assert.True(phases.IndexOf(ArchivePhase.Decompressing)
            < phases.IndexOf(ArchivePhase.Reading));
    }

    [Fact]
    public async Task DecompressionReportsARealFractionAndEntryCountingDoesNot()
    {
        var recorder = new Recorder();

        await Backend().OpenAsync(Fixture.Path("modes.tar.gz"), ArchiveFormat.TarGz, recorder);

        // Bytes read against the file's length is a number that exists up front. How many
        // entries an archive holds is not, so a fraction there would be invented.
        Assert.All(recorder.Reports.Where(r => r.Phase == ArchivePhase.Decompressing),
            report => Assert.InRange(report.Fraction ?? -1, 0, 1));
        Assert.All(recorder.Reports.Where(r => r.Phase == ArchivePhase.Reading),
            report => Assert.Null(report.Fraction));
    }

    [Fact]
    public async Task ExtractionReportsTheNumberOfEntriesWrittenSoFar()
    {
        using var destination = new TemporaryDirectory();
        var recorder = new Recorder();

        await Backend().ExtractAsync(Fixture.Path("no-dir-entries.zip"), ArchiveFormat.Zip,
            [], destination.Path, flatten: false, recorder);

        var counts = recorder.Reports
            .Where(report => report.Phase == ArchivePhase.Extracting)
            .Select(report => report.Count)
            .ToList();
        Assert.NotEmpty(counts);
        // Monotonic: a count that goes backwards would read as work being undone.
        Assert.Equal(counts.OrderBy(count => count), counts);
    }

    [Fact]
    public async Task AnArchiveOpensJustAsWellWithNobodyWatchingTheProgress()
    {
        // Null is the ordinary case for every non-UI caller — DragStaging passes it — so the
        // reporting must not be on the path that does the work.
        var archive = await Backend()
            .OpenAsync(Fixture.Path("modes.tar.gz"), ArchiveFormat.TarGz, progress: null);

        Assert.Contains(archive.Entries, entry => entry.Path == "bin/blender");
    }

    [Fact]
    public async Task CancelingAReadThrowsRatherThanReturningHalfAnArchive()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Backend().OpenAsync(Fixture.Path("modes.tar.gz"), ArchiveFormat.TarGz, null,
                source.Token));
    }

    [Fact]
    public async Task ACanceledReadLeavesNoScratchFileBehind()
    {
        // The scratch file for a compressed tar is the size of the whole uncompressed archive —
        // 9.3 GB for the tarball this was found on — so leaking one is not a tidiness question.
        var scratch = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "wlrix", "archiver", "scratch");
        var before = Directory.Exists(scratch) ? Directory.GetFiles(scratch).Length : 0;

        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        try
        {
            await Backend().OpenAsync(Fixture.Path("modes.tar.gz"), ArchiveFormat.TarGz, null,
                source.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected; the point of the test is what is left on disk afterwards.
        }

        var after = Directory.Exists(scratch) ? Directory.GetFiles(scratch).Length : 0;
        Assert.Equal(before, after);
    }

    [Fact]
    public void ScratchFilesDoNotGoUnderTheSystemTemporaryDirectory()
    {
        // `/tmp` is a tmpfs on this desktop and on most systemd ones, so a scratch file there is
        // RAM. Unpacking a 3.1 GB tarball to read its listing put 9.3 GB in memory before this
        // was moved to the app's own data directory, which is on real disk.
        var scratch = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "wlrix", "archiver", "scratch");

        Assert.False(scratch.StartsWith(Path.GetTempPath(), StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheTopLevelStartsExpandedHoweverDeepTheArchiveGoes()
    {
        // Expanding every directory is what locked the window for seconds on a 29,631-entry
        // tarball: 5,304 directories, every row realized at once, long after the read had
        // already finished.
        var entries = new[] { "a/b/c/d.txt", "a/b/e.txt", "f/g.txt" }
            .Select(path => new ArchiveEntry(path, false, 1, 1, null, null, null, null));
        var columns = new ColumnLayout();

        var roots = ArchiveTree.Build(entries)
            .Select(node => new EntryNodeViewModel(node, columns))
            .ToList();

        foreach (var node in roots.SelectMany(root => root.Descend()))
            Assert.Equal(node.Depth == 0 && node.IsDirectory, node.IsExpanded);
    }
}
