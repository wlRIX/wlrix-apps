namespace Wlrix.Desks.Models;

/// <summary>One workspace ("desk"). Id 0 is the always-visible Global desk.</summary>
public sealed record DeskInfo(int Id, bool Active, string Name);

/// <summary>
/// A top-level window. Geometry is where the window is shown, in compositor-logical
/// coordinates: the decorated frame, or -- while <see cref="Minimized"/> -- the rectangle of
/// its icon in the compositor's minimized-window grid. <see cref="DeskId"/> 0 means the window
/// lives on the Global desk and is therefore visible on every desk.
/// </summary>
public sealed record WindowInfo(
    long Id, int DeskId, double X, double Y, double W, double H, bool Minimized, string AppId, string Title);

/// <summary>
/// A full desk/window snapshot, as produced by the compositor's <c>wlrix-desks</c> feed. The
/// output layout the previews are scaled against comes from Avalonia's <c>Screens</c>, not the
/// feed, so it is not part of the snapshot.
/// </summary>
public sealed record DeskSnapshot(
    IReadOnlyList<DeskInfo> Desks,
    IReadOnlyList<WindowInfo> Windows)
{
    public static DeskSnapshot Empty { get; } = new([], []);
}
