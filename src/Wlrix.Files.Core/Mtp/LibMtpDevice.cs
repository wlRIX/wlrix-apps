using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Wlrix.Files.Core.Mtp;

/// <summary>A real device, through libmtp.</summary>
/// <remarks>
/// Everything here runs on the device's own thread (see <see cref="MtpDeviceThread"/>), which is
/// why nothing locks. The one exception is <see cref="Open"/>, whose detection pass touches
/// libusb's global state and so is serialized across devices.
/// </remarks>
public sealed unsafe class LibMtpDevice : IMtpDevice
{
    private static readonly Lock DetectLock = new();
    private static bool _initialized;

    private nint _device;

    private LibMtpDevice(nint device)
    {
        _device = device;
        FriendlyName = LibMtp.Taken(LibMtp.GetFriendlyname(device)) is { Length: > 0 } friendly
            ? friendly
            : LibMtp.Taken(LibMtp.GetModelname(device));
    }

    public string? FriendlyName { get; }

    /// <summary>Opens the device at a USB bus and device number, as udev reports them.</summary>
    /// <remarks>
    /// Uncached: the cached open walks every object on the device before returning, which on a
    /// phone with years of photos is minutes of nothing on screen. One folder at a time is what
    /// a file manager wants anyway.
    /// </remarks>
    public static LibMtpDevice Open(int bus, int deviceNumber)
    {
        lock (DetectLock)
        {
            if (!_initialized)
            {
                LibMtp.Init();
                _initialized = true;
            }

            var error = LibMtp.DetectRawDevices(out var raw, out var count);
            try
            {
                if (error != MtpConst.ErrorNone || raw is null)
                    throw new MtpException(FileErrorKind.NotFound, $"no MTP device found (libmtp error {error})");

                for (var i = 0; i < count; i++)
                {
                    if (raw[i].BusLocation != bus || raw[i].DevNum != deviceNumber)
                        continue;

                    var device = LibMtp.OpenRawDeviceUncached(&raw[i]);
                    if (device == 0)
                    {
                        // By far the usual reason: something else already claimed the interface.
                        throw new MtpException(FileErrorKind.AccessDenied,
                            "the device could not be opened; another program may be using it");
                    }
                    return new LibMtpDevice(device);
                }

                throw new MtpException(FileErrorKind.NotFound, $"no MTP device at bus {bus} device {deviceNumber}");
            }
            finally
            {
                if (raw is not null)
                    LibMtp.FreeMemory(raw);
            }
        }
    }

    public IReadOnlyList<MtpStorageInfo> ReadStorages()
    {
        var device = Device;
        // Asked again every time rather than kept, because an Android phone has none until it is
        // unlocked and the answer changes the moment the owner allows access.
        if (LibMtp.GetStorage(device, 0) != 0)
        {
            var failure = TakeError(FileErrorKind.Unknown, "could not read the device's storages");
            if (failure.Kind == FileErrorKind.ConnectionLost)
                throw failure;
            return [];
        }

        var storages = new List<MtpStorageInfo>();
        for (var storage = (MtpStorage*)((MtpDeviceHead*)device)->Storage; storage is not null; storage = (MtpStorage*)storage->Next)
        {
            storages.Add(new MtpStorageInfo(
                storage->Id,
                LibMtp.Borrowed(storage->StorageDescription) ?? $"Storage {storage->Id:x8}",
                (long)storage->MaxCapacity,
                (long)storage->FreeSpaceInBytes,
                storage->AccessCapability != MtpConst.AccessReadWrite));
        }
        return storages;
    }

    public IReadOnlyList<uint> ChildHandles(uint storage, uint parent)
    {
        var device = Device;
        LibMtp.ClearErrorstack(device);
        var count = LibMtp.GetChildren(device, storage, parent, out var children);
        if (count < 0)
            throw TakeError(FileErrorKind.Unknown, "could not list the folder");

        try
        {
            return count == 0 ? [] : new ReadOnlySpan<uint>(children, count).ToArray();
        }
        finally
        {
            if (children is not null)
                LibMtp.FreeMemory(children);
        }
    }

    public MtpObject? Metadata(uint handle)
    {
        var device = Device;
        LibMtp.ClearErrorstack(device);
        var file = LibMtp.GetFilemetadata(device, handle);
        if (file is null)
        {
            // Gone between being listed and being asked about, which a phone deleting things
            // on its own makes ordinary. A device that has gone is a different matter.
            var failure = TakeError(FileErrorKind.NotFound, "could not read the object");
            if (failure.Kind == FileErrorKind.ConnectionLost)
                throw failure;
            return null;
        }

        try
        {
            return ToObject(file);
        }
        finally
        {
            LibMtp.DestroyFile(file);
        }
    }

    public void Download(uint handle, Stream sink, Func<bool> cancel)
    {
        var state = new Transfer(sink, cancel);
        var pin = GCHandle.Alloc(state);
        try
        {
            var result = LibMtp.GetFileToHandler(Device, handle, &Put, GCHandle.ToIntPtr(pin), null, 0);
            Finish(result, state, "could not read the file");
        }
        finally
        {
            pin.Free();
        }
    }

    public uint Upload(Stream source, long length, uint storage, uint parent, string name, Func<bool> cancel)
    {
        var state = new Transfer(source, cancel) { Remaining = length };
        var pin = GCHandle.Alloc(state);
        // Allocated with the C allocator's own functions and freed the same way, rather than
        // with LIBMTP_destroy_file_t: the name is ours, and handing libmtp a managed buffer to
        // free would be a crash waiting for the first upload.
        var file = (MtpFile*)NativeMemory.AllocZeroed((nuint)sizeof(MtpFile));
        var utf8 = Utf8(name);
        try
        {
            file->ParentId = parent;
            file->StorageId = storage;
            file->Filename = (nint)utf8;
            file->Filesize = (ulong)length;
            file->ModificationDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Unknown lets the phone decide what the file is from its name, which is what an
            // Android phone does anyway and what keeps it from refusing a type it does not list.
            file->Filetype = MtpConst.FiletypeUnknown;

            var result = LibMtp.SendFileFromHandler(Device, &Get, GCHandle.ToIntPtr(pin), file, null, 0);
            if ((state.Canceled || state.Failure is not null) && result == 0 && file->ItemId != 0)
            {
                // Sent in full, padded out after the source gave up. It is not the file anyone
                // wanted, so it goes before anyone sees it.
                LibMtp.DeleteObject(Device, file->ItemId);
            }
            Finish(result, state, "could not write the file");
            return file->ItemId;
        }
        finally
        {
            NativeMemory.Free(utf8);
            NativeMemory.Free(file);
            pin.Free();
        }
    }

    public uint CreateFolder(uint storage, uint parent, string name)
    {
        var device = Device;
        LibMtp.ClearErrorstack(device);
        var utf8 = Utf8(name);
        try
        {
            var handle = LibMtp.CreateFolder(device, utf8, parent, storage);
            if (handle == 0)
                throw TakeError(FileErrorKind.Unknown, "could not create the folder");
            return handle;
        }
        finally
        {
            NativeMemory.Free(utf8);
        }
    }

    public void Delete(uint handle)
    {
        var device = Device;
        LibMtp.ClearErrorstack(device);
        if (LibMtp.DeleteObject(device, handle) != 0)
            throw TakeError(FileErrorKind.Unknown, "could not delete");
    }

    public void Rename(uint handle, string name)
    {
        var device = Device;
        LibMtp.ClearErrorstack(device);
        var utf8 = Utf8(name);
        try
        {
            if (LibMtp.SetObjectFilename(device, handle, utf8) != 0)
                throw TakeError(FileErrorKind.Unknown, "could not rename");
        }
        finally
        {
            NativeMemory.Free(utf8);
        }
    }

    public void Move(uint handle, uint storage, uint parent)
    {
        var device = Device;
        LibMtp.ClearErrorstack(device);
        if (LibMtp.MoveObject(device, handle, storage, parent) != 0)
            throw TakeError(FileErrorKind.Unsupported, "the device would not move it");
    }

    public void Dispose()
    {
        if (_device == 0)
            return;
        LibMtp.ReleaseDevice(_device);
        _device = 0;
    }

    private nint Device => _device != 0
        ? _device
        : throw new MtpException(FileErrorKind.ConnectionLost, "the device has been released");

    private static MtpObject ToObject(MtpFile* file) => new(
        file->ItemId,
        file->ParentId,
        file->StorageId,
        LibMtp.Borrowed(file->Filename) ?? string.Empty,
        file->Filetype == MtpConst.FiletypeFolder,
        file->Filetype == MtpConst.FiletypeFolder ? 0 : (long)file->Filesize,
        file->ModificationDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(file->ModificationDate) : null);

    /// <summary>Turns a transfer's result into success or the one exception that explains it.</summary>
    private void Finish(int result, Transfer state, string what)
    {
        // The callback's own failure first: an exception from the stream is the real reason, and
        // libmtp only knows that the handler said no.
        if (state.Failure is { } failure)
        {
            LibMtp.ClearErrorstack(_device);
            throw failure is MtpException ? failure : new MtpException(FileErrorKind.Interrupted, failure.Message);
        }
        if (state.Canceled)
        {
            LibMtp.ClearErrorstack(_device);
            throw new OperationCanceledException();
        }
        if (result != 0)
            throw TakeError(FileErrorKind.Interrupted, what);
    }

    /// <summary>Reads and clears the error stack, classified.</summary>
    /// <remarks>
    /// libmtp reports through a stack rather than a return value, and the entry that says the
    /// device is gone is usually underneath a generic one saying the call failed. So the whole
    /// stack is read and the most specific kind wins.
    /// </remarks>
    private MtpException TakeError(FileErrorKind fallback, string what)
    {
        var kind = fallback;
        var text = new StringBuilder(what);
        for (var error = LibMtp.GetErrorstack(_device); error is not null; error = (MtpError*)error->Next)
        {
            var classified = Classify(error->ErrorNumber);
            if (classified is { } specific && (kind == fallback || specific == FileErrorKind.ConnectionLost))
                kind = specific;
            if (LibMtp.Borrowed(error->ErrorText) is { Length: > 0 } message)
                text.Append(": ").Append(message.Trim());
        }
        LibMtp.ClearErrorstack(_device);
        return new MtpException(kind, text.ToString());
    }

    internal static FileErrorKind? Classify(int errorNumber) => errorNumber switch
    {
        MtpConst.ErrorNoDeviceAttached or MtpConst.ErrorUsbLayer or MtpConst.ErrorConnecting => FileErrorKind.ConnectionLost,
        MtpConst.ErrorStorageFull => FileErrorKind.NoSpace,
        MtpConst.ErrorCancelled => FileErrorKind.Interrupted,
        _ => null
    };

    private static byte* Utf8(string text)
    {
        var count = Encoding.UTF8.GetByteCount(text);
        var buffer = (byte*)NativeMemory.Alloc((nuint)count + 1);
        Encoding.UTF8.GetBytes(text, new Span<byte>(buffer, count));
        buffer[count] = 0;
        return buffer;
    }

    /// <summary>What a transfer's callback needs, reached through the <c>priv</c> pointer.</summary>
    private sealed class Transfer(Stream stream, Func<bool> cancel)
    {
        public Stream Stream { get; } = stream;
        public Func<bool> Cancel { get; } = cancel;
        public long Remaining { get; set; }
        public Exception? Failure { get; set; }
        public bool Canceled { get; set; }
    }

    private static Transfer StateOf(nint priv) => (Transfer)GCHandle.FromIntPtr(priv).Target!;

    // Neither handler ever refuses a block, and no transfer is ever cancelled mid-stream. Both
    // were tried against a Pixel: refusing a block leaves the device sending into a session that
    // has stopped listening, and libmtp's cancel request is not one Android honors -- either
    // way the next request hangs until libusb gives up a minute later, and the session is lost.
    // So a transfer nobody wants any more is run to its end: a download's remaining blocks are
    // dropped as they arrive, and an upload is padded out and then deleted. That costs the rest
    // of the file's time on the wire, and keeps the device usable afterwards.

    /// <summary>libmtp handing over a block of a download.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ushort Put(nint parameters, nint priv, uint length, byte* data, uint* written)
    {
        var state = StateOf(priv);
        if (!state.Canceled && state.Cancel())
            state.Canceled = true;
        // Dropped once nobody wants them. See above for why the transfer is not stopped.
        if (state.Canceled || state.Failure is not null)
        {
            *written = length;
            return MtpConst.HandlerOk;
        }

        try
        {
            state.Stream.Write(new ReadOnlySpan<byte>(data, (int)length));
        }
        catch (Exception ex)
        {
            // An exception must never cross back into C. Kept for Finish to rethrow.
            state.Failure = ex;
        }
        *written = length;
        return MtpConst.HandlerOk;
    }

    /// <summary>libmtp asking for the next block of an upload.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ushort Get(nint parameters, nint priv, uint wanted, byte* data, uint* got)
    {
        var state = StateOf(priv);
        if (!state.Canceled && state.Cancel())
            state.Canceled = true;
        var span = new Span<byte>(data, (int)Math.Min(wanted, (ulong)Math.Max(0, state.Remaining)));
        var total = 0;
        if (!state.Canceled)
        {
            try
            {
                while (total < span.Length)
                {
                    var read = state.Stream.Read(span[total..]);
                    if (read == 0)
                    {
                        // The device was promised more bytes than the source had. Sending short
                        // would leave a truncated file under the real name.
                        state.Failure = new MtpException(FileErrorKind.Interrupted, "the file ended before its announced size");
                        state.Canceled = true;
                        break;
                    }
                    total += read;
                }
            }
            catch (Exception ex)
            {
                state.Failure = ex;
                state.Canceled = true;
            }
        }

        // Once cancelled the block is padded rather than refused: the device was promised a size
        // and has to be given it. Upload deletes the result.
        if (state.Canceled)
        {
            span[total..].Clear();
            total = span.Length;
        }

        state.Remaining -= total;
        *got = (uint)total;
        return MtpConst.HandlerOk;
    }
}
