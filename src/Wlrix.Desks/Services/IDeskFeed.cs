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
}

/// <summary>Thrown when the compositor answers a command with an <c>err</c> reply.</summary>
public sealed class DeskCommandException(string reply)
    : Exception($"The compositor rejected the request: {reply}");
