namespace Wlrix.Files.Core.Dnd;

/// <summary>
/// Opening a folder by hovering over it during a drag.
/// </summary>
/// <remarks>
/// The alternative is dropping the selection somewhere, navigating, and dragging it again,
/// which is what makes drag and drop useless for anything but the one directory already on
/// screen.
///
/// <para>
/// Driven by an explicit clock rather than a timer, so the delay can be tested without
/// waiting for it. The view calls <see cref="Update"/> on every drag-over event with the
/// current time, and navigates when it answers with a folder.
/// </para>
/// </remarks>
public sealed class SpringLoad
{
    /// <summary>How long the pointer must rest on a folder before it opens.</summary>
    /// <remarks>
    /// Long enough not to fire while crossing a folder on the way somewhere else, short
    /// enough not to feel stuck. The same 800 ms the Finder and Dolphin use.
    /// </remarks>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(800);

    private readonly TimeSpan _delay;
    private Location? _hovering;
    private DateTimeOffset _since;
    private bool _fired;

    public SpringLoad(TimeSpan? delay = null) => _delay = delay ?? DefaultDelay;

    /// <summary>What the pointer is currently resting on, if anything.</summary>
    public Location? Hovering => _hovering;

    /// <summary>
    /// Records where the pointer is, and answers with the folder to open — once.
    /// </summary>
    /// <param name="target">The folder under the pointer, or null for anywhere else.</param>
    /// <param name="now">The current time.</param>
    /// <remarks>
    /// Answering once per rest is what keeps a pointer left sitting on an already-opened
    /// folder from navigating into it again on every subsequent event. Moving away and back
    /// starts a new rest, and that one may fire.
    /// </remarks>
    public Location? Update(Location? target, DateTimeOffset now)
    {
        if (target != _hovering)
        {
            _hovering = target;
            _since = now;
            _fired = false;
            return null;
        }

        if (target is null || _fired || now - _since < _delay)
            return null;

        _fired = true;
        return target;
    }

    /// <summary>Forgets the current rest. Called when the drag leaves or ends.</summary>
    public void Reset()
    {
        _hovering = null;
        _fired = false;
    }
}
