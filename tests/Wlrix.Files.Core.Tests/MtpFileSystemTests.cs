using System.Runtime.InteropServices;
using System.Text;
using Wlrix.Files.Core.Mtp;
using Wlrix.Files.Core.Operations;
using Wlrix.Files.Core.Testing;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// A phone as a filesystem: paths over handles, the device's one thread, and the streams.
/// </summary>
/// <remarks>
/// Against <see cref="FakeMtpDevice"/>, which also fails any test that reaches the device from
/// a second thread -- the one rule libmtp cannot report breaking, because it corrupts its
/// session instead of throwing.
/// </remarks>
public class MtpFileSystemTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Internal = "Internal shared storage";

    private static readonly Location Root = MtpFileSystemFactory.RootOf("pixel");

    private static Location At(string path) => Location.Parse("mtp://pixel" + path);

    private static Location InStorage(string path) => Root.Child(Internal).Combine(path);

    private static async Task<(MtpFileSystem Fs, FakeMtpDevice Device, uint Storage)> Mounted(
        Action<FakeMtpDevice, uint>? build = null)
    {
        var device = new FakeMtpDevice();
        var storage = device.AddStorage(Internal);
        build?.Invoke(device, storage);
        var fs = await MtpFileSystem.OpenAsync(Root, () => device, None);
        return (fs, device, storage);
    }

    private static async Task<List<FileEntry>> List(IFileSystem fs, Location location)
    {
        var entries = new List<FileEntry>();
        await foreach (var entry in fs.EnumerateAsync(location, None))
            entries.Add(entry);
        return entries;
    }

    private static async Task<byte[]> Read(IFileSystem fs, Location location)
    {
        await using var stream = await fs.OpenReadAsync(location, None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static async Task Write(IFileSystem fs, Location location, byte[] content, long? length, WriteMode mode = WriteMode.Overwrite)
    {
        await using var stream = await fs.OpenWriteAsync(location, mode, length, None);
        await stream.WriteAsync(content);
    }

    // --- the tree ---------------------------------------------------------

    [Fact]
    public async Task TheDeviceRootListsItsStoragesAsFolders()
    {
        var (fs, device, _) = await Mounted();
        device.AddStorage("SD card");

        var entries = await List(fs, Root);

        Assert.Equal([Internal, "SD card"], entries.Select(e => e.Name));
        Assert.All(entries, entry => Assert.True(entry.IsDirectory));
    }

    [Fact]
    public void StoragesWithTheSameNameAreNumberedAndSlashesCannotSplitThem()
    {
        var named = MtpFileSystem.NameStorages(
        [
            new MtpStorageInfo(1, "Card", 0, 0, false),
            new MtpStorageInfo(2, "Card", 0, 0, false),
            new MtpStorageInfo(3, "A/B", 0, 0, false),
            new MtpStorageInfo(4, "  ", 0, 0, false),
        ]);

        Assert.Equal(["Card", "Card (2)", "A-B", "Storage 00000004"], named.Select(s => s.Name));
    }

    [Fact]
    public async Task ALockedPhoneSaysSoRatherThanLookingEmpty()
    {
        // An Android phone hides every storage until it is unlocked. An empty listing would
        // read as a phone with nothing on it.
        var (fs, device, _) = await Mounted();
        device.Locked = true;

        var error = await Assert.ThrowsAsync<FileOperationException>(() => List(fs, Root));

        Assert.Equal(FileErrorKind.AccessDenied, error.Kind);
        Assert.Contains("Unlock", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStorageAppearsOnceThePhoneIsUnlocked()
    {
        var (fs, device, _) = await Mounted();
        device.Locked = true;
        await Assert.ThrowsAsync<FileOperationException>(() => List(fs, Root));

        device.Locked = false;

        Assert.Single(await List(fs, Root));
    }

    [Fact]
    public async Task PathsWalkDownThroughFolders()
    {
        var (fs, _, _) = await Mounted((device, storage) =>
        {
            var dcim = device.AddFolder(storage, MtpDevice.Root, "DCIM");
            var camera = device.AddFolder(storage, dcim, "Camera");
            device.AddFile(storage, camera, "PXL_0001.jpg", new byte[1234]);
        });

        var photo = InStorage("DCIM/Camera/PXL_0001.jpg");
        var stat = await fs.StatAsync(photo, None);

        Assert.Equal(FileKind.File, stat.Kind);
        Assert.Equal(1234, stat.Size);
        Assert.True((await fs.StatAsync(InStorage("DCIM"), None)).IsDirectory);
        Assert.False(await fs.ExistsAsync(InStorage("DCIM/Nope"), None));
    }

    [Fact]
    public void AStorageNameWithSpacesSurvivesTheRoundTripThroughAUri()
    {
        // Everything is keyed by the canonical string -- bookmarks, the session file, the
        // window registry -- so the storage folder has to come back out as the same location.
        var photo = InStorage("DCIM/a b.jpg");

        var again = Location.Parse(photo.ToUriString());

        Assert.Equal(photo, again);
        Assert.Equal("/" + Internal + "/DCIM/a b.jpg", again.Path);
        Assert.Equal("mtp://pixel", again.MountKey);
    }

    [Fact]
    public async Task ReachingOneFileDoesNotAskAboutAllOfItsNeighbors()
    {
        // MTP has no lookup by name, and describing a handle is a request each. A folder of
        // fifteen thousand downloads must not cost fifteen thousand requests to open one.
        var (fs, device, _) = await Mounted((device, storage) =>
        {
            var download = device.AddFolder(storage, MtpDevice.Root, "Download");
            device.AddFile(storage, download, "wanted.pdf", [1]);
            for (var i = 0; i < 500; i++)
                device.AddFile(storage, download, $"other-{i}.bin", [2]);
        });

        Assert.True(await fs.ExistsAsync(InStorage("Download/wanted.pdf"), None));

        Assert.True(device.MetadataRequests < 10, $"{device.MetadataRequests} metadata requests");
    }

    [Fact]
    public async Task ListingAgainOnlyAsksAboutWhatIsNew()
    {
        // The pane polls by listing. Without remembering what each handle was, every poll of a
        // big folder would be minutes of requests.
        uint download = 0;
        var (fs, device, storage) = await Mounted((device, storage) =>
        {
            download = device.AddFolder(storage, MtpDevice.Root, "Download");
            for (var i = 0; i < 100; i++)
                device.AddFile(storage, download, $"f{i}", [1]);
        });
        Assert.Equal(100, (await List(fs, InStorage("Download"))).Count);
        var before = device.MetadataRequests;

        device.AddFile(storage, download, "new", [1]);
        var again = await List(fs, InStorage("Download"));

        Assert.Equal(101, again.Count);
        Assert.Equal(before + 1, device.MetadataRequests);
    }

    [Fact]
    public async Task SomethingDeletedOnThePhoneDropsOutOfTheNextListing()
    {
        uint doomed = 0;
        var (fs, device, _) = await Mounted((device, storage) =>
        {
            device.AddFile(storage, MtpDevice.Root, "keep", [1]);
            doomed = device.AddFile(storage, MtpDevice.Root, "doomed", [1]);
        });
        Assert.Equal(2, (await List(fs, InStorage(""))).Count);

        device.DeleteOnPhone(doomed);

        Assert.Equal(["keep"], (await List(fs, InStorage(""))).Select(e => e.Name));
    }

    [Fact]
    public async Task DotfilesAreMarkedHidden()
    {
        var (fs, _, _) = await Mounted((device, storage) =>
        {
            device.AddFolder(storage, MtpDevice.Root, ".thumbnails");
            device.AddFolder(storage, MtpDevice.Root, "DCIM");
        });

        var entries = await List(fs, InStorage(""));

        Assert.True(entries.Single(e => e.Name == ".thumbnails").IsHidden);
        Assert.False(entries.Single(e => e.Name == "DCIM").IsHidden);
    }

    [Fact]
    public async Task FreeSpaceAtTheRootIsEveryStorageTogether()
    {
        var (fs, device, _) = await Mounted();
        device.AddStorage("SD card", capacity: 1000, free: 400);

        var root = await fs.GetFreeSpaceAsync(Root, None);
        var card = await fs.GetFreeSpaceAsync(Root.Child("SD card"), None);

        Assert.Equal(new FreeSpace(64_000_001_000, 32_000_000_400), root);
        Assert.Equal(new FreeSpace(1000, 400), card);
    }

    [Fact]
    public async Task ALockedPhoneHasNoFreeSpaceFigureRatherThanAnError()
    {
        var (fs, device, _) = await Mounted();
        device.Locked = true;

        Assert.Null(await fs.GetFreeSpaceAsync(Root, None));
    }

    // --- streams ------------------------------------------------------------

    [Fact]
    public async Task AFileReadsBackWhole()
    {
        var content = Encoding.UTF8.GetBytes(new string('x', 20_000));
        var (fs, _, _) = await Mounted((device, storage) => device.AddFile(storage, MtpDevice.Root, "big.txt", content));

        Assert.Equal(content, await Read(fs, InStorage("big.txt")));
    }

    [Fact]
    public async Task ClosingAReadEarlyFreesTheDevice()
    {
        // A transfer holds the device until it ends. One abandoned halfway -- a preview that
        // only wanted the header -- must end, or nothing else on the phone could happen again.
        using var hold = new ManualResetEventSlim();
        var (fs, device, _) = await Mounted((device, storage) =>
            device.AddFile(storage, MtpDevice.Root, "video.mp4", new byte[64 * 1024]));
        device.HoldDownloads = hold;

        await using (var stream = await fs.OpenReadAsync(InStorage("video.mp4"), None))
        {
            var first = new byte[16];
            await stream.ReadExactlyAsync(first);
        }
        hold.Set();

        var stat = fs.StatAsync(InStorage("video.mp4"), None);
        Assert.Same(stat, await Task.WhenAny(stat, Task.Delay(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task AWriteOfKnownSizeArrivesWhole()
    {
        var (fs, device, storage) = await Mounted();
        var content = new byte[50_000];
        Random.Shared.NextBytes(content);

        await Write(fs, InStorage("upload.bin"), content, content.Length);

        Assert.Equal(content, device.ContentOf(device.HandleOf(storage, MtpDevice.Root, "upload.bin")!.Value));
    }

    [Fact]
    public async Task AWriteOfUnknownSizeIsHeldUntilClosedAndThenSent()
    {
        // MTP announces a file's size before its first byte, so without one there is nothing to
        // do but wait for the last.
        var (fs, device, storage) = await Mounted();

        await Write(fs, InStorage("unknown.bin"), [1, 2, 3], length: null);

        Assert.Equal([1, 2, 3], device.ContentOf(device.HandleOf(storage, MtpDevice.Root, "unknown.bin")!.Value));
    }

    [Fact]
    public async Task AShortWriteIsNotSentUnderTheRealName()
    {
        var (fs, device, storage) = await Mounted();

        await Assert.ThrowsAnyAsync<Exception>(() => Write(fs, InStorage("short.bin"), [1, 2, 3], length: 10));

        Assert.Null(device.HandleOf(storage, MtpDevice.Root, "short.bin"));
    }

    [Fact]
    public async Task OverwritingReplacesTheOldFile()
    {
        var (fs, device, storage) = await Mounted((device, storage) => device.AddFile(storage, MtpDevice.Root, "a.txt", [9, 9]));

        await Write(fs, InStorage("a.txt"), [1], length: 1);

        Assert.Equal([1], device.ContentOf(device.HandleOf(storage, MtpDevice.Root, "a.txt")!.Value));
        Assert.Single(await List(fs, InStorage("")));
    }

    [Fact]
    public async Task CreateNewRefusesAnExistingName()
    {
        var (fs, _, _) = await Mounted((device, storage) => device.AddFile(storage, MtpDevice.Root, "a.txt", [9]));

        var error = await Assert.ThrowsAsync<FileOperationException>(() =>
            fs.OpenWriteAsync(InStorage("a.txt"), WriteMode.CreateNew, 1, None));

        Assert.Equal(FileErrorKind.AlreadyExists, error.Kind);
    }

    [Fact]
    public async Task NothingCanBeWrittenAtTheDeviceRoot()
    {
        var (fs, _, _) = await Mounted();

        var error = await Assert.ThrowsAsync<FileOperationException>(() =>
            fs.OpenWriteAsync(At("/loose.txt"), WriteMode.Overwrite, 1, None));

        Assert.Equal(FileErrorKind.AccessDenied, error.Kind);
    }

    [Fact]
    public async Task AReadOnlyStorageRefusesWrites()
    {
        var (fs, device, _) = await Mounted();
        device.AddStorage("Card", readOnly: true);

        var error = await Assert.ThrowsAsync<FileOperationException>(() =>
            fs.OpenWriteAsync(Root.Child("Card").Child("a.txt"), WriteMode.Overwrite, 1, None));

        Assert.Equal(FileErrorKind.ReadOnly, error.Kind);
    }

    // --- changing the tree --------------------------------------------------

    [Fact]
    public async Task CreatingADirectoryMakesItsMissingParents()
    {
        var (fs, _, _) = await Mounted();

        await fs.CreateDirectoryAsync(InStorage("Backup/2026/October"), None);

        Assert.True((await fs.StatAsync(InStorage("Backup/2026/October"), None)).IsDirectory);
    }

    [Fact]
    public async Task AFolderWithSomethingInItIsOnlyDeletedWhenAskedToBeRecursive()
    {
        var (fs, device, storage) = await Mounted((device, storage) =>
        {
            var folder = device.AddFolder(storage, MtpDevice.Root, "Old");
            var inner = device.AddFolder(storage, folder, "Inner");
            device.AddFile(storage, inner, "x", [1]);
        });

        var refused = await Assert.ThrowsAsync<FileOperationException>(() =>
            fs.DeleteAsync(InStorage("Old"), recursive: false, None));
        Assert.Equal(FileErrorKind.NotEmpty, refused.Kind);

        await fs.DeleteAsync(InStorage("Old"), recursive: true, None);

        Assert.False(await fs.ExistsAsync(InStorage("Old"), None));
        Assert.Null(device.HandleOf(storage, MtpDevice.Root, "Old"));
    }

    [Fact]
    public async Task RenamingInPlaceKeepsTheSameObject()
    {
        uint handle = 0;
        var (fs, device, _) = await Mounted((device, storage) =>
            handle = device.AddFile(storage, MtpDevice.Root, "before.txt", [1]));

        await fs.RenameAsync(InStorage("before.txt"), InStorage("after.txt"), None);

        Assert.False(await fs.ExistsAsync(InStorage("before.txt"), None));
        Assert.True(await fs.ExistsAsync(InStorage("after.txt"), None));
        Assert.True(device.Exists(handle));
    }

    [Fact]
    public async Task RenamingOntoAnExistingNameIsRefused()
    {
        var (fs, _, _) = await Mounted((device, storage) =>
        {
            device.AddFile(storage, MtpDevice.Root, "a", [1]);
            device.AddFile(storage, MtpDevice.Root, "b", [2]);
        });

        var error = await Assert.ThrowsAsync<FileOperationException>(() =>
            fs.RenameAsync(InStorage("a"), InStorage("b"), None));

        Assert.Equal(FileErrorKind.AlreadyExists, error.Kind);
    }

    // --- through the operations engine -------------------------------------

    private sealed class OneProvider(IFileSystem fs) : IFileSystemProvider
    {
        public Task<IFileSystem> GetAsync(Location location, CancellationToken cancellationToken) =>
            Task.FromResult(fs);
    }

    private static Task<OperationResult> Run(IFileSystem fs, FileOperation operation) =>
        new OperationJob(new OneProvider(fs), retries: FixedRetryPolicy.SkipAll).RunAsync(operation, null, None);

    [Fact]
    public async Task CopyingWithinThePhoneFinishesRatherThanWaitingOnItself()
    {
        // The copy opens a read and then a write on the same device. A write that streamed
        // would wait for the device while the read holds it, and the read would wait for the
        // write to drain it -- forever. It has to be spooled instead.
        var content = new byte[100_000];
        Random.Shared.NextBytes(content);
        var (fs, device, storage) = await Mounted((device, storage) =>
        {
            device.AddFile(storage, MtpDevice.Root, "clip.mp4", content);
            device.AddFolder(storage, MtpDevice.Root, "Backup");
        });

        var copy = Run(fs, FileOperation.Copy([InStorage("clip.mp4")], InStorage("Backup")));
        Assert.Same(copy, await Task.WhenAny(copy, Task.Delay(TimeSpan.FromSeconds(10))));

        Assert.True((await copy).Succeeded);
        var backup = device.HandleOf(storage, MtpDevice.Root, "Backup")!.Value;
        Assert.Equal(content, device.ContentOf(device.HandleOf(storage, backup, "clip.mp4")!.Value));
        Assert.Null(device.HandleOf(storage, backup, "clip.mp4" + OperationJob.PartSuffix));
    }

    [Fact]
    public async Task MovingWithinThePhoneMovesTheObjectRatherThanCopyingIt()
    {
        uint handle = 0;
        var (fs, device, storage) = await Mounted((device, storage) =>
        {
            handle = device.AddFile(storage, MtpDevice.Root, "photo.jpg", [1, 2, 3]);
            device.AddFolder(storage, MtpDevice.Root, "Pictures");
        });

        var result = await Run(fs, FileOperation.Move([InStorage("photo.jpg")], InStorage("Pictures")));

        Assert.True(result.Succeeded);
        Assert.Equal(0, device.Uploads);
        var pictures = device.HandleOf(storage, MtpDevice.Root, "Pictures")!.Value;
        Assert.Equal(handle, device.HandleOf(storage, pictures, "photo.jpg"));
        Assert.False(await fs.ExistsAsync(InStorage("photo.jpg"), None));
    }

    // --- the device and its thread ------------------------------------------

    [Fact]
    public async Task ManyCallersStillReachTheDeviceOnOneThread()
    {
        // FakeMtpDevice throws on a call from a second thread, so this fails rather than hangs
        // if anything bypasses the device thread.
        var (fs, _, _) = await Mounted((device, storage) =>
        {
            for (var i = 0; i < 20; i++)
                device.AddFile(storage, MtpDevice.Root, $"f{i}", [(byte)i]);
        });

        var reads = Enumerable.Range(0, 20).Select(i => Read(fs, InStorage($"f{i}")));
        var results = await Task.WhenAll(reads);

        Assert.Equal(Enumerable.Range(0, 20).Select(i => (byte)i), results.Select(r => r.Single()));
    }

    [Fact]
    public async Task AnUnpluggedDeviceIsAConnectionLost()
    {
        var (fs, device, _) = await Mounted();
        device.Unplugged = true;

        var error = await Assert.ThrowsAsync<FileOperationException>(() => List(fs, Root));

        Assert.Equal(FileErrorKind.ConnectionLost, error.Kind);
    }

    [Fact]
    public async Task DisposingReleasesTheDevice()
    {
        var (fs, device, _) = await Mounted();

        await fs.DisposeAsync();

        Assert.True(device.Disposed);
    }

    [Theory]
    [InlineData(MtpConst.ErrorNoDeviceAttached, FileErrorKind.ConnectionLost)]
    [InlineData(MtpConst.ErrorUsbLayer, FileErrorKind.ConnectionLost)]
    [InlineData(MtpConst.ErrorStorageFull, FileErrorKind.NoSpace)]
    [InlineData(MtpConst.ErrorCancelled, FileErrorKind.Interrupted)]
    public void LibMtpErrorsAreClassified(int number, FileErrorKind kind) =>
        Assert.Equal(kind, LibMtpDevice.Classify(number));

    [Fact]
    public void AGeneralLibMtpErrorIsLeftToTheCaller() =>
        Assert.Null(LibMtpDevice.Classify(MtpConst.ErrorGeneral));

    [Theory]
    [InlineData("37181FDHS001UR", "37181fdhs001ur")]
    [InlineData("Ab:Cd  ef!", "ab-cd-ef")]
    [InlineData("", "usb-7-9")]
    [InlineData(null, "usb-7-9")]
    [InlineData("---", "usb-7-9")]
    public void ADeviceIdIsAUsableHost(string? serial, string expected)
    {
        var id = MtpFileSystemFactory.DeviceId(serial, 7, 9);

        Assert.Equal(expected, id);
        Assert.Equal(id, MtpFileSystemFactory.RootOf(id).Host);
    }

    [Fact]
    public void TheNativeStructsMatchLibmtpsLayout()
    {
        // Measured from <libmtp.h> 1.1.23 on x86-64 with a C program; a mismatch here reads
        // garbage out of every listing rather than failing anywhere useful.
        Assert.Equal(40, Marshal.SizeOf<MtpRawDevice>());
        Assert.Equal(32, (int)Marshal.OffsetOf<MtpRawDevice>(nameof(MtpRawDevice.BusLocation)));
        Assert.Equal(36, (int)Marshal.OffsetOf<MtpRawDevice>(nameof(MtpRawDevice.DevNum)));
        Assert.Equal(56, Marshal.SizeOf<MtpFile>());
        Assert.Equal(16, (int)Marshal.OffsetOf<MtpFile>(nameof(MtpFile.Filename)));
        Assert.Equal(32, (int)Marshal.OffsetOf<MtpFile>(nameof(MtpFile.ModificationDate)));
        Assert.Equal(40, (int)Marshal.OffsetOf<MtpFile>(nameof(MtpFile.Filetype)));
        Assert.Equal(72, Marshal.SizeOf<MtpStorage>());
        Assert.Equal(40, (int)Marshal.OffsetOf<MtpStorage>(nameof(MtpStorage.StorageDescription)));
        Assert.Equal(24, (int)Marshal.OffsetOf<MtpDeviceHead>(nameof(MtpDeviceHead.Storage)));
        Assert.Equal(8, (int)Marshal.OffsetOf<MtpError>(nameof(MtpError.ErrorText)));
    }
}

internal static class LocationPaths
{
    /// <summary>A relative path below a location, for building fixtures.</summary>
    public static Location Combine(this Location location, string relative) =>
        relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Aggregate(location, (at, name) => at.Child(name));
}
