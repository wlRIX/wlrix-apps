// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Sockets;
using Tmds.DBus.Protocol;

namespace Wlrix.Writer.Services;

/// <summary>
/// The single-instance guard, over the session bus.
/// </summary>
/// <remarks>
/// The bus name is the identity: whoever owns <c>com.wlrix.writer</c> is the editor, and a second
/// launch discovers that by failing to take it. The interface served is
/// <c>org.freedesktop.Application</c>, the freedesktop-standard one.
///
/// <para>
/// Wlrix.Files' guard, less the <c>ActivateAction</c> half: the editor's desktop entry declares no
/// actions. A fourth copy of this class in the repository is the point at which it should move
/// into a shared library.
/// </para>
/// </remarks>
public sealed class DBusInstanceGuard : IPathMethodHandler, IAsyncDisposable
{
    /// <summary>The bus name that means "the text editor".</summary>
    public const string BusName = "com.wlrix.writer";

    /// <summary>The object path, which the spec derives from the name.</summary>
    public const string ObjectPath = "/com/wlrix/writer";

    private const string ApplicationInterface = "org.freedesktop.Application";

    private DBusConnection? _connection;

    /// <inheritdoc/>
    public string Path => ObjectPath;

    /// <inheritdoc/>
    public bool HandlesChildPaths => false;

    /// <summary>
    /// Raised on the primary when another launch asks for files to be opened, or for the window
    /// to come forward (an empty list).
    /// </summary>
    /// <remarks>Arrives on a bus thread; the subscriber marshals it to the UI.</remarks>
    public event Action<IReadOnlyList<string>>? OpenRequested;

    /// <summary>Tries to become the one instance.</summary>
    /// <returns>
    /// False if another process already is, in which case all that is left to do is
    /// <see cref="SendOpenAsync"/> and exit. True with no session bus at all: refusing to start
    /// an editor over that would be far worse than the second window this exists to prevent.
    /// </returns>
    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        if (DBusAddress.Session is not { } address)
            return true;

        var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // TryRequestName, not QueueNameRequest: a second instance must find out at once that
            // it is second, rather than wait in line for a name it will never be given.
            if (!await connection.TryRequestNameAsync(BusName, RequestNameOptions.None).ConfigureAwait(false))
            {
                _connection = connection;
                return false;
            }

            connection.AddMethodHandler(this);
            _connection = connection;
            return true;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or SocketException)
        {
            // A bus that is there but will not talk is the same situation as no bus.
            connection.Dispose();
            return true;
        }
    }

    /// <summary>Asks the primary to open <paramref name="uris"/>, or just to come forward.</summary>
    public async Task SendOpenAsync(IReadOnlyList<string> uris, CancellationToken cancellationToken = default)
    {
        if (_connection is not { } connection)
            return;

        // Built in its own scope: MessageWriter is a ref struct over the connection's send buffer
        // and cannot be held across the await that sends it.
        var message = Build();
        await connection.CallMethodAsync(message).ConfigureAwait(false);
        return;

        MessageBuffer Build()
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: BusName,
                path: ObjectPath,
                @interface: ApplicationInterface,
                member: uris.Count > 0 ? "Open" : "Activate",
                signature: uris.Count > 0 ? "asa{sv}" : "a{sv}");
            if (uris.Count > 0)
                writer.WriteArray(uris);
            // The trailing platform data every method in this interface takes. Empty: it carries
            // a startup-notification token and an activation environment, and this application
            // has nothing to say in either.
            writer.WriteDictionary(new Dictionary<string, VariantValue>());
            return writer.CreateMessage();
        }
    }

    /// <inheritdoc/>
    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([System.Text.Encoding.UTF8.GetBytes(IntrospectXml)], []);
            return ValueTask.CompletedTask;
        }

        if (request.InterfaceAsString != ApplicationInterface)
        {
            context.ReplyUnknownMethodError();
            return ValueTask.CompletedTask;
        }

        switch (request.MemberAsString)
        {
            case "Open":
                OpenRequested?.Invoke(request.GetBodyReader().ReadArrayOfString());
                Reply(context);
                break;

            case "Activate":
                OpenRequested?.Invoke([]);
                Reply(context);
                break;

            default:
                context.ReplyUnknownMethodError();
                break;
        }

        return ValueTask.CompletedTask;
    }

    private static void Reply(MethodContext context)
    {
        if (context.NoReplyExpected)
            return;
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    public ValueTask DisposeAsync()
    {
        if (_connection is { } connection)
        {
            _connection = null;
            connection.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private const string IntrospectXml = """
        <interface name="org.freedesktop.Application">
          <method name="Activate">
            <arg type="a{sv}" name="platform_data" direction="in"/>
          </method>
          <method name="Open">
            <arg type="as" name="uris" direction="in"/>
            <arg type="a{sv}" name="platform_data" direction="in"/>
          </method>
        </interface>
        """;
}
