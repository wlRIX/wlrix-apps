using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace Wlrix.Files.Core.Mtp;

/// <summary>
/// A phone, or anything else that speaks MTP, as a filesystem.
/// </summary>
/// <remarks>
/// Laid out as <c>mtp://device/Storage name/path</c>. The device root lists the storages as
/// folders, even when there is only one, so the path bar always says which one you are in and
/// a path stays meaningful when an SD card comes and goes.
///
/// <para>
/// <b>MTP has no paths.</b> Every object is a numeric handle, and the only way from a name to
/// a handle is to ask about each handle in the folder. Asking is a request per object --
/// fifteen thousand files in a phone's Download folder is minutes of them -- so two things
/// are kept: each folder's list of handles, which is cheap to fetch, and what each handle
/// turned out to be, which is not. A path is resolved by walking those, describing only as
/// many handles as it takes to find the name. A folder's handles are fetched afresh
/// whenever it is enumerated, which is how changes made on the phone are picked up (the
/// pane polls, and polling enumerates), and only handles not seen before cost a request.
/// </para>
///
/// <para>
/// <b>One thing at a time.</b> Every device call is a work item on <see cref="MtpDeviceThread"/>,
/// including the transfers, which hold the device until their last byte. Two consequences
/// shape the streams below. A read does not start until it is first read from, so the copy
/// that opened it can still open its target. And a write opened while a read is open on the
/// same device cannot stream — the device is about to be busy with the read — so it is
/// spooled to a temporary file and sent when it is closed.
/// </para>
///
/// <para>
/// Not <see cref="RemoteFileSystem"/>, whose pool of sessions with one leased to each open
/// stream is the opposite of what a device that can only be opened once needs.
/// </para>
/// </remarks>
public sealed class MtpFileSystem : IFileSystem
{
    private const string LockedMessage =
        "The device is not sharing its files. Unlock it, and if it asks, allow access to its data.";

    /// <summary>How far a download may run ahead of whoever is reading it.</summary>
    private const long PipeHigh = 1024 * 1024;
    private const long PipeLow = 512 * 1024;

    private readonly MtpDeviceThread _thread;
    private readonly IMtpDevice _device;

    /// <summary>How many objects are described per trip to the device thread while listing.</summary>
    /// <remarks>
    /// Small enough that the first rows appear almost at once and that anything else waiting
    /// for the device -- a copy, another window -- gets a turn between batches; large enough
    /// that the queue is not most of the cost.
    /// </remarks>
    private const int ListBatch = 64;

    /// <summary>How old a folder's remembered handles may be before a miss in them is checked again.</summary>
    /// <remarks>
    /// A copy asks whether its target exists several times over a few seconds, and in a folder of
    /// fifteen thousand files each fresh look costs seconds of its own. Changes made from here
    /// are applied to the remembered handles directly, so the only thing this delays noticing is
    /// a file appearing on the phone itself in the last few seconds.
    /// </remarks>
    private const long RecheckAfterMs = 5000;

    // Touched only on the device thread, which is what makes them safe without a lock.
    private readonly Dictionary<(uint Storage, uint Parent), (IReadOnlyList<uint> Handles, long FetchedAt)> _handles = [];
    private readonly Dictionary<uint, MtpObject> _objects = [];
    private IReadOnlyList<(string Name, MtpStorageInfo Info)>? _storages;

    private int _openReads;
    private volatile bool _disposed;

    private MtpFileSystem(string mountKey, MtpDeviceThread thread, IMtpDevice device)
    {
        MountKey = mountKey;
        _thread = thread;
        _device = device;
    }

    /// <summary>Opens a device on a thread of its own and wraps it.</summary>
    /// <param name="open">Opens the device. Called once, on the device's thread.</param>
    public static async Task<MtpFileSystem> OpenAsync(
        Location location, Func<IMtpDevice> open, CancellationToken cancellationToken)
    {
        var thread = new MtpDeviceThread($"mtp {location.Host}");
        try
        {
            var device = await thread.RunAsync(open, cancellationToken).ConfigureAwait(false);
            return new MtpFileSystem(location.MountKey, thread, device);
        }
        catch (MtpException ex)
        {
            thread.Dispose();
            throw new FileOperationException(ex.Kind, location, ex.Message, ex);
        }
        catch
        {
            thread.Dispose();
            throw;
        }
    }

    public string MountKey { get; }

    /// <summary>What the device calls itself.</summary>
    public string? FriendlyName => _device.FriendlyName;

    /// <summary>
    /// Rename, and nothing it cannot keep. No modification times: MTP has a property for one
    /// and Android ignores it, so promising it would make every copy look newer than its source.
    /// </summary>
    public FileSystemCapabilities Capabilities => FileSystemCapabilities.Rename | FileSystemCapabilities.ReportsFreeSpace;

    // --- reading the tree ---------------------------------------------------

    public Task<FileStat> StatAsync(Location location, CancellationToken cancellationToken) =>
        OnDevice(location, () => Resolve(location) switch
        {
            Node.Object { Item: var item } => new FileStat
            {
                Location = location,
                Kind = item.IsFolder ? FileKind.Directory : FileKind.File,
                Size = item.Size,
                Modified = item.Modified
            },
            _ => new FileStat { Location = location, Kind = FileKind.Directory }
        }, cancellationToken);

    public async Task<bool> ExistsAsync(Location location, CancellationToken cancellationToken)
    {
        try
        {
            await StatAsync(location, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (FileOperationException ex) when (ex.Kind == FileErrorKind.NotFound)
        {
            return false;
        }
    }

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(
        Location location, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The handles in one trip, then their details a batch per trip: a large folder shows its
        // first rows long before its last, and never holds the device for the whole listing.
        var (storages, handles) = await OnDevice(location, () => Listing(location), cancellationToken).ConfigureAwait(false);
        foreach (var storage in storages)
            yield return new FileEntry { Location = location.Child(storage), Name = storage, Kind = FileKind.Directory };

        for (var start = 0; start < handles.Count; start += ListBatch)
        {
            var batch = handles.Skip(start).Take(ListBatch).ToList();
            var items = await OnDevice(location, () => batch.Select(Describe).OfType<MtpObject>().ToList(), cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in items)
                yield return Entry(location, item);
        }
    }

    /// <summary>What a listing will hold: storage names at the device root, otherwise a folder's handles.</summary>
    private (IReadOnlyList<string> Storages, IReadOnlyList<uint> Handles) Listing(Location location) =>
        Resolve(location) switch
        {
            Node.DeviceRoot => ([.. Storages(fresh: true).Select(storage => storage.Name)], []),
            Node.Storage { Info: var storage } => ([], Handles(storage.Id, MtpDevice.Root, fresh: true)),
            Node.Object { Item: { IsFolder: true } folder } => ([], Handles(folder.Storage, folder.Handle, fresh: true)),
            _ => throw new FileOperationException(FileErrorKind.NotADirectory, location)
        };

    private static FileEntry Entry(Location parent, MtpObject item) => new()
    {
        Location = parent.Child(item.Name),
        Name = item.Name,
        Kind = item.IsFolder ? FileKind.Directory : FileKind.File,
        Size = item.Size,
        Modified = item.Modified,
        IsHidden = item.Name.StartsWith('.')
    };

    public Task<FreeSpace?> GetFreeSpaceAsync(Location location, CancellationToken cancellationToken) =>
        OnDevice<FreeSpace?>(location, () =>
        {
            IReadOnlyList<(string Name, MtpStorageInfo Info)> storages;
            try
            {
                storages = Storages(fresh: true);
            }
            catch (FileOperationException ex) when (ex.Kind == FileErrorKind.AccessDenied)
            {
                // Locked: there is an answer, it is just not ours to have yet.
                return null;
            }

            // At the device root there is no one storage you are "on", so the answer is all of
            // them -- which is also what the rail shows for the phone as a whole.
            var on = location.IsRoot
                ? storages
                : storages.Where(storage => storage.Name == Segments(location)[0]).ToList();
            return on.Count == 0
                ? null
                : new FreeSpace(on.Sum(storage => storage.Info.CapacityBytes), on.Sum(storage => storage.Info.FreeBytes));
        }, cancellationToken);

    // --- streams ------------------------------------------------------------

    public async Task<Stream> OpenReadAsync(Location location, CancellationToken cancellationToken)
    {
        var item = await OnDevice(location, () => Resolve(location) switch
        {
            Node.Object { Item: { IsFolder: false } file } => file,
            _ => throw new FileOperationException(FileErrorKind.IsADirectory, location)
        }, cancellationToken).ConfigureAwait(false);

        Interlocked.Increment(ref _openReads);
        return new ReadStream(this, location, item, cancellationToken);
    }

    public async Task<Stream> OpenWriteAsync(
        Location location, WriteMode mode, long? length, CancellationToken cancellationToken)
    {
        if (mode == WriteMode.Append)
            throw new FileOperationException(FileErrorKind.Unsupported, location, "MTP cannot append to a file");

        // Checked now, so a write that is going to be refused is refused before any bytes are
        // read for it, rather than when the stream is closed and they have all been spooled.
        await OnDevice(location, () =>
        {
            var (_, _, existing) = Target(location);
            if (existing is not null && mode == WriteMode.CreateNew)
                throw new FileOperationException(FileErrorKind.AlreadyExists, location);
            if (existing is { IsFolder: true })
                throw new FileOperationException(FileErrorKind.IsADirectory, location);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        if (length is { } known && Volatile.Read(ref _openReads) == 0)
            return new StreamingWriteStream(this, location, known, cancellationToken);
        return new SpooledWriteStream(this, location, length, cancellationToken);
    }

    /// <summary>Sends a whole file, replacing whatever already has its name. On the device thread.</summary>
    private void Upload(Location location, Stream source, long length, Func<bool> cancel)
    {
        var (storage, parent, existing) = Target(location);
        // MTP has no overwrite: the old object has to go first. Done here, at the last moment,
        // so a write that never gets this far leaves the original alone.
        if (existing is not null)
            Remove(existing);
        try
        {
            Added(storage, parent, _device.Upload(source, length, storage, parent, location.Name, cancel));
        }
        catch
        {
            // A failed upload may or may not have left an object behind, depending on how far it
            // got; only the device knows, so the next look asks it.
            Forget(storage, parent);
            throw;
        }
    }

    /// <summary>Where a new file would go: its storage, its parent's handle, and what is there now.</summary>
    private (uint Storage, uint Parent, MtpObject? Existing) Target(Location location)
    {
        if (location.Parent is not { } parentLocation)
            throw new FileOperationException(FileErrorKind.InvalidName, location);

        var (storage, parent) = Folder(parentLocation);
        if (storage.IsReadOnly)
            throw new FileOperationException(FileErrorKind.ReadOnly, location);
        return (storage.Id, parent, Find(storage.Id, parent, location.Name));
    }

    // --- changing the tree --------------------------------------------------

    public Task CreateDirectoryAsync(Location location, CancellationToken cancellationToken) =>
        OnDevice(location, () =>
        {
            var segments = Segments(location);
            if (segments.Length < 2)
            {
                // The device root and the storages are the device's own, and exist or do not.
                Resolve(location);
                return true;
            }

            var storage = StorageNamed(location, segments[0]);
            if (storage.IsReadOnly)
                throw new FileOperationException(FileErrorKind.ReadOnly, location);

            var parent = MtpDevice.Root;
            foreach (var name in segments.Skip(1))
            {
                if (Find(storage.Id, parent, name) is { } existing)
                {
                    if (!existing.IsFolder)
                        throw new FileOperationException(FileErrorKind.NotADirectory, location);
                    parent = existing.Handle;
                    continue;
                }

                var created = _device.CreateFolder(storage.Id, parent, name);
                Added(storage.Id, parent, created);
                parent = created;
            }
            return true;
        }, cancellationToken);

    public Task DeleteAsync(Location location, bool recursive, CancellationToken cancellationToken) =>
        OnDevice(location, () =>
        {
            if (Resolve(location) is not Node.Object { Item: var item })
                throw new FileOperationException(FileErrorKind.AccessDenied, location, "a storage cannot be deleted");

            if (item.IsFolder)
            {
                var children = Handles(item.Storage, item.Handle, fresh: true);
                if (children.Count > 0 && !recursive)
                    throw new FileOperationException(FileErrorKind.NotEmpty, location);
                // Emptied from here rather than trusting the device to: some delete a folder's
                // contents along with it and some refuse, and a device that refused halfway would
                // leave no way to tell what had gone.
                foreach (var child in children)
                {
                    if (Describe(child) is { } described)
                        DeleteTree(described, cancellationToken);
                }
            }

            Remove(item);
            return true;
        }, cancellationToken);

    private void DeleteTree(MtpObject item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (item.IsFolder)
        {
            foreach (var child in Handles(item.Storage, item.Handle, fresh: true))
            {
                if (Describe(child) is { } described)
                    DeleteTree(described, cancellationToken);
            }
        }
        Remove(item);
    }

    /// <summary>Deletes one object and everything remembered about it.</summary>
    private void Remove(MtpObject item)
    {
        _device.Delete(item.Handle);
        _objects.Remove(item.Handle);
        _handles.Remove((item.Storage, item.Handle));
        Removed(item.Storage, item.Parent, item.Handle);
    }

    public Task RenameAsync(Location from, Location to, CancellationToken cancellationToken) =>
        OnDevice(from, () =>
        {
            if (Resolve(from) is not Node.Object { Item: var item })
                throw new FileOperationException(FileErrorKind.AccessDenied, from, "a storage cannot be renamed");

            var (storage, parent, existing) = Target(to);
            if (existing is not null)
                throw new FileOperationException(FileErrorKind.AlreadyExists, to);

            // What the handle is changes either way, so what was remembered about it goes.
            _objects.Remove(item.Handle);
            if (storage != item.Storage || parent != item.Parent)
            {
                _device.Move(item.Handle, storage, parent);
                Removed(item.Storage, item.Parent, item.Handle);
                Added(storage, parent, item.Handle);
            }
            if (to.Name != item.Name)
                _device.Rename(item.Handle, to.Name);
            return true;
        }, cancellationToken);

    public Task CreateSymlinkAsync(Location link, string target, CancellationToken cancellationToken) =>
        throw new FileOperationException(FileErrorKind.Unsupported, link);

    public Task SetModifiedAsync(Location location, DateTimeOffset modified, CancellationToken cancellationToken) =>
        throw new FileOperationException(FileErrorKind.Unsupported, location);

    public Task SetUnixModeAsync(Location location, int mode, CancellationToken cancellationToken) =>
        throw new FileOperationException(FileErrorKind.Unsupported, location);

    // --- paths to handles ---------------------------------------------------

    /// <summary>What a location names: the device, a storage, or an object on one.</summary>
    private abstract record Node
    {
        public sealed record DeviceRoot : Node;
        public sealed record Storage(MtpStorageInfo Info) : Node;
        public sealed record Object(MtpObject Item) : Node;
    }

    private static string[] Segments(Location location) =>
        location.IsRoot ? [] : location.Path[1..].Split('/');

    private Node Resolve(Location location)
    {
        var segments = Segments(location);
        if (segments.Length == 0)
            return new Node.DeviceRoot();

        var storage = StorageNamed(location, segments[0]);
        if (segments.Length == 1)
            return new Node.Storage(storage);

        var parent = MtpDevice.Root;
        MtpObject? item = null;
        foreach (var name in segments.Skip(1))
        {
            if (item is { IsFolder: false })
                throw new FileOperationException(FileErrorKind.NotADirectory, location);
            item = Find(storage.Id, parent, name)
                ?? throw new FileOperationException(FileErrorKind.NotFound, location);
            parent = item.Handle;
        }
        return new Node.Object(item!);
    }

    /// <summary>The storage and handle of a folder new things can go in.</summary>
    private (MtpStorageInfo Storage, uint Handle) Folder(Location location) => Resolve(location) switch
    {
        Node.Storage { Info: var storage } => (storage, MtpDevice.Root),
        Node.Object { Item: { IsFolder: true } folder } =>
            (Storages(fresh: false).First(storage => storage.Info.Id == folder.Storage).Info, folder.Handle),
        Node.DeviceRoot => throw new FileOperationException(FileErrorKind.AccessDenied, location,
            "files go inside one of the device's storages"),
        _ => throw new FileOperationException(FileErrorKind.NotADirectory, location)
    };

    /// <summary>A child by name, or null.</summary>
    /// <remarks>
    /// Handles already described are searched first, and then only as many of the rest as it
    /// takes: opening one file in a folder of thousands should not cost a request for each of
    /// its neighbors. If the remembered handles do not have it, they are fetched once more,
    /// which is what lets a file made on the phone a moment ago be reached by a path typed into
    /// the path bar before any poll has noticed it.
    /// </remarks>
    private MtpObject? Find(uint storage, uint parent, string name)
    {
        var stale = _handles.TryGetValue((storage, parent), out var known)
                    && Environment.TickCount64 - known.FetchedAt > RecheckAfterMs;
        if (Search(Handles(storage, parent, fresh: false), name) is { } found)
            return found;
        return stale ? Search(Handles(storage, parent, fresh: true), name) : null;
    }

    private MtpObject? Search(IReadOnlyList<uint> handles, string name)
    {
        foreach (var handle in handles)
        {
            if (_objects.TryGetValue(handle, out var known) && known.Name == name)
                return known;
        }
        foreach (var handle in handles)
        {
            if (!_objects.ContainsKey(handle) && Describe(handle) is { } item && item.Name == name)
                return item;
        }
        return null;
    }

    /// <summary>A folder's handles, remembered or fetched.</summary>
    private IReadOnlyList<uint> Handles(uint storage, uint parent, bool fresh)
    {
        if (!fresh && _handles.TryGetValue((storage, parent), out var known))
            return known.Handles;

        var handles = _device.ChildHandles(storage, parent);
        // Whatever was here and is not any more has been deleted or moved, on the phone or by us,
        // and what was remembered about it would only ever be wrong.
        if (_handles.TryGetValue((storage, parent), out var previous))
        {
            var now = handles.ToHashSet();
            foreach (var gone in previous.Handles.Where(handle => !now.Contains(handle)))
                _objects.Remove(gone);
        }
        _handles[(storage, parent)] = (handles, Environment.TickCount64);
        return handles;
    }

    /// <summary>What a handle is, remembered or asked for. Null if it no longer exists.</summary>
    private MtpObject? Describe(uint handle)
    {
        if (_objects.TryGetValue(handle, out var known))
            return known;
        if (_device.Metadata(handle) is not { } item)
            return null;
        _objects[handle] = item;
        return item;
    }

    /// <summary>Drops one folder's handles, when what is in it can no longer be known from here.</summary>
    private void Forget(uint storage, uint parent) => _handles.Remove((storage, parent));

    /// <summary>Records something made here, so the folder need not be fetched again to find it.</summary>
    private void Added(uint storage, uint parent, uint handle)
    {
        if (_handles.TryGetValue((storage, parent), out var known))
            _handles[(storage, parent)] = ([.. known.Handles, handle], known.FetchedAt);
    }

    /// <summary>Records something removed from here.</summary>
    private void Removed(uint storage, uint parent, uint handle)
    {
        if (_handles.TryGetValue((storage, parent), out var known))
            _handles[(storage, parent)] = ([.. known.Handles.Where(other => other != handle)], known.FetchedAt);
    }

    private MtpStorageInfo StorageNamed(Location location, string name)
    {
        var storages = Storages(fresh: false);
        if (storages.FirstOrDefault(storage => storage.Name == name) is { Info: { } found })
            return found;

        // Not where it was: an SD card put in, or a phone unlocked since the last look.
        storages = Storages(fresh: true);
        if (storages.FirstOrDefault(storage => storage.Name == name) is { Info: { } again })
            return again;

        throw new FileOperationException(FileErrorKind.NotFound, location);
    }

    private IReadOnlyList<(string Name, MtpStorageInfo Info)> Storages(bool fresh)
    {
        if (!fresh && _storages is { Count: > 0 } known)
            return known;

        var storages = _device.ReadStorages();
        // The root of the device is the one place that cannot be empty without explanation:
        // an Android phone hides every storage until it is unlocked, and an empty folder would
        // look like a phone with nothing on it.
        if (storages.Count == 0)
        {
            _storages = [];
            throw new FileOperationException(FileErrorKind.AccessDenied, Location.Parse(MountKey + "/"), LockedMessage);
        }

        _storages = NameStorages(storages);
        return _storages;
    }

    /// <summary>Gives each storage a folder name, numbering any that would collide.</summary>
    internal static IReadOnlyList<(string Name, MtpStorageInfo Info)> NameStorages(IReadOnlyList<MtpStorageInfo> storages)
    {
        var named = new List<(string, MtpStorageInfo)>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var storage in storages)
        {
            // A slash is the one character a folder name cannot carry, and nothing stops a
            // device from putting one in a description.
            var name = storage.Description.Replace('/', '-').Trim();
            if (name.Length == 0)
                name = $"Storage {storage.Id:x8}";

            var candidate = name;
            for (var n = 2; !taken.Add(candidate); n++)
                candidate = $"{name} ({n})";
            named.Add((candidate, storage));
        }
        return named;
    }

    // --- the device thread --------------------------------------------------

    /// <summary>Runs work on the device thread, giving any device failure the location it was about.</summary>
    private async Task<T> OnDevice<T>(Location location, Func<T> work, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return await _thread.RunAsync(work, cancellationToken).ConfigureAwait(false);
        }
        catch (MtpException ex)
        {
            throw new FileOperationException(ex.Kind, location, ex.Message, ex);
        }
    }

    private Task OnDevice(Location location, Func<bool> work, CancellationToken cancellationToken) =>
        OnDevice<bool>(location, work, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Released on its own thread like every other call, after anything already queued.
        try
        {
            await _thread.RunAsync(_device.Dispose, CancellationToken.None).ConfigureAwait(false);
        }
        catch (MtpException)
        {
            // Already gone, which is the usual reason to be disposing.
        }
        _thread.Dispose();
    }

    // --- stream types -------------------------------------------------------

    private static Pipe NewPipe() => new(new PipeOptions(
        pauseWriterThreshold: PipeHigh, resumeWriterThreshold: PipeLow, useSynchronizationContext: false));

    /// <summary>
    /// A download, streamed: bytes are handed over as the device sends them, so a copy's
    /// progress moves with the transfer rather than jumping at the end.
    /// </summary>
    private sealed class ReadStream(MtpFileSystem owner, Location location, MtpObject item, CancellationToken token)
        : Stream
    {
        private readonly Pipe _pipe = NewPipe();
        private Stream? _reader;
        private volatile bool _closed;

        /// <summary>Starts the transfer on first use. See the class remarks for why not sooner.</summary>
        private Stream Reader
        {
            get
            {
                if (_reader is not null)
                    return _reader;

                _reader = _pipe.Reader.AsStream();
                var sink = _pipe.Writer.AsStream();
                _ = owner._thread.RunAsync(() =>
                {
                    try
                    {
                        owner._device.Download(item.Handle, sink, () => _closed || owner._disposed || token.IsCancellationRequested);
                        _pipe.Writer.Complete();
                    }
                    catch (MtpException ex)
                    {
                        _pipe.Writer.Complete(new FileOperationException(ex.Kind, location, ex.Message, ex));
                    }
                    catch (OperationCanceledException ex)
                    {
                        _pipe.Writer.Complete(ex);
                    }
                    return true;
                }, CancellationToken.None);
                return _reader;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Reader.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Reader.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Reader.ReadAsync(buffer, offset, count, cancellationToken);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => item.Size;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                // Closing early makes the rest of the transfer be dropped as it arrives -- a phone
                // cannot be stopped mid-file without losing its session, see LibMtpDevice -- and
                // completing the reader releases a download blocked on a full pipe.
                _closed = true;
                _pipe.Reader.Complete();
                Interlocked.Decrement(ref owner._openReads);
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// An upload, streamed: the device receives the bytes as they are written. Only used when
    /// the size is known and nothing else is about to need the device.
    /// </summary>
    private sealed class StreamingWriteStream : Stream
    {
        private readonly Pipe _pipe = NewPipe();
        private readonly Stream _writer;
        private readonly Task _upload;
        private readonly Location _location;
        private bool _closed;

        public StreamingWriteStream(MtpFileSystem owner, Location location, long length, CancellationToken token)
        {
            _location = location;
            _writer = _pipe.Writer.AsStream();
            var source = _pipe.Reader.AsStream();
            _upload = owner._thread.RunAsync(() =>
            {
                try
                {
                    owner.Upload(location, source, length, () => owner._disposed || token.IsCancellationRequested);
                }
                finally
                {
                    // Whatever happened, the writer must not be left blocked on a full pipe.
                    _pipe.Reader.Complete();
                }
                return true;
            }, CancellationToken.None);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfFailed();
            _writer.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfFailed();
            await _writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <summary>Surfaces a failed upload at the next write rather than after all the bytes.</summary>
        private void ThrowIfFailed()
        {
            if (_upload.IsFaulted)
                Rethrow();
        }

        private void Rethrow()
        {
            var error = _upload.Exception!.InnerException!;
            throw error is MtpException mtp
                ? new FileOperationException(mtp.Kind, _location, mtp.Message, mtp)
                : error;
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_closed)
            {
                _closed = true;
                await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
                try
                {
                    await _upload.ConfigureAwait(false);
                }
                catch (MtpException ex)
                {
                    throw new FileOperationException(ex.Kind, _location, ex.Message, ex);
                }
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
                DisposeAsync().AsTask().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// An upload written to a temporary file first and sent when closed. For a size that is not
    /// known up front, and for a write that would otherwise wait on a read from the same device.
    /// </summary>
    private sealed class SpooledWriteStream : Stream
    {
        private readonly MtpFileSystem _owner;
        private readonly Location _location;
        private readonly long? _length;
        private readonly CancellationToken _token;
        private readonly FileStream _spool;
        private bool _closed;

        public SpooledWriteStream(MtpFileSystem owner, Location location, long? length, CancellationToken token)
        {
            _owner = owner;
            _location = location;
            _length = length;
            _token = token;
            _spool = new FileStream(
                System.IO.Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        }

        public override void Write(byte[] buffer, int offset, int count) => _spool.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _spool.Write(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _spool.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _spool.WriteAsync(buffer, offset, count, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            if (!_closed)
            {
                _closed = true;
                try
                {
                    await _spool.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    var size = _spool.Length;
                    // Short of what was promised means the writer gave up partway. Sending what
                    // there is would put a truncated file on the device under the real name.
                    if (_length is { } expected && size != expected)
                        throw new FileOperationException(FileErrorKind.Interrupted, _location);

                    _spool.Position = 0;
                    await _owner.OnDevice(_location, () =>
                    {
                        _owner.Upload(_location, _spool, size, () => _owner._disposed || _token.IsCancellationRequested);
                        return true;
                    }, _token).ConfigureAwait(false);
                }
                finally
                {
                    await _spool.DisposeAsync().ConfigureAwait(false);
                }
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
                DisposeAsync().AsTask().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _spool.Length;
        public override long Position { get => _spool.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
