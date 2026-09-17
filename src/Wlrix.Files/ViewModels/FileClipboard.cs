using Wlrix.Files.Core;

namespace Wlrix.Files.ViewModels;

/// <summary>What Cut or Copy put aside, waiting for a Paste.</summary>
/// <remarks>
/// Application-scoped rather than per window, so a cut in one window pastes in another —
/// which is the whole point of having two windows open in Classic mode.
///
/// <para>
/// Deliberately not the system clipboard yet. Sharing cut and paste with other applications
/// means the <c>x-special/gnome-copied-files</c> convention and a matching parse of what
/// other file managers put there; until that is written, keeping it in-process is honest
/// rather than half-working.
/// </para>
/// </remarks>
public sealed class FileClipboard
{
    /// <summary>What is on the clipboard, if anything.</summary>
    public IReadOnlyList<Location> Locations { get; private set; } = [];

    /// <summary>Whether pasting should move rather than copy.</summary>
    public bool IsCut { get; private set; }

    public bool HasContent => Locations.Count > 0;

    /// <summary>Raised when the contents change, so Paste can enable itself.</summary>
    public event Action? Changed;

    public void Copy(IReadOnlyList<Location> locations) => Set(locations, cut: false);

    public void Cut(IReadOnlyList<Location> locations) => Set(locations, cut: true);

    /// <summary>
    /// Empties the clipboard after a cut has been pasted.
    /// </summary>
    /// <remarks>
    /// A cut is consumed by its paste: the sources no longer exist, so leaving them on the
    /// clipboard would make the next paste fail on files that are already gone. A copy stays,
    /// because pasting it twice is meaningful.
    /// </remarks>
    public void ConsumeIfCut()
    {
        if (IsCut)
            Set([], cut: false);
    }

    private void Set(IReadOnlyList<Location> locations, bool cut)
    {
        Locations = locations;
        IsCut = cut;
        Changed?.Invoke();
    }
}
