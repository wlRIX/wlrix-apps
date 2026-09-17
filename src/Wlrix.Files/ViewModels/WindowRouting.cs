using Wlrix.Files.Core;
using Wlrix.Files.Core.State;

namespace Wlrix.Files.ViewModels;

/// <summary>What the user asked for when they opened a directory.</summary>
/// <remarks>
/// The gesture, not the outcome. What it turns into depends on the navigation mode, and
/// keeping the two apart is what lets Classic and Modern be one policy rather than two code
/// paths through the whole application.
/// </remarks>
public enum OpenIntent
{
    /// <summary>A plain double-click, with nothing held.</summary>
    Default,
    NewTab,
    NewWindow
}

/// <summary>What actually happens.</summary>
public enum OpenTarget
{
    InPlace,
    NewTab,
    NewWindow
}

/// <summary>The one place Classic and Modern differ.</summary>
public static class NavigationPolicy
{
    /// <summary>Reads the intent off the gesture.</summary>
    /// <remarks>
    /// The same in both modes, deliberately: only the unmodified case changes with the mode,
    /// so a user who learns Control for a tab keeps it when they switch.
    /// </remarks>
    public static OpenIntent IntentFor(bool control, bool shift, bool middleButton)
    {
        // Middle-click first: it is the least ambiguous of the three, and a middle-click with
        // Shift held by accident should still be the tab the user was reaching for.
        if (middleButton || control)
            return OpenIntent.NewTab;
        return shift ? OpenIntent.NewWindow : OpenIntent.Default;
    }

    /// <summary>What an intent means under a mode.</summary>
    public static OpenTarget Decide(OpenIntent intent, NavigationMode mode) => intent switch
    {
        OpenIntent.NewTab => OpenTarget.NewTab,
        OpenIntent.NewWindow => OpenTarget.NewWindow,
        // The whole of the difference between the two modes, in one line: IRIX's fm opened a
        // window per directory, Dolphin navigates the one you are in.
        _ => mode == NavigationMode.Classic ? OpenTarget.NewWindow : OpenTarget.InPlace
    };
}

/// <summary>
/// What a window view model needs of the application's window management.
/// </summary>
/// <remarks>
/// The narrow face of <c>WindowManager</c>, so a view model depends on three methods rather
/// than on the object that constructs Avalonia windows — which would make it untestable for
/// the sake of one reference.
/// </remarks>
public interface IWindowRouting<TWindow> where TWindow : class
{
    /// <summary>Opens a directory the way the mode and the gesture ask for.</summary>
    void Open(Location location, OpenIntent intent, TWindow from);

    /// <summary>
    /// Asks whether this window may navigate to a location, taking the key if it may.
    /// Answers the window to raise instead, if Classic mode would rather raise one.
    /// </summary>
    TWindow? Claim(TWindow window, Location location);

    /// <summary>
    /// Brings the registry back in step after a navigation that was not asked about —
    /// Back, Forward, Up, or a tab switch.
    /// </summary>
    /// <remarks>
    /// These move the active tab without going through <see cref="Claim"/>, because raising a
    /// different window in answer to Back would be a strange thing for a Back button to do.
    /// The key still has to follow, or the registry would claim this window shows a directory
    /// it has navigated away from.
    /// </remarks>
    void Resync(TWindow window, Location location);

    /// <summary>Brings a window to the front.</summary>
    void Present(TWindow window);
}

/// <summary>What the router needs a window to be able to do.</summary>
/// <remarks>
/// An interface, and generic over the window type, so the routing rules can be tested against
/// a recording fake with no display, no compositor and no Avalonia. The real implementation is
/// four one-line methods, one of which is <c>Show(); Activate();</c> — and <c>Activate</c> is
/// the whole reason the Avalonia fork grew <c>xdg_activation_v1</c> support.
/// </remarks>
public interface IWindowPresenter<TWindow> where TWindow : class
{
    /// <summary>Builds a window showing <paramref name="location"/> and shows it.</summary>
    TWindow Create(Location location);

    /// <summary>Raises an existing window, without stealing keyboard focus.</summary>
    void Present(TWindow window);

    /// <summary>Points the window's active tab somewhere else.</summary>
    void NavigateInPlace(TWindow window, Location location);

    /// <summary>Adds a tab and makes it current.</summary>
    void AddTab(TWindow window, Location location);
}

/// <summary>
/// Which window is showing which directory.
/// </summary>
/// <remarks>
/// This is what makes Classic mode's "raise the window that already has it" possible, and it
/// is an ordinary in-process dictionary rather than anything cleverer because the single
/// instance guard guarantees there is only ever one process to ask.
///
/// <para>
/// A window may be tracked without a key. Two windows can genuinely show one directory —
/// Modern mode allows it, and so does a Classic session that had two windows before a
/// navigation brought them together — so the key belongs to whichever got there first and the
/// other simply has none until it moves.
/// </para>
/// </remarks>
public sealed class WindowRegistry<TWindow> where TWindow : class
{
    private readonly Dictionary<Location, TWindow> _byLocation = [];
    private readonly Dictionary<TWindow, Location> _keys = [];
    private readonly List<TWindow> _windows = [];

    public int Count => _windows.Count;

    /// <summary>Every window, in the order they were opened.</summary>
    public IReadOnlyList<TWindow> Windows => _windows;

    /// <summary>The window showing <paramref name="location"/>, if one holds that key.</summary>
    public bool TryGet(Location location, out TWindow window) =>
        _byLocation.TryGetValue(location, out window!);

    /// <summary>What this window is keyed by, or null if another window holds its location.</summary>
    public Location? KeyOf(TWindow window) => _keys.TryGetValue(window, out var key) ? key : null;

    /// <summary>Starts tracking a window, keyed by <paramref name="location"/> if it is free.</summary>
    public void Add(TWindow window, Location location)
    {
        if (!_windows.Contains(window))
            _windows.Add(window);
        Rekey(window, location);
    }

    /// <summary>
    /// Moves a window's key, because its active tab went somewhere else.
    /// </summary>
    /// <returns>
    /// False if another window already holds <paramref name="location"/>. The caller is
    /// expected to raise that one rather than let two windows claim one directory.
    /// </returns>
    public bool Rekey(TWindow window, Location location)
    {
        if (_byLocation.TryGetValue(location, out var holder) && !ReferenceEquals(holder, window))
            return false;

        if (_keys.TryGetValue(window, out var old))
            _byLocation.Remove(old);

        _keys[window] = location;
        _byLocation[location] = window;
        return true;
    }

    /// <summary>Gives up a window's key without forgetting the window.</summary>
    /// <remarks>
    /// What happens when a window navigates somewhere another window already holds. Leaving
    /// the old key in place instead would leave the registry claiming this window shows a
    /// directory it has navigated away from.
    /// </remarks>
    public void Unkey(TWindow window)
    {
        if (_keys.Remove(window, out var key))
            _byLocation.Remove(key);
    }

    /// <summary>Stops tracking a closed window.</summary>
    public void Forget(TWindow window)
    {
        if (_keys.Remove(window, out var key))
            _byLocation.Remove(key);
        _windows.Remove(window);
    }
}

/// <summary>
/// Turns "open this directory" into a window, a tab, or a navigation.
/// </summary>
/// <remarks>
/// Every route through Classic and Modern mode goes through <see cref="Open"/>, which is the
/// point: the two modes are a policy object rather than a branch repeated at each call site.
/// </remarks>
public sealed class WindowRouter<TWindow>(
    IWindowPresenter<TWindow> presenter,
    Func<NavigationMode> mode)
    where TWindow : class
{
    public WindowRegistry<TWindow> Registry { get; } = new();

    public NavigationMode Mode => mode();

    /// <summary>Opens <paramref name="location"/> the way the mode and the gesture ask for.</summary>
    /// <param name="current">The window the request came from, or null at startup.</param>
    public void Open(Location location, OpenIntent intent, TWindow? current)
    {
        switch (NavigationPolicy.Decide(intent, Mode))
        {
            case OpenTarget.NewWindow:
                OpenWindow(location);
                break;

            case OpenTarget.NewTab when current is not null:
                presenter.AddTab(current, location);
                break;

            // A tab with no window to put it in is a window.
            case OpenTarget.NewTab:
                OpenWindow(location);
                break;

            default:
                NavigateInPlace(location, current);
                break;
        }
    }

    /// <summary>Registers a window that was created outside the router, such as the first one.</summary>
    public void Track(TWindow window, Location location) => Registry.Add(window, location);

    public void Forget(TWindow window) => Registry.Forget(window);

    /// <summary>Raises a window. Exposed so a caller can raise whatever blocked it.</summary>
    public void Present(TWindow window) => presenter.Present(window);

    /// <summary>
    /// Asks whether <paramref name="window"/> may go to <paramref name="location"/>, and
    /// takes the key if it may.
    /// </summary>
    /// <returns>
    /// Null to go ahead. Otherwise the window that already shows that directory, which in
    /// Classic mode comes forward while this one stays where it is — the IRIX behavior. The
    /// caller asks <b>before</b> navigating, because "leave this one alone" is not something
    /// that can be done afterwards.
    /// </returns>
    /// <remarks>
    /// Modern mode always says yes: two windows on one directory is ordinary there. The
    /// second simply holds no key until it moves somewhere free, rather than keeping a stale
    /// one that would claim it still shows where it came from.
    /// </remarks>
    public TWindow? TryClaim(TWindow window, Location location)
    {
        if (Registry.Rekey(window, location))
            return null;

        Registry.TryGet(location, out var holder);
        if (Mode == NavigationMode.Classic)
            return holder;

        Registry.Unkey(window);
        return null;
    }

    private void OpenWindow(Location location)
    {
        // Raising rather than opening a second window is the Classic behavior, and it is what
        // the whole registry exists for. In Modern mode a Shift-click asked for a new window
        // in so many words, and answering it by raising a different one would be a refusal.
        if (Mode == NavigationMode.Classic && Registry.TryGet(location, out var existing))
        {
            presenter.Present(existing);
            return;
        }

        Registry.Add(presenter.Create(location), location);
    }

    private void NavigateInPlace(Location location, TWindow? current)
    {
        if (current is null)
        {
            OpenWindow(location);
            return;
        }

        if (TryClaim(current, location) is { } holder)
        {
            presenter.Present(holder);
            return;
        }

        presenter.NavigateInPlace(current, location);
    }
}
