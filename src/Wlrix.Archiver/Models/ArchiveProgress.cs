namespace Wlrix.Archiver.Models;

/// <summary>What a long archive operation is currently doing.</summary>
public enum ArchivePhase
{
    /// <summary>Undoing the outer compression to get at the archive inside.</summary>
    Decompressing,

    /// <summary>Walking the entry list.</summary>
    Reading,

    /// <summary>Writing entries out to disk.</summary>
    Extracting,

    /// <summary>Rewriting the archive after an edit.</summary>
    Saving,
}

/// <summary>One progress report from a backend.</summary>
/// <param name="Phase">Which step is running.</param>
/// <param name="Fraction">
/// How far through, from 0 to 1, or <c>null</c> when there is no way to know.
/// </param>
/// <param name="Count">Entries handled so far, where that is the meaningful measure.</param>
/// <remarks>
/// Two measures rather than one because the two phases can report honestly about different
/// things. Decompression knows exactly where it is — bytes read against the file's length — and
/// gives a real percentage. Enumeration does not: an archive does not say how many entries it
/// has until you have read them all, so the only truthful report is a running count.
///
/// Which matters because on a large gzip the split is lopsided. Reading a 3.1 GB
/// <c>.tar.gz</c> of 29,631 entries spends about 51 seconds decompressing and a third of a
/// second enumerating, so the percentage covers essentially the whole wait.
/// </remarks>
public readonly record struct ArchiveProgress(
    ArchivePhase Phase,
    double? Fraction = null,
    int Count = 0);
