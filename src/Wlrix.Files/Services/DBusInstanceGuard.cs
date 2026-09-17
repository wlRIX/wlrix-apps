using System.Net.Sockets;
using Tmds.DBus.Protocol;
using Wlrix.Files.Core;

namespace Wlrix.Files.Services;

/// <summary>
/// The single-instance guard, over the session bus.
/// </summary>
/// <remarks>
/// The bus name is the identity: whoever owns <c>com.wlrix.files</c> is the file manager, and
/// a second launch discovers that by failing to take it. The interface served is
/// <c>org.freedesktop.Application</c>, the freedesktop-standard one, so this is also what a
/// future <c>DBusActivatable=true</c> entry would need — though the desktop entry deliberately
/// does not set that yet, because <c>wlrix-desktop</c> launches from <c>Exec=</c> anyway.
///
/// <para>
/// Hand-written rather than generated. The generator's handler mode wants an introspection
/// document and an <c>AllowUnsafeBlocks</c> csproj, and this is three methods of which two are
/// one line — the whole thing is smaller than the plumbing to generate it.
/// </para>
/// </remarks>
public sealed class DBusInstanceGuard : IInstanceGuard, IPathMethodHandler
{
    /// <summary>The bus name that means "the file manager".</summary>
    public const string BusName = "com.wlrix.files";

    /// <summary>The object path, which the spec derives from the name.</summary>
    public const string ObjectPath = "/com/wlrix/files";

    private const string ApplicationInterface = "org.freedesktop.Application";

    private DBusConnection? _connection;

    /// <inheritdoc/>
    public string Path => ObjectPath;

    /// <inheritdoc/>
    /// <remarks>One object, no children. There is nothing below it to serve.</remarks>
    public bool HandlesChildPaths => false;

    /// <inheritdoc/>
    public event Action<IReadOnlyList<string>>? OpenRequested;

    /// <inheritdoc/>
    public event Action<string>? ActionRequested;

    /// <inheritdoc/>
    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        if (DBusAddress.Session is not { } address)
        {
            // No bus at all. Refusing to start a file manager over that would be a far worse
            // failure than the two windows the guard exists to prevent.
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
    public async Task SendOpenAsync(IReadOnlyList<string> uris, CancellationToken cancellationToken = default)
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
                member: uris.Count > 0 ? "Open" : "Activate",
                signature: uris.Count > 0 ? "asa{sv}" : "a{sv}");
            if (uris.Count > 0)
                writer.WriteArray(uris);
            // The trailing a{sv} every method in this interface takes. Empty: the platform data
            // carries a startup-notification token and an activation environment, and this
            // application has nothing to say in either.
            writer.WriteDictionary(new Dictionary<string, VariantValue>());
            return writer.CreateMessage();
        }
    }

    /// <inheritdoc/>
    public async Task SendActionAsync(string action, CancellationToken cancellationToken = default)
    {
        if (_connection is not { } connection)
            return;

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
                member: "ActivateAction",
                signature: "sava{sv}");
            writer.WriteString(action);
            // No parameters. The one action the entry declares takes none, and an empty av is
            // still a required argument rather than something that can be left out.
            var parameters = writer.WriteArrayStart(DBusType.Variant);
            writer.WriteArrayEnd(parameters);
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
                var reader = request.GetBodyReader();
                // The platform data that follows is deliberately not read. It carries a
                // startup-notification token and an activation environment, neither of which
                // this application has anything to do with.
                OpenRequested?.Invoke(reader.ReadArrayOfString());
                Reply(context);
                break;

            case "Activate":
                // No URIs: come forward showing whatever you already have.
                OpenRequested?.Invoke([]);
                Reply(context);
                break;

            case "ActivateAction":
                ActionRequested?.Invoke(request.GetBodyReader().ReadString());
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

    public async ValueTask DisposeAsync()
    {
        if (_connection is { } connection)
        {
            _connection = null;
            connection.Dispose();
        }
        await ValueTask.CompletedTask.ConfigureAwait(false);
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
          <method name="ActivateAction">
            <arg type="s" name="action_name" direction="in"/>
            <arg type="av" name="parameter" direction="in"/>
            <arg type="a{sv}" name="platform_data" direction="in"/>
          </method>
        </interface>
        """;
}
