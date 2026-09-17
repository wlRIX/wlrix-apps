namespace Wlrix.Files.Core;

/// <summary>
/// Keeps the file manager to one process per session.
/// </summary>
/// <remarks>
/// Not tidiness. The Classic-mode window registry is an in-process dictionary, and it is only
/// correct if there is one process to ask: two instances would each raise their own window for
/// a directory and IRIX's one-window-per-directory rule would quietly become two.
///
/// <para>
/// An interface because the implementation talks to the session bus and CI has none. Nothing
/// in the test suite ever instantiates the real one.
/// </para>
/// </remarks>
public interface IInstanceGuard : IAsyncDisposable
{
    /// <summary>
    /// Tries to become the one instance.
    /// </summary>
    /// <returns>
    /// True if this process is now the primary. False if another already is, in which case
    /// the only useful thing left to do is <see cref="SendOpenAsync"/> and exit.
    /// </returns>
    /// <remarks>
    /// Answers true when there is no session bus at all. A file manager that refused to start
    /// because it could not find D-Bus would be a worse failure than two windows.
    /// </remarks>
    Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the instance that already exists to open these, and to come forward.</summary>
    Task SendOpenAsync(IReadOnlyList<string> uris, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised on the primary when another invocation hands it something to open.
    /// </summary>
    /// <remarks>
    /// Arrives on a bus thread. A subscriber that touches the UI marshals it itself, which is
    /// the same contract the operation queue's progress uses and for the same reason: this
    /// assembly must not know a dispatcher exists.
    /// </remarks>
    event Action<IReadOnlyList<string>>? OpenRequested;

    /// <summary>Asks the existing instance to perform one of the desktop entry's actions.</summary>
    Task SendActionAsync(string action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised on the primary when a desktop-entry action is invoked.
    /// </summary>
    /// <remarks>
    /// Arrives on a bus thread, like <see cref="OpenRequested"/>, and marshalling is the
    /// subscriber's job for the same reason.
    /// </remarks>
    event Action<string>? ActionRequested;
}

/// <summary>An instance guard for somewhere there is no bus, and for tests.</summary>
/// <remarks>
/// Always primary, never receives anything. That is the right answer for a single run under a
/// test harness, and the honest one for a session with no D-Bus.
/// </remarks>
public sealed class SoleInstanceGuard : IInstanceGuard
{
    public Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task SendOpenAsync(IReadOnlyList<string> uris, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SendActionAsync(string action, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public event Action<IReadOnlyList<string>>? OpenRequested
    {
        add { }
        remove { }
    }

    public event Action<string>? ActionRequested
    {
        add { }
        remove { }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
