namespace Wlrix.Files.Core.Mtp;

/// <summary>One storage on a device: a phone's internal storage, or its SD card.</summary>
/// <param name="Id">The device's id for it. Stable while the device stays connected.</param>
/// <param name="Description">What the device calls it, e.g. "Internal shared storage".</param>
/// <param name="CapacityBytes">How big it is.</param>
/// <param name="FreeBytes">How much of it is free.</param>
/// <param name="IsReadOnly">Whether the device refuses writes to it.</param>
public sealed record MtpStorageInfo(uint Id, string Description, long CapacityBytes, long FreeBytes, bool IsReadOnly);

/// <summary>One file or folder on a device, as a listing reports it.</summary>
/// <param name="Handle">The device's id for it. MTP has no paths, only these.</param>
/// <param name="Parent">The containing folder's handle, or <see cref="MtpDevice.Root"/>.</param>
/// <param name="Storage">Which storage it is on.</param>
public sealed record MtpObject(
    uint Handle,
    uint Parent,
    uint Storage,
    string Name,
    bool IsFolder,
    long Size,
    DateTimeOffset? Modified);

/// <summary>Constants shared by every device.</summary>
public static class MtpDevice
{
    /// <summary>The parent of anything at the top of a storage.</summary>
    public const uint Root = MtpConst.Root;
}

/// <summary>Why a device call failed, before it has a location to be reported against.</summary>
/// <remarks>
/// The device layer knows nothing of paths, so it cannot build a
/// <see cref="FileOperationException"/> itself; the filesystem catches this and attaches the
/// location the caller asked about.
/// </remarks>
public sealed class MtpException(FileErrorKind kind, string message) : Exception(message)
{
    public FileErrorKind Kind { get; } = kind;
}

/// <summary>
/// One connected device, in the vocabulary of the protocol rather than of files.
/// </summary>
/// <remarks>
/// The seam between libmtp and everything else. <see cref="MtpFileSystem"/> turns paths into
/// handles over this, and a test drives it with an in-memory device instead of a phone.
///
/// <para>
/// <b>Every call is synchronous, and every call for one device must come from one thread.</b>
/// libmtp is not thread-safe, and the protocol could not do two things at once anyway: a
/// transfer holds the device until it finishes. <see cref="MtpDeviceThread"/> is how that is
/// kept true. Failures are thrown as <see cref="MtpException"/> and nothing else.
/// </para>
/// </remarks>
public interface IMtpDevice : IDisposable
{
    /// <summary>What the device calls itself, or null if it will not say.</summary>
    string? FriendlyName { get; }

    /// <summary>
    /// The storages, read afresh. Empty while an Android phone is locked, which is how it
    /// keeps its files to itself until the owner allows access.
    /// </summary>
    IReadOnlyList<MtpStorageInfo> ReadStorages();

    /// <summary>The handles of what is in one folder. Cheap: no names, sizes or dates.</summary>
    /// <remarks>
    /// Separate from <see cref="Metadata"/> because the two cost wildly different amounts. A
    /// phone answers this for fifteen thousand files in a few seconds, and then wants a request
    /// per file for anything about them -- minutes for the same folder. Keeping them apart is
    /// what lets a listing be shown as it arrives and a poll fetch only what is new.
    /// </remarks>
    IReadOnlyList<uint> ChildHandles(uint storage, uint parent);

    /// <summary>One object's name, size and date, or null if it has gone.</summary>
    MtpObject? Metadata(uint handle);

    /// <summary>
    /// Streams a file's contents into <paramref name="sink"/>, returning when it is all there.
    /// </summary>
    /// <param name="cancel">Polled as data arrives. True abandons the transfer.</param>
    void Download(uint handle, Stream sink, Func<bool> cancel);

    /// <summary>
    /// Creates a file from <paramref name="source"/>, which must hold exactly
    /// <paramref name="length"/> bytes, answering the new file's handle.
    /// </summary>
    /// <remarks>The size is needed before the first byte: MTP announces it to the device up front.</remarks>
    uint Upload(Stream source, long length, uint storage, uint parent, string name, Func<bool> cancel);

    /// <summary>Creates a folder, answering its handle.</summary>
    uint CreateFolder(uint storage, uint parent, string name);

    /// <summary>Deletes one object. A folder should be empty: what a device does with a full one varies.</summary>
    void Delete(uint handle);

    void Rename(uint handle, string name);

    /// <summary>Moves an object into another folder, keeping its name.</summary>
    void Move(uint handle, uint storage, uint parent);
}
