using Wlrix.Archiver.Models;

namespace Wlrix.Archiver.Services.Archives;

/// <summary>Works out what a file is, and which backend should handle it.</summary>
public interface IArchiveBackendRegistry
{
    /// <summary>
    /// The format of the file at <paramref name="path"/>, from its contents where they say and
    /// its name otherwise.
    /// </summary>
    ArchiveFormat Identify(string path);

    /// <summary>
    /// The backend to use for <paramref name="format"/>, or <c>null</c> if nothing handles it.
    /// </summary>
    IArchiveBackend? For(ArchiveFormat format);

    /// <summary>What can be done with <paramref name="format"/> by the best available backend.</summary>
    ArchiveCapabilities CapabilitiesFor(ArchiveFormat format);
}

/// <inheritdoc />
public sealed class ArchiveBackendRegistry : IArchiveBackendRegistry
{
    private readonly IReadOnlyList<IArchiveBackend> _backends;

    /// <param name="backends">
    /// In preference order. Ties on capability count go to the earlier one, so the managed
    /// backend should come first and the ones that shell out after it.
    /// </param>
    public ArchiveBackendRegistry(IEnumerable<IArchiveBackend> backends) =>
        _backends = backends.ToList();

    public ArchiveFormat Identify(string path)
    {
        // Contents first. An extension is a hint the user controls — a `.zip` that is really a
        // 7z, or a tarball someone renamed — and opening it as what it claims to be produces a
        // parse error rather than the archive that is plainly there.
        var sniffed = Sniff(path);
        if (sniffed != ArchiveFormat.Unknown)
            return sniffed;

        return ArchiveFormats.FromPath(path);
    }

    public IArchiveBackend? For(ArchiveFormat format) => _backends
        .Where(backend => backend.Supports(format) != ArchiveCapabilities.None)
        // The most capable wins, so 7z becomes writable exactly when the external tool is
        // installed and stays readable through the managed backend when it is not.
        .OrderByDescending(backend => System.Numerics.BitOperations.PopCount(
            (uint)backend.Supports(format)))
        .FirstOrDefault();

    public ArchiveCapabilities CapabilitiesFor(ArchiveFormat format) =>
        For(format)?.Supports(format) ?? ArchiveCapabilities.None;

    /// <summary>The format the first few bytes say this is.</summary>
    /// <remarks>
    /// Magic numbers only, no library involved: this runs before a backend is chosen, so it
    /// cannot ask one. A compressed tar cannot be told from a compressed anything-else without
    /// decompressing, so <c>.gz</c> and friends fall back to the extension for the tar/not-tar
    /// half of the question, which is what the compound suffixes in
    /// <see cref="ArchiveFormats"/> are for.
    /// </remarks>
    internal static ArchiveFormat Sniff(string path)
    {
        Span<byte> head = stackalloc byte[6];
        int read;
        try
        {
            using var stream = File.OpenRead(path);
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ArchiveFormat.Unknown;
        }

        if (read >= 4 && head[0] == 'P' && head[1] == 'K'
            && (head[2] == 3 || head[2] == 5 || head[2] == 7))
        {
            return ArchiveFormat.Zip;
        }

        if (read >= 6 && head[0] == '7' && head[1] == 'z' && head[2] == 0xbc && head[3] == 0xaf
            && head[4] == 0x27 && head[5] == 0x1c)
        {
            return ArchiveFormat.SevenZip;
        }

        if (read >= 4 && head[0] == 'R' && head[1] == 'a' && head[2] == 'r' && head[3] == '!')
            return ArchiveFormat.Rar;

        // The compressed wrappers say nothing about what is inside them, so the extension
        // decides between `.tar.gz` and a lone `.gz`, and only the extension can.
        if (read >= 2 && head[0] == 0x1f && head[1] == 0x8b)
            return FromExtensionOr(path, ArchiveFormat.GZip, ArchiveFormat.TarGz);

        if (read >= 3 && head[0] == 'B' && head[1] == 'Z' && head[2] == 'h')
            return FromExtensionOr(path, ArchiveFormat.BZip2, ArchiveFormat.TarBz2);

        if (read >= 6 && head[0] == 0xfd && head[1] == '7' && head[2] == 'z' && head[3] == 'X'
            && head[4] == 'Z' && head[5] == 0x00)
        {
            return FromExtensionOr(path, ArchiveFormat.Xz, ArchiveFormat.TarXz);
        }

        if (read >= 4 && head[0] == 'L' && head[1] == 'Z' && head[2] == 'I' && head[3] == 'P')
            return ArchiveFormat.Lzip;

        // Tar's "magic" is 5 bytes 257 into the header, well past what was read, and plenty of
        // tars predate even that. Left to the extension.
        return ArchiveFormat.Unknown;
    }

    /// <summary>
    /// <paramref name="tarred"/> when the name says this wrapper holds a tar, else
    /// <paramref name="bare"/>.
    /// </summary>
    private static ArchiveFormat FromExtensionOr(string path, ArchiveFormat bare,
        ArchiveFormat tarred) =>
        ArchiveFormats.FromPath(path).IsTar() ? tarred : bare;
}
