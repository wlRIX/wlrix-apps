// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Sockets;
using System.Text;

namespace Wlrix.Common;

/// <summary>
/// Asks <c>wlrix-sound</c> to play one of the desktop's sounds.
/// </summary>
/// <remarks>
/// <para>
/// The same request the Rust components send (<c>wlrix-sound/src/client.rs</c>): one datagram,
/// <c>play &lt;name&gt;</c>, to <c>$XDG_RUNTIME_DIR/wlrix-sound.sock</c>. Kept in step with that
/// file by hand. Not the daemon's D-Bus interface, because this is called on the UI thread in the
/// middle of a drop or a launch, and a datagram to a local socket returns at once whatever the
/// daemon is doing.
/// </para>
/// <para>
/// Every failure is silence and nothing else. With no daemon running the desktop is quiet, not
/// broken. Whether a sound actually plays (the theme, the master switch, the muted list) is the
/// daemon's decision, made from <c>sounds.toml</c>; a caller asks for every sound it has a reason
/// to and never reads that file.
/// </para>
/// </remarks>
public static class SystemSound
{
    /// <summary>The daemon's socket, relative to <c>$XDG_RUNTIME_DIR</c>.</summary>
    public const string SocketName = "wlrix-sound.sock";

    private static readonly Lock Gate = new();
    private static Socket? _socket;
    private static UnixDomainSocketEndPoint? _endpoint;
    private static bool _unavailable;

    /// <summary>
    /// Plays the sound called <paramref name="name"/>: a freedesktop sound name such as
    /// <c>dialog-error</c>, or one of wlRIX's own such as <c>wlrix-launch</c>. Returns at once;
    /// never throws.
    /// </summary>
    public static void Play(string name)
    {
        try
        {
            lock (Gate)
            {
                if (!Open())
                    return;
                _socket!.SendTo(Encoding.UTF8.GetBytes("play " + name), _endpoint!);
            }
        }
        catch (SocketException)
        {
            // No daemon, or a full queue: either way, this sound is not heard.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// The sound for a dialog showing one of <c>Wlrix.Avalonia.Dialogs</c>' icons -- <c>Error</c>,
    /// <c>Critical</c>, <c>Warning</c>, <c>Question</c>, <c>Information</c> -- for an app's own
    /// dialog that borrows the icon, so it sounds the way a <c>MessageDialog</c> of that type does.
    /// </summary>
    public static string ForDialogIcon(string icon) => icon switch
    {
        "Error" => "dialog-error",
        "Critical" => "dialog-error-serious",
        "Warning" => "dialog-warning",
        "Question" => "dialog-question",
        _ => "dialog-information",
    };

    /// <summary>The socket, made on first use. False when there is nowhere to send to.</summary>
    private static bool Open()
    {
        if (_socket is not null)
            return true;
        if (_unavailable)
            return false;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime) || !Path.IsPathRooted(runtime))
        {
            // No runtime directory is not going to change while this process runs.
            _unavailable = true;
            return false;
        }

        _endpoint = new UnixDomainSocketEndPoint(Path.Combine(runtime, SocketName));
        _socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified)
        {
            Blocking = false,
        };
        return true;
    }
}
