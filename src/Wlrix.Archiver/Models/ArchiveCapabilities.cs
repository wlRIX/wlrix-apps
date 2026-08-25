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

    /// <summary>Everything: the common case for zip and tar.</summary>
    All = Read | Extract | Add | Remove | Create,

    /// <summary>Read-only, which is what rar and 7z come to without an external tool.</summary>
    ReadOnly = Read | Extract,
}
