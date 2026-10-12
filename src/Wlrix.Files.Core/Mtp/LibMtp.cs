using System.Runtime.InteropServices;

namespace Wlrix.Files.Core.Mtp;

// The slice of libmtp the file manager uses, and nothing more: finding and opening a device,
// its storages, a folder's handles and one object's details, streaming a file in either direction, and the
// four calls that change the tree.
//
// The structs are declared field for field after <libmtp.h> as of 1.1.x, whose layouts have
// been stable since the 1.0 series. Only LIBMTP_file_t is ever built on this side, for an
// upload; everything else is read through pointers libmtp hands back and copied out before it
// is freed.
//
// None of this is thread-safe, and libmtp says so: every call for one device has to come from
// one thread, which is what MtpDeviceThread exists to guarantee.

[StructLayout(LayoutKind.Sequential)]
internal struct MtpDeviceEntry
{
    public nint Vendor;
    public ushort VendorId;
    public nint Product;
    public ushort ProductId;
    public uint DeviceFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtpRawDevice
{
    public MtpDeviceEntry DeviceEntry;
    public uint BusLocation;
    public byte DevNum;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtpFile
{
    public uint ItemId;
    public uint ParentId;
    public uint StorageId;
    public nint Filename;
    public ulong Filesize;
    public long ModificationDate;
    public int Filetype;
    public nint Next;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtpStorage
{
    public uint Id;
    public ushort StorageType;
    public ushort FilesystemType;
    public ushort AccessCapability;
    public ulong MaxCapacity;
    public ulong FreeSpaceInBytes;
    public ulong FreeSpaceInObjects;
    public nint StorageDescription;
    public nint VolumeIdentifier;
    public nint Next;
    public nint Prev;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtpError
{
    public int ErrorNumber;
    public nint ErrorText;
    public nint Next;
}

/// <summary>The head of <c>LIBMTP_mtpdevice_t</c>, as far as the storage list.</summary>
/// <remarks>
/// A prefix only. The device is always allocated by libmtp and only ever read through a
/// pointer, so declaring the leading fields is enough and leaves the rest free to grow.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct MtpDeviceHead
{
    public byte ObjectBitsize;
    public nint Params;
    public nint UsbInfo;
    public nint Storage;
    public nint ErrorStack;
}

internal static class MtpConst
{
    /// <summary>The parent id of anything at the top of a storage.</summary>
    public const uint Root = 0xffffffff;

    public const int FiletypeFolder = 0;
    /// <summary><c>LIBMTP_FILETYPE_UNKNOWN</c>: what an upload is sent as, so the phone sniffs it.</summary>
    public const int FiletypeUnknown = 44;

    public const ushort HandlerOk = 0;

    public const int ErrorNone = 0;
    public const int ErrorGeneral = 1;
    public const int ErrorPtpLayer = 2;
    public const int ErrorUsbLayer = 3;
    public const int ErrorMemoryAllocation = 4;
    public const int ErrorNoDeviceAttached = 5;
    public const int ErrorStorageFull = 6;
    public const int ErrorConnecting = 7;
    public const int ErrorCancelled = 8;

    /// <summary>The storage's <c>AccessCapability</c>: anything but zero refuses writes.</summary>
    public const ushort AccessReadWrite = 0;
}

internal static unsafe partial class LibMtp
{
    private const string Lib = "libmtp.so.9";

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Init")]
    public static partial void Init();

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Detect_Raw_Devices")]
    public static partial int DetectRawDevices(out MtpRawDevice* devices, out int count);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Open_Raw_Device_Uncached")]
    public static partial nint OpenRawDeviceUncached(MtpRawDevice* device);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Release_Device")]
    public static partial void ReleaseDevice(nint device);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Get_Friendlyname")]
    public static partial nint GetFriendlyname(nint device);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Get_Modelname")]
    public static partial nint GetModelname(nint device);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Get_Storage")]
    public static partial int GetStorage(nint device, int sortBy);

    /// <summary>A folder's child handles, in an array the caller frees. Negative on failure.</summary>
    [LibraryImport(Lib, EntryPoint = "LIBMTP_Get_Children")]
    public static partial int GetChildren(nint device, uint storage, uint parent, out uint* children);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Get_Filemetadata")]
    public static partial MtpFile* GetFilemetadata(nint device, uint id);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_destroy_file_t")]
    public static partial void DestroyFile(MtpFile* file);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Get_File_To_Handler")]
    public static partial int GetFileToHandler(
        nint device, uint id,
        delegate* unmanaged[Cdecl]<nint, nint, uint, byte*, uint*, ushort> put, nint priv,
        void* progress, nint progressData);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Send_File_From_Handler")]
    public static partial int SendFileFromHandler(
        nint device,
        delegate* unmanaged[Cdecl]<nint, nint, uint, byte*, uint*, ushort> get, nint priv,
        MtpFile* metadata, void* progress, nint progressData);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Create_Folder")]
    public static partial uint CreateFolder(nint device, byte* name, uint parent, uint storage);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Delete_Object")]
    public static partial int DeleteObject(nint device, uint id);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Set_Object_Filename")]
    public static partial int SetObjectFilename(nint device, uint id, byte* name);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Move_Object")]
    public static partial int MoveObject(nint device, uint id, uint storage, uint parent);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Get_Errorstack")]
    public static partial MtpError* GetErrorstack(nint device);

    [LibraryImport(Lib, EntryPoint = "LIBMTP_Clear_Errorstack")]
    public static partial void ClearErrorstack(nint device);

    /// <summary>Frees memory libmtp allocated and handed over: strings and the raw device array.</summary>
    [LibraryImport(Lib, EntryPoint = "LIBMTP_FreeMemory")]
    public static partial void FreeMemory(void* memory);

    /// <summary>Reads a string libmtp still owns.</summary>
    public static string? Borrowed(nint utf8) => utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);

    /// <summary>Reads a string libmtp handed over, and frees it.</summary>
    public static string? Taken(nint utf8)
    {
        if (utf8 == 0)
            return null;
        var text = Marshal.PtrToStringUTF8(utf8);
        FreeMemory((void*)utf8);
        return text;
    }
}
