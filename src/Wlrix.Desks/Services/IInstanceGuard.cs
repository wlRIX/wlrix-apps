namespace Wlrix.Desks.Services;

/// <summary>
/// Keeps the overview to one window per session — IRIX's "runonce".
/// </summary>
/// <remarks>
/// The session starts <c>wlrix-desks</c> at login and the Toolchest's Desktop menu runs it
/// again, so a second launch is the ordinary case rather than an odd one. It should bring the
/// overview that already exists forward instead of stacking another identical window behind it.
///
/// <para>
/// An interface because the implementation talks to the session bus and CI has none. Nothing in
/// the test suite ever instantiates the real one.
/// </para>
/// </remarks>
public interface IInstanceGuard : IAsyncDisposable
{
    /// <summary>
    /// Tries to become the one instance.
    /// </summary>
    /// <returns>
    /// True if this process is now the primary. False if another already is, in which case the
    /// only useful thing left to do is <see cref="SendActivateAsync"/> and exit — unless
    /// <c>--new</c> was asked for, which is exactly the request to carry on anyway.
    /// </returns>
    /// <remarks>
    /// Answers true when there is no session bus at all. An overview that refused to open
    /// because it could not find D-Bus would be a worse failure than the second window the
    /// guard exists to prevent.
    /// </remarks>
    Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the instance that already exists to come forward.</summary>
    Task SendActivateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised on the primary when another invocation asks it to come forward.
    /// </summary>
    /// <remarks>
    /// Arrives on a bus thread. A subscriber that touches the UI marshals it itself; this type
    /// must not know a dispatcher exists.
    /// </remarks>
    event Action? ActivateRequested;
}

/// <summary>An instance guard for somewhere there is no bus, and for tests.</summary>
/// <remarks>
/// Always primary, never receives anything. That is the right answer for a single run under a
/// test harness, and the honest one for a session with no D-Bus.
/// </remarks>
public sealed class SoleInstanceGuard : IInstanceGuard
{
    public Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task SendActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public event Action? ActivateRequested
    {
        add { }
        remove { }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
