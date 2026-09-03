using Wlrix.Desks.Localization;
using Wlrix.Desks.Models;

namespace Wlrix.Desks.Services;

/// <summary>
/// A source of desk snapshots plus the commands that mutate them. Implemented by
/// <see cref="DeskIpcClient"/> (the live compositor socket) and <see cref="SampleDeskFeed"/>
/// (an offline stand-in). Events may be raised on background threads.
/// </summary>
public interface IDeskFeed : IDisposable
{
    /// <summary>Raised with a full snapshot on connect and on every desk/window change.</summary>
    event Action<DeskSnapshot>? SnapshotReceived;

    /// <summary>Raised when the feed cannot reach the compositor (it keeps retrying).</summary>
    event Action? Unavailable;

    /// <summary>Begins receiving snapshots.</summary>
    void Start();

    /// <summary>Switches the active desk. Throws <see cref="DeskCommandException"/> on rejection.</summary>
    Task SwitchAsync(int id);

    /// <summary>Creates a desk with the given name. Throws <see cref="DeskCommandException"/> on rejection.</summary>
    Task CreateAsync(string name);

    /// <summary>Removes a desk. Throws <see cref="DeskCommandException"/> on rejection.</summary>
    Task RemoveAsync(int id);

    /// <summary>Renames a desk. Throws <see cref="DeskCommandException"/> on rejection.</summary>
    Task RenameAsync(int id, string name);

    /// <summary>Minimizes a window to its icon in the compositor's minimized grid.</summary>
    Task MinimizeWindowAsync(long id);

    /// <summary>Restores a minimized window.</summary>
    Task RestoreWindowAsync(long id);

    /// <summary>Raises a window to the top of the stacking order.</summary>
    Task RaiseWindowAsync(long id);

    /// <summary>Lowers a window to the bottom of the stacking order.</summary>
    Task LowerWindowAsync(long id);

    /// <summary>Moves a window to another desk; desk 0 is the Global desk, which shows its
    /// windows on every desk.</summary>
    Task MoveWindowToDeskAsync(long id, int deskId);
}

/// <summary>Thrown when the compositor answers a command with an <c>err</c> reply.</summary>
/// <remarks>
/// The message is localized because it is shown to the person: the view model hands it straight
/// to a <c>MessageDialog</c> rather than logging it. What the compositor said is not translated
/// and cannot be — it is the protocol's own word for what went wrong.
/// </remarks>
public sealed class DeskCommandException(string reply)
    : Exception(Strings.CommandRejected(reply));
