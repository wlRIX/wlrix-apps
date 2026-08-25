using Wlrix.Archiver.Models;
using Wlrix.Archiver.Services.Archives;
using Wlrix.Archiver.Services.Encodings;
using Xunit;

namespace Wlrix.Archiver.Tests;

public class ArchiveBackendRegistryTests
{
    private static ArchiveBackendRegistry Registry(params IArchiveBackend[] backends) =>
        new(backends.Length == 0 ? [new SharpCompressBackend(new FilenameDecoder())] : backends);

    [Theory]
    [InlineData("sjis-names.zip", ArchiveFormat.Zip)]
    [InlineData("modes.tar", ArchiveFormat.Tar)]
    [InlineData("modes.tar.gz", ArchiveFormat.TarGz)]
    public void RealArchivesAreIdentifiedCorrectly(string fixture, ArchiveFormat expected)
    {
        Assert.Equal(expected, Registry().Identify(Fixture.Path(fixture)));
    }

    [Fact]
    public void ContentsBeatTheExtensionWhenTheTwoDisagree()
    {
        // An extension is a hint the user controls. Opening a renamed zip as a tar produces a
        // parse error rather than the archive that is plainly there.
        using var work = new TemporaryDirectory();
        var lying = Path.Combine(work.Path, "actually-a-zip.tar");
        File.Copy(Fixture.Path("sjis-names.zip"), lying);

        Assert.Equal(ArchiveFormat.Zip, Registry().Identify(lying));
    }

    [Fact]
    public void AGzipStreamNeedsItsExtensionToSayWhetherAТarIsInside()
    {
        // The gzip header says nothing about its payload, so this is the one question the magic
        // bytes cannot answer and the compound extension has to.
        using var work = new TemporaryDirectory();
        var bare = Path.Combine(work.Path, "notes.gz");
        File.Copy(Fixture.Path("modes.tar.gz"), bare);

        Assert.Equal(ArchiveFormat.GZip, Registry().Identify(bare));
        Assert.Equal(ArchiveFormat.TarGz, Registry().Identify(Fixture.Path("modes.tar.gz")));
    }

    [Theory]
    [InlineData("backup.tar.gz", ArchiveFormat.TarGz)]
    [InlineData("backup.tgz", ArchiveFormat.TarGz)]
    [InlineData("backup.tar.bz2", ArchiveFormat.TarBz2)]
    [InlineData("backup.gz", ArchiveFormat.GZip)]
    [InlineData("notes.txt", ArchiveFormat.Unknown)]
    public void CompoundExtensionsAreMatchedWholeRatherThanByTheirLastComponent(string name,
        ArchiveFormat expected)
    {
        // GetExtension answers ".gz" for "backup.tar.gz", which would make a tarball look like a
        // single gzipped file with no entries in it.
        Assert.Equal(expected, ArchiveFormats.FromPath(name));
    }

    [Fact]
    public void AMissingFileFallsBackToItsNameInsteadOfThrowing()
    {
        Assert.Equal(ArchiveFormat.Zip, Registry().Identify("/nonexistent/thing.zip"));
    }

    [Fact]
    public void TheMoreCapableBackendWinsForAFormatBothCanHandle()
    {
        var managed = new SharpCompressBackend(new FilenameDecoder());
        var writer = new FakeBackend(ArchiveFormat.SevenZip, ArchiveCapabilities.All);

        // SharpCompress reads 7z and cannot write it; the external tool can do both. This is
        // what makes 7z writable exactly when 7z is installed.
        Assert.Same(writer, Registry(managed, writer).For(ArchiveFormat.SevenZip));
        Assert.Equal(ArchiveCapabilities.All,
            Registry(managed, writer).CapabilitiesFor(ArchiveFormat.SevenZip));
    }

    [Fact]
    public void AnUnavailableExternalToolLeavesTheManagedBackendInCharge()
    {
        var managed = new SharpCompressBackend(new FilenameDecoder());
        var absent = new FakeBackend(ArchiveFormat.SevenZip, ArchiveCapabilities.None);

        Assert.Same(managed, Registry(managed, absent).For(ArchiveFormat.SevenZip));
        Assert.Equal(ArchiveCapabilities.ReadOnly,
            Registry(managed, absent).CapabilitiesFor(ArchiveFormat.SevenZip));
    }

    [Fact]
    public void AFormatNothingHandlesHasNoBackendAndNoCapabilities()
    {
        Assert.Null(Registry().For(ArchiveFormat.Unknown));
        Assert.Equal(ArchiveCapabilities.None, Registry().CapabilitiesFor(ArchiveFormat.Unknown));
    }

    /// <summary>A backend that claims one format, for testing the choice between them.</summary>
    private sealed class FakeBackend(ArchiveFormat format, ArchiveCapabilities capabilities)
        : IArchiveBackend
    {
        public string Name => "fake";

        public ArchiveCapabilities Supports(ArchiveFormat other) =>
            other == format ? capabilities : ArchiveCapabilities.None;

        public Task<OpenArchive> OpenAsync(string path, ArchiveFormat f,
            IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(new OpenArchive(path, f, capabilities, []));

        public Task ExtractAsync(string path, ArchiveFormat f, IReadOnlyList<string> entries,
            string destination, bool flatten = false,
            IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task AddAsync(string path, ArchiveFormat f, IReadOnlyList<string> sources,
            string prefix = "", CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string path, ArchiveFormat f, IReadOnlyList<string> entries,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task CreateAsync(string path, ArchiveFormat f, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
