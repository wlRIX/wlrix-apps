namespace Wlrix.Files.Core.Metadata;

/// <summary>Who a permission applies to.</summary>
public enum PermissionClass
{
    Owner,
    Group,
    Other
}

/// <summary>What is permitted.</summary>
/// <remarks>
/// The values are the POSIX bits themselves, so a class's three bits are one shift away
/// rather than a lookup table.
/// </remarks>
[Flags]
public enum PermissionBits
{
    None = 0,
    Execute = 1,
    Write = 2,
    Read = 4
}

/// <summary>
/// The low twelve bits of a POSIX mode, as something a checkbox grid can bind to.
/// </summary>
/// <remarks>
/// A value type over an <c>int</c> rather than a wrapper around .NET's
/// <see cref="UnixFileMode"/>, for two reasons. The mode arrives from
/// <see cref="FileEntry.UnixMode"/> as an int and goes back through
/// <see cref="IFileSystem.SetUnixModeAsync"/> as one, so this is the shape at both ends; and
/// <see cref="UnixFileMode"/> has no setuid, setgid or sticky bit worth the name — it spells
/// them, but nothing in <c>System.IO</c> will set them, and a permissions editor that silently
/// dropped the setgid bit off a shared directory would be worse than one that never showed it.
///
/// <para>
/// Only the permission bits are held. The file-type bits above them belong to the kind, which
/// is already <see cref="FileKind"/>, and are not something a user may edit.
/// </para>
/// </remarks>
public readonly record struct UnixPermissions
{
    /// <summary>Every bit this type represents: the permissions plus the three special ones.</summary>
    public const int Mask = 0xFFF;

    public const int SetUserIdBit = 0x800;
    public const int SetGroupIdBit = 0x400;
    public const int StickyBit = 0x200;

    public UnixPermissions(int mode) => Mode = mode & Mask;

    /// <summary>The twelve bits, in the form <see cref="IFileSystem.SetUnixModeAsync"/> takes.</summary>
    public int Mode { get; }

    public bool SetUserId => (Mode & SetUserIdBit) != 0;

    public bool SetGroupId => (Mode & SetGroupIdBit) != 0;

    /// <summary>
    /// The restricted-deletion flag. On a directory it means only an entry's owner may remove
    /// it, which is what <c>/tmp</c> has and why the word "sticky" no longer describes it.
    /// </summary>
    public bool Sticky => (Mode & StickyBit) != 0;

    /// <summary>What one class of user may do.</summary>
    public PermissionBits For(PermissionClass who) =>
        (PermissionBits)((Mode >> Shift(who)) & 7);

    public bool Has(PermissionClass who, PermissionBits what) => (For(who) & what) == what;

    /// <summary>The same permissions with one bit turned on or off.</summary>
    public UnixPermissions With(PermissionClass who, PermissionBits what, bool allowed)
    {
        var bit = (int)what << Shift(who);
        return new UnixPermissions(allowed ? Mode | bit : Mode & ~bit);
    }

    public UnixPermissions WithSetUserId(bool on) => WithSpecial(SetUserIdBit, on);

    public UnixPermissions WithSetGroupId(bool on) => WithSpecial(SetGroupIdBit, on);

    public UnixPermissions WithSticky(bool on) => WithSpecial(StickyBit, on);

    /// <summary>The four-digit octal form, as <c>chmod</c> takes it: <c>0755</c>, <c>1777</c>.</summary>
    public string Octal => Convert.ToString(Mode, 8).PadLeft(4, '0');

    /// <summary>
    /// The nine-character form <c>ls</c> prints: <c>rwxr-xr-x</c>.
    /// </summary>
    /// <remarks>
    /// The special bits are folded into the execute column the way <c>ls</c> folds them —
    /// lowercase when the execute bit is also set, uppercase when it is not. That is not
    /// decoration: <c>rwS</c> means a setuid file nobody can execute, which is almost always a
    /// mistake, and the case is the only thing that says so.
    /// </remarks>
    public string Symbolic
    {
        get
        {
            Span<char> text = stackalloc char[9];
            Write(text[..3], PermissionClass.Owner, SetUserId, 's');
            Write(text[3..6], PermissionClass.Group, SetGroupId, 's');
            Write(text[6..], PermissionClass.Other, Sticky, 't');
            return new string(text);
        }
    }

    /// <summary>The character <c>ls</c> puts in front of the permissions.</summary>
    public static char KindCharacter(FileKind kind) => kind switch
    {
        FileKind.Directory => 'd',
        FileKind.Symlink => 'l',
        FileKind.Fifo => 'p',
        FileKind.Socket => 's',
        FileKind.BlockDevice => 'b',
        FileKind.CharDevice => 'c',
        _ => '-'
    };

    /// <summary>The whole ten-character line, kind included.</summary>
    public string Describe(FileKind kind) => KindCharacter(kind) + Symbolic;

    /// <summary>
    /// Reads an octal mode, with or without a leading zero, refusing anything else.
    /// </summary>
    /// <remarks>
    /// Typed by hand into the properties dialog, so it has to refuse <c>chmod</c>'s symbolic
    /// form (<c>u+x</c>) rather than half-understand it, and refuse a digit above 7 rather
    /// than let <see cref="Convert.ToInt32(string,int)"/> throw.
    /// </remarks>
    public static bool TryParseOctal(string? text, out UnixPermissions permissions)
    {
        permissions = default;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 4)
            return false;

        var mode = 0;
        foreach (var c in trimmed)
        {
            if (c is < '0' or > '7')
                return false;
            mode = (mode << 3) | (c - '0');
        }

        permissions = new UnixPermissions(mode);
        return true;
    }

    public override string ToString() => Symbolic;

    private UnixPermissions WithSpecial(int bit, bool on) =>
        new(on ? Mode | bit : Mode & ~bit);

    private static int Shift(PermissionClass who) => who switch
    {
        PermissionClass.Owner => 6,
        PermissionClass.Group => 3,
        _ => 0
    };

    private void Write(Span<char> into, PermissionClass who, bool special, char specialChar)
    {
        var bits = For(who);
        into[0] = (bits & PermissionBits.Read) != 0 ? 'r' : '-';
        into[1] = (bits & PermissionBits.Write) != 0 ? 'w' : '-';
        var executable = (bits & PermissionBits.Execute) != 0;
        into[2] = special
            ? executable ? specialChar : char.ToUpperInvariant(specialChar)
            : executable ? 'x' : '-';
    }
}
