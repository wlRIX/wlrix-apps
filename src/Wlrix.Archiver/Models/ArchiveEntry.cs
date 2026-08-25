namespace Wlrix.Archiver.Models;

/// <summary>One member of an archive, as the listing shows it.</summary>
/// <param name="Path">
/// The full path inside the archive, with <c>/</c> separators and no leading slash.
/// </param>
/// <param name="IsDirectory">Whether this is a directory rather than a file.</param>
/// <param name="Size">Uncompressed size in bytes, or <c>null</c> when the format does not say.</param>
/// <param name="CompressedSize">Stored size in bytes, or <c>null</c>.</param>
/// <param name="Modified">Last modification time, or <c>null</c> when the format does not record one.</param>
/// <param name="Mode">The Unix permission bits, or <c>null</c>. Only tar and some zips carry these.</param>
/// <param name="Owner">Owning user name, falling back to the numeric uid, or <c>null</c>.</param>
/// <param name="Group">Owning group name, falling back to the numeric gid, or <c>null</c>.</param>
/// <param name="IsEncrypted">Whether reading the contents needs a password.</param>
/// <param name="LinkTarget">Where a symlink points, or <c>null</c> for anything else.</param>
/// <param name="Synthesized">
/// Whether this directory was inferred from a child's path rather than read from the archive.
/// See <see cref="ArchiveTree"/> for why that happens.
/// </param>
public sealed record ArchiveEntry(
    string Path,
    bool IsDirectory,
    long? Size,
    long? CompressedSize,
    DateTime? Modified,
    int? Mode,
    string? Owner,
    string? Group,
    bool IsEncrypted = false,
    string? LinkTarget = null,
    bool Synthesized = false)
{
    /// <summary>The last path component: what the tree shows on the row.</summary>
    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            return slash < 0 ? trimmed : trimmed[(slash + 1)..];
        }
    }

    /// <summary>
    /// The permission bits as <c>drwxr-xr-x</c>, or an empty string when the format records none.
    /// </summary>
    /// <remarks>
    /// Rendered here rather than in the view model because it is a property of the entry, and
    /// because the tests want it without constructing any UI.
    /// </remarks>
    public string ModeText
    {
        get
        {
            if (Mode is not { } mode)
                return string.Empty;

            Span<char> text = stackalloc char[10];
            text[0] = LinkTarget is not null ? 'l' : IsDirectory ? 'd' : '-';
            const string Rwx = "rwxrwxrwx";
            for (var i = 0; i < 9; i++)
            {
                // Bit 8 is owner-read and bit 0 is other-execute, so the leftmost character of
                // the string is the highest bit.
                var set = (mode & (1 << (8 - i))) != 0;
                text[i + 1] = set ? Rwx[i] : '-';
            }

            ApplySpecialBits(text, mode);
            return new string(text);
        }
    }

    /// <summary>
    /// Overlays setuid, setgid and the sticky bit onto the execute positions, the way
    /// <c>ls</c> does: an <c>s</c> where the execute bit is also set, an <c>S</c> where it is not.
    /// </summary>
    private static void ApplySpecialBits(Span<char> text, int mode)
    {
        const int SetUid = 0x800;
        const int SetGid = 0x400;
        const int Sticky = 0x200;

        if ((mode & SetUid) != 0)
            text[3] = text[3] == 'x' ? 's' : 'S';
        if ((mode & SetGid) != 0)
            text[6] = text[6] == 'x' ? 's' : 'S';
        if ((mode & Sticky) != 0)
            text[9] = text[9] == 'x' ? 't' : 'T';
    }
}
