namespace Wlrix.Archiver.Models;

/// <summary>What can be done with an open archive.</summary>
/// <remarks>
/// Format support is lopsided and there is no way around it: SharpCompress reads rar and 7z but
/// writes neither, creating a rar needs the proprietary <c>rar</c> binary, and a gzip stream
/// holds exactly one member so there is nothing to add to or remove from it. Rather than let
/// each command discover its own limits at the point of failure, every backend declares what it
/// can do for a given file up front and the menu enables itself from that.
/// </remarks>
[Flags]
public enum ArchiveCapabilities
{
    None = 0,

    /// <summary>The entry list can be read.</summary>
    Read = 1 << 0,

    /// <summary>Entries can be written out to disk.</summary>
    Extract = 1 << 1,

    /// <summary>Files can be added to the archive in place.</summary>
    Add = 1 << 2,

    /// <summary>Entries can be deleted from the archive.</summary>
    Remove = 1 << 3,

    /// <summary>A new, empty archive of this format can be created.</summary>
    Create = 1 << 4,

    /// <summary>What this backend writes can be encrypted with a password.</summary>
    /// <remarks>
    /// Separate from <see cref="Add"/> because reading encryption and writing it are different
    /// abilities, and the library this application leans on has only the first: SharpCompress
    /// exposes <c>Password</c> on its reader options and on nothing that writes, so it can open
    /// an encrypted zip and can never produce one. The 7z command line can, which is why this is
    /// a property of the backend rather than of the format.
    ///
    /// <para>
    /// Offering encryption where it does not hold would be the worst kind of wrong: an archive
    /// somebody believes is protected and is not.
    /// </para>
    /// </remarks>
    Encrypt = 1 << 5,

    /// <summary>Everything a format can do without encryption: the common case for zip and tar.</summary>
    All = Read | Extract | Add | Remove | Create,

    /// <summary>Read-only, which is what rar and 7z come to without an external tool.</summary>
    ReadOnly = Read | Extract,
}
