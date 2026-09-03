using Avalonia;
using Avalonia.Threading;
using Wlrix.Avalonia;
using Wlrix.Settings.Client;

namespace Wlrix.Theme;

/// <summary>
/// Keeps an application drawing in the session's color scheme, and keeps it there.
/// </summary>
/// <remarks>
/// <para>
/// One line in an app's <c>OnFrameworkInitializationCompleted</c> is the whole of it. Without
/// this an app renders in whatever scheme <c>WlrixTheme</c> ships with, which is right until
/// somebody changes the session's — and then the desktop, the window chrome and the tray are
/// one color and every application window is another.
/// </para>
/// <para>
/// The scheme is read from <c>com.wlrix.Settings</c> rather than from a config file, for the
/// same reason a settings panel writes through it: <c>wlrix-apps</c> has no TOML parser, and the
/// scheme lives in four files rather than one. <c>appearance.palette</c> is the daemon's fan-out
/// key for exactly that, so this asks for one key and is told when it moves.
/// </para>
/// <para>
/// Everything here fails soft. A session with no settings service is a complete session — the
/// daemon is a writer and a notifier, never a source of truth — so an app that cannot reach it
/// keeps the theme's default and says so in its own log if it wants to, rather than refusing to
/// start over a color.
/// </para>
/// </remarks>
public sealed class SessionScheme : IDisposable
{
    /// <summary>The daemon key that carries the scheme for the whole desktop.</summary>
    public const string Key = "appearance.palette";

    /// <summary>
    /// The live follower, so the garbage collector cannot take it.
    /// </summary>
    /// <remarks>
    /// Callers discard the result — following the session scheme is not something an app should
    /// have to hold a field for. Without this, nothing outside would reference the object: the
    /// only chain back to it runs through the event handler its own client holds, which is a
    /// cycle and collectable as one. It would work for a while and then stop, at whichever
    /// collection happened to sweep it, which is the worst shape a bug can have.
    /// </remarks>
    private static SessionScheme? _live;

    private readonly Application _application;
    private SettingsClient? _client;
    private bool _disposed;

    private SessionScheme(Application application) => _application = application;

    /// <summary>
    /// Adopt the session's scheme and follow it until the process ends or this is disposed.
    /// </summary>
    /// <returns>
    /// The subscription, or <c>null</c> if there is no settings service or the application has
    /// no <see cref="WlrixTheme"/> to restyle. Both are ordinary situations, not failures — and
    /// the result does not have to be kept, since a live follower holds itself.
    /// </returns>
    /// <remarks>
    /// Call it first thing in <c>OnFrameworkInitializationCompleted</c> and do not await it:
    /// reaching the bus takes longer than building a window, and the scheme arriving a moment
    /// after the first paint is a repaint, not a flash of the wrong window. Awaiting it would
    /// delay the window instead.
    /// </remarks>
    public static async Task<SessionScheme?> FollowAsync(Application? application)
    {
        if (application is null || WlrixTheme.From(application) is null)
            return null;

        var follower = new SessionScheme(application);
        try
        {
            await follower.StartAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Bus activation means "not on the bus" is a broken install rather than an idle
            // session — but a color is not worth failing a launch over either way.
            follower.Dispose();
            return null;
        }

        // Replacing rather than adding: one application, one scheme, and a second call should
        // not leave the first still listening.
        Interlocked.Exchange(ref _live, follower)?.Dispose();
        return follower;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Interlocked.CompareExchange(ref _live, null, this);

        if (_client is not null)
        {
            _client.Changed -= OnChanged;
            _client.Dispose();
            _client = null;
        }
    }

    private async Task StartAsync()
    {
        _client = await SettingsClient.ConnectAsync().ConfigureAwait(false);
        Adopt(await _client.GetAsync(Key).ConfigureAwait(false));

        await _client.WatchAsync().ConfigureAwait(false);
        _client.Changed += OnChanged;
    }

    /// <summary>
    /// Somebody changed the scheme — this app, another app, or a person with an editor open.
    /// </summary>
    /// <remarks>
    /// The echo of this app's own write is deliberately <em>not</em> dropped. A settings panel
    /// filters on <c>Origin</c> so it does not fight its own debounce timer; here there is
    /// nothing to fight, and dropping it would leave the one app that changed the scheme as the
    /// only one still drawing in the old one.
    /// </remarks>
    private void OnChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (!e.Values.TryGetValue(Key, out var value))
            return;

        // Raised on a bus thread, and this repaints every window in the process.
        Dispatcher.UIThread.Post(() => Adopt(value));
    }

    private void Adopt(object? value)
    {
        // An empty string is what the daemon answers for a key nothing sets, which means "the
        // default" rather than "no scheme" — and WlrixTheme.Scheme reads it the same way.
        if (value is not string id || _disposed)
            return;
        if (WlrixTheme.From(_application) is { } theme)
            theme.Scheme = id;
    }
}
