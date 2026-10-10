// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.ClipboardHistory.Services;

/// <summary>
/// Keeps the popup to one window per session, and makes a second launch close it.
/// </summary>
/// <remarks>
/// <c>Super+V</c> starts <c>wlrix-clipboard-history</c> every time it is pressed. Pressed while
/// the popup is open, the second copy tells the first to close and exits without a window — so
/// the key opens and dismisses the popup, and the compositor keeps no state about it.
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
    /// only useful thing left to do is <see cref="SendActivateAsync"/> and exit.
    /// </returns>
    /// <remarks>
    /// Answers true when there is no session bus at all, so the popup can open and say why it
    /// has nothing to show.
    /// </remarks>
    Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the instance that already exists to close.</summary>
    Task SendActivateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised on the primary when another invocation asks it to close.
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
