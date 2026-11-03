namespace Wlrix.Iconbox.Models;

/// <summary>
/// One window as the compositor reports it over <c>wlrix-desks</c>.
/// </summary>
/// <param name="Id">
/// This process's own number for the window, in the order the compositor announced them. Not
/// the compositor's; the protocol has none to share.
/// </param>
/// <param name="AppId">The window's application id, which picks its picture.</param>
/// <param name="Title">The window's title, which is the tile's label.</param>
/// <param name="Minimized">Whether it is minimized, which is what draws its tile raised.</param>
/// <param name="Activated">Whether it has the keyboard, which outlines its tile.</param>
/// <param name="OnVisibleDesk">Whether it is on the active desk or the global one.</param>
/// <param name="Minimizable">
/// Whether its frame offers Minimize. A compositor older than <c>wlrix-desks</c> version 5 does
/// not say, and every window counts as minimizable then.
/// </param>
public sealed record ToplevelInfo(
    long Id,
    string AppId,
    string Title,
    bool Minimized,
    bool Activated,
    bool OnVisibleDesk,
    bool Minimizable);
