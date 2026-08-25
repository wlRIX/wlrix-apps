namespace Wlrix.Archiver.Models;

/// <summary>The archive formats the application knows by name.</summary>
/// <remarks>
/// A container and its compression are separate concerns — <c>.tar.gz</c> is a tar inside a
/// gzip — but the user picks one thing from a file dialog, so they are one list here and the
/// backends unwrap the layers.
/// </remarks>
public enum ArchiveFormat
{
    /// <summary>Nothing recognized it.</summary>
    Unknown,
    Zip,
    Tar,
    TarGz,
    TarBz2,
    TarXz,
    TarZst,
    /// <summary>A bare gzip stream: one member, no directory.</summary>
    GZip,
    BZip2,
    Xz,
    Lzip,
    SevenZip,
    Rar,
}

/// <summary>Names and extensions for <see cref="ArchiveFormat"/>.</summary>
public static class ArchiveFormats
{
    /// <summary>
    /// Extensions, longest first.
    /// </summary>
    /// <remarks>
    /// Order is load-bearing. <c>Path.GetExtension</c> answers <c>.gz</c> for
    /// <c>backup.tar.gz</c>, which would make a tarball look like a single gzipped file with no
    /// entries in it. Matching the whole tail longest-first is what keeps the compound
    /// extensions from being read as their last component.
    /// </remarks>
    private static readonly (string Suffix, ArchiveFormat Format)[] BySuffix =
    [
        (".tar.gz", ArchiveFormat.TarGz),
        (".tar.bz2", ArchiveFormat.TarBz2),
        (".tar.xz", ArchiveFormat.TarXz),
        (".tar.zst", ArchiveFormat.TarZst),
        (".tgz", ArchiveFormat.TarGz),
        (".tbz2", ArchiveFormat.TarBz2),
        (".tbz", ArchiveFormat.TarBz2),
        (".txz", ArchiveFormat.TarXz),
        (".tzst", ArchiveFormat.TarZst),
        (".zip", ArchiveFormat.Zip),
        (".jar", ArchiveFormat.Zip),
        (".tar", ArchiveFormat.Tar),
        (".7z", ArchiveFormat.SevenZip),
        (".rar", ArchiveFormat.Rar),
        (".gz", ArchiveFormat.GZip),
        (".bz2", ArchiveFormat.BZip2),
        (".xz", ArchiveFormat.Xz),
        (".lz", ArchiveFormat.Lzip),
    ];

    /// <summary>The format <paramref name="path"/>'s name suggests, ignoring its contents.</summary>
    public static ArchiveFormat FromPath(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        foreach (var (suffix, format) in BySuffix)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return format;
        }

        return ArchiveFormat.Unknown;
    }

    /// <summary>Whether the format is a tar, however it is compressed.</summary>
    public static bool IsTar(this ArchiveFormat format) => format is ArchiveFormat.Tar
        or ArchiveFormat.TarGz or ArchiveFormat.TarBz2 or ArchiveFormat.TarXz
        or ArchiveFormat.TarZst;

    /// <summary>
    /// Whether the format holds a single compressed stream rather than a directory of entries.
    /// </summary>
    /// <remarks>
    /// These carry one member and no names — a bare <c>.gz</c> knows nothing about what is
    /// inside it beyond an optional original filename — so the listing shows one synthetic
    /// entry and nothing can be added or removed.
    /// </remarks>
    public static bool IsSingleStream(this ArchiveFormat format) => format is ArchiveFormat.GZip
        or ArchiveFormat.BZip2 or ArchiveFormat.Xz or ArchiveFormat.Lzip;
}
