using Wlrix.Settings.Displays.Models;

namespace Wlrix.Settings.Displays.Services;

/// <summary>
/// Where the panel gets its displays from and sends changes to. The real one speaks
/// wlr-output-management; <see cref="SampleOutputFeed"/> stands in for <c>--demo</c> and the
/// tests.
/// </summary>
public interface IOutputFeed : IDisposable
{
    /// <summary>
    /// Raised with the whole set of displays whenever anything about them changes, including
    /// once on connecting. May be raised on any thread.
    /// </summary>
    event Action<OutputSnapshot>? SnapshotReceived;

    /// <summary>
    /// Raised when there is no compositor to talk to, or it does not offer output management.
    /// May be raised on any thread.
    /// </summary>
    event Action? Unavailable;

    void Start();

    /// <summary>
    /// Ask for every display to be as <paramref name="changes"/> describes, as one atomic
    /// configuration. A display not named is left as it is.
    /// </summary>
    Task<ApplyResult> ApplyAsync(IReadOnlyList<OutputChange> changes);
}
