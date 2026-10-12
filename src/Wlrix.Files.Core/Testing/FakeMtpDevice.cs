using Wlrix.Files.Core.Mtp;

namespace Wlrix.Files.Core.Testing;

/// <summary>
/// A phone in memory, speaking the same handles-and-objects vocabulary as libmtp.
/// </summary>
/// <remarks>
/// It also polices the rule a real device cannot report breaking: every call has to come from
/// the one thread that first called it. libmtp would not throw, it would corrupt its session,
/// so this throws instead and the tests see it.
/// </remarks>
public sealed class FakeMtpDevice : IMtpDevice
{
    private readonly Lock _lock = new();
    private readonly List<MtpStorageInfo> _storages = [];
    private readonly Dictionary<uint, (MtpObject Item, byte[] Content)> _objects = [];
    private uint _next = 100;
    private int? _thread;

    public string? FriendlyName { get; init; } = "Fake Phone";

    /// <summary>True makes the device hide its storages, as a locked Android phone does.</summary>
    public bool Locked { get; set; }

    /// <summary>True makes every call fail as if the cable had been pulled.</summary>
    public bool Unplugged { get; set; }

    /// <summary>When set, a download waits on it after its first block, so a test can act mid-transfer.</summary>
    public ManualResetEventSlim? HoldDownloads { get; set; }

    /// <summary>When set, the device refuses to move objects between folders.</summary>
    public bool RefusesMove { get; set; }

    public int ChildHandleRequests { get; private set; }
    public int MetadataRequests { get; private set; }
    public int Uploads { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Adds a storage, answering its id.</summary>
    public uint AddStorage(string description, bool readOnly = false, long capacity = 64_000_000_000, long free = 32_000_000_000)
    {
        var id = 0x10001u + (uint)_storages.Count;
        _storages.Add(new MtpStorageInfo(id, description, capacity, free, readOnly));
        return id;
    }

    /// <summary>Adds a folder under <paramref name="parent"/>, answering its handle.</summary>
    public uint AddFolder(uint storage, uint parent, string name) => Add(storage, parent, name, folder: true, []);

    /// <summary>Adds a file, answering its handle.</summary>
    public uint AddFile(uint storage, uint parent, string name, byte[] content) => Add(storage, parent, name, folder: false, content);

    /// <summary>The handle of whatever is called <paramref name="name"/> in a folder, or null.</summary>
    public uint? HandleOf(uint storage, uint parent, string name)
    {
        lock (_lock)
        {
            return _objects.Values
                .Where(o => o.Item.Storage == storage && o.Item.Parent == parent && o.Item.Name == name)
                .Select(o => (uint?)o.Item.Handle)
                .FirstOrDefault();
        }
    }

    /// <summary>The contents of a file.</summary>
    public byte[] ContentOf(uint handle)
    {
        lock (_lock)
            return _objects[handle].Content;
    }

    /// <summary>Removes an object as if it had been deleted on the phone itself.</summary>
    public void DeleteOnPhone(uint handle)
    {
        lock (_lock)
            _objects.Remove(handle);
    }

    public bool Exists(uint handle)
    {
        lock (_lock)
            return _objects.ContainsKey(handle);
    }

    private uint Add(uint storage, uint parent, string name, bool folder, byte[] content)
    {
        lock (_lock)
        {
            var handle = _next++;
            _objects[handle] = (new MtpObject(handle, parent, storage, name, folder, content.Length, DateTimeOffset.UnixEpoch), content);
            return handle;
        }
    }

    public IReadOnlyList<MtpStorageInfo> ReadStorages()
    {
        Check();
        return Locked ? [] : [.. _storages];
    }

    public IReadOnlyList<uint> ChildHandles(uint storage, uint parent)
    {
        Check();
        ChildHandleRequests++;
        lock (_lock)
        {
            return [.. _objects.Values
                .Where(o => o.Item.Storage == storage && o.Item.Parent == parent)
                .Select(o => o.Item.Handle)
                .Order()];
        }
    }

    public MtpObject? Metadata(uint handle)
    {
        Check();
        MetadataRequests++;
        lock (_lock)
            return _objects.TryGetValue(handle, out var found) ? found.Item : null;
    }

    public void Download(uint handle, Stream sink, Func<bool> cancel)
    {
        Check();
        byte[] content;
        lock (_lock)
            content = _objects.TryGetValue(handle, out var found) ? found.Content : throw Missing();

        const int block = 4096;
        for (var offset = 0; offset < content.Length; offset += block)
        {
            if (cancel())
                throw new OperationCanceledException();
            sink.Write(content.AsSpan(offset, Math.Min(block, content.Length - offset)));
            if (offset == 0)
                HoldDownloads?.Wait(TimeSpan.FromSeconds(10));
        }
    }

    public uint Upload(Stream source, long length, uint storage, uint parent, string name, Func<bool> cancel)
    {
        Check();
        using var buffer = new MemoryStream();
        var block = new byte[4096];
        while (buffer.Length < length)
        {
            if (cancel())
                throw new OperationCanceledException();
            var read = source.Read(block, 0, (int)Math.Min(block.Length, length - buffer.Length));
            if (read == 0)
                throw new MtpException(FileErrorKind.Interrupted, "the file ended before its announced size");
            buffer.Write(block, 0, read);
        }

        Uploads++;
        return Add(storage, parent, name, folder: false, buffer.ToArray());
    }

    public uint CreateFolder(uint storage, uint parent, string name)
    {
        Check();
        return AddFolder(storage, parent, name);
    }

    public void Delete(uint handle)
    {
        Check();
        lock (_lock)
        {
            if (!_objects.Remove(handle))
                throw Missing();
        }
    }

    public void Rename(uint handle, string name)
    {
        Check();
        lock (_lock)
        {
            var (item, content) = _objects.TryGetValue(handle, out var found) ? found : throw Missing();
            _objects[handle] = (item with { Name = name }, content);
        }
    }

    public void Move(uint handle, uint storage, uint parent)
    {
        Check();
        if (RefusesMove)
            throw new MtpException(FileErrorKind.Unsupported, "the device would not move it");
        lock (_lock)
        {
            var (item, content) = _objects.TryGetValue(handle, out var found) ? found : throw Missing();
            _objects[handle] = (item with { Storage = storage, Parent = parent }, content);
        }
    }

    public void Dispose()
    {
        Check();
        Disposed = true;
    }

    private void Check()
    {
        var current = Environment.CurrentManagedThreadId;
        _thread ??= current;
        if (_thread != current)
            throw new InvalidOperationException("an MTP device was called from a second thread");
        if (Unplugged)
            throw new MtpException(FileErrorKind.ConnectionLost, "no device attached");
    }

    private static MtpException Missing() => new(FileErrorKind.NotFound, "no such object");
}
