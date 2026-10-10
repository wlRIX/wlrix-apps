// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Sockets;
using System.Text;
using Tmds.DBus.Protocol;

namespace Wlrix.ClipboardHistory.Services;

/// <summary>
/// The single-instance guard, over the session bus.
/// </summary>
/// <remarks>
/// The bus name is the identity: whoever owns <c>com.wlrix.clipboard-history</c> is the open
/// popup, and a second launch discovers that by failing to take it. The interface served is
/// <c>org.freedesktop.Application</c>, the freedesktop-standard one; the only caller is another
/// <c>wlrix-clipboard-history</c>, started by the same key that opened this one, and what it
/// asks for is read as "close" — which is what makes <c>Super+V</c> a toggle.
///
/// <para>
/// Copied from <c>Wlrix.Desks</c>, whose guard this is with the names changed. Hand-written
/// rather than generated, for the reason given there: the generator's handler mode wants an
/// introspection document of its own, and this is two methods that do the same thing.
/// </para>
/// </remarks>
public sealed class DBusInstanceGuard : IInstanceGuard, IPathMethodHandler
{
    /// <summary>The bus name that means "the popup is open".</summary>
    public const string BusName = Program.AppId;

    /// <summary>The object path, which the spec derives from the name.</summary>
    /// <remarks>Dots become slashes and the hyphen, which a path may not hold, an underscore.</remarks>
    public const string ObjectPath = "/com/wlrix/clipboard_history";

    private const string ApplicationInterface = "org.freedesktop.Application";

    private DBusConnection? _connection;

    /// <inheritdoc/>
    public string Path => ObjectPath;

    /// <inheritdoc/>
    /// <remarks>One object, no children. There is nothing below it to serve.</remarks>
    public bool HandlesChildPaths => false;

    /// <inheritdoc/>
    public event Action? ActivateRequested;

    /// <inheritdoc/>
    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        if (DBusAddress.Session is not { } address)
        {
            // No bus at all. The popup will say the history is unreachable; refusing to open it
            // would leave the key doing nothing, with nothing said.
            return true;
        }

        var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // TryRequestName, not QueueNameRequest: a second instance must find out at once
            // that it is second, rather than wait in line for a name it will never be given.
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

    /// <inheritdoc/>
    public async Task SendActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not { } connection)
            return;

        // Built in its own scope: MessageWriter is a ref struct over the connection's send
        // buffer and cannot be held across the await that sends it.
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
                member: "Activate",
                signature: "a{sv}");
            // The trailing a{sv} every method in this interface takes. Empty: the platform data
            // carries a startup-notification token and an activation environment, and this
            // application has nothing to say in either.
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
            context.ReplyIntrospectXml([Encoding.UTF8.GetBytes(IntrospectXml)], []);
            return ValueTask.CompletedTask;
        }

        if (request.InterfaceAsString != ApplicationInterface)
        {
            context.ReplyUnknownMethodError();
            return ValueTask.CompletedTask;
        }

        switch (request.MemberAsString)
        {
            case "Activate":
            // The popup opens nothing, so Open means the same as Activate: another press of the
            // key, which closes it. The URI array and the platform data are not read.
            case "Open":
                ActivateRequested?.Invoke();
                Reply(context);
                break;

            default:
                // ActivateAction included: the entry declares no actions to invoke.
                context.ReplyUnknownMethodError();
                break;
        }

        return ValueTask.CompletedTask;
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

    private static void Reply(MethodContext context)
    {
        if (context.NoReplyExpected)
            return;
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
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
