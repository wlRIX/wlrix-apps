using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;
using ZLogger;

namespace Wlrix.PolkitAgent.Services;

/// <summary>One <c>BeginAuthentication</c>: what polkit wants authenticated, and for whom.</summary>
/// <param name="ActionId">The polkit action, e.g. <c>com.wlrix.settings.login.configure</c>.</param>
/// <param name="Message">The action's own message, already in the session's language.</param>
/// <param name="Details">What the caller attached: for pkexec, the program and its command line.</param>
/// <param name="Cookie">Identifies this request to the helper and to <c>CancelAuthentication</c>.</param>
internal sealed record AgentRequest(
    string ActionId,
    string Message,
    string IconName,
    IReadOnlyDictionary<string, string> Details,
    string Cookie,
    IReadOnlyList<PolkitIdentity> Identities)
{
    /// <summary>Reads the arguments of <c>BeginAuthentication</c>: <c>sssa{ss}sa(sa{sv})</c>.</summary>
    public static AgentRequest Read(ref Reader reader)
    {
        var actionId = reader.ReadString();
        var message = reader.ReadString();
        var iconName = reader.ReadString();

        var details = new Dictionary<string, string>();
        var end = reader.ReadDictionaryStart();
        while (reader.HasNext(end))
        {
            reader.AlignStruct();
            var key = reader.ReadString();
            details[key] = reader.ReadString();
        }

        var cookie = reader.ReadString();
        var identities = Services.Identities.Read(ref reader);
        return new AgentRequest(actionId, message, iconName, details, cookie, identities);
    }
}

/// <summary>Whatever puts the question to the person: the dialog, or a test double.</summary>
internal interface IAuthenticationPresenter
{
    /// <summary>
    /// Asks for a password until it is right or the person gives up. True when polkit has been
    /// told the request is authenticated; false when it was canceled, by the person or by
    /// <paramref name="cancellationToken"/> (polkit's <c>CancelAuthentication</c>).
    /// </summary>
    Task<bool> AuthenticateAsync(AgentRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The <c>org.freedesktop.PolicyKit1.AuthenticationAgent</c> object polkitd calls.
///
/// <c>BeginAuthentication</c> is answered only when the attempt is over, which may be minutes
/// later, so each call's context is kept past the handler (<see cref="MethodContext.DisposesAsynchronously"/>)
/// and the bus goes on reading -- it has to, or a <c>CancelAuthentication</c> for the dialog on
/// screen could never arrive. Requests are put to the person one at a time, in order.
/// </summary>
internal sealed class AuthenticationAgent(IAuthenticationPresenter presenter, ILogger<AuthenticationAgent> logger)
    : IPathMethodHandler
{
    public const string ObjectPath = "/com/wlrix/PolkitAgent";
    private const string AgentInterface = "org.freedesktop.PolicyKit1.AuthenticationAgent";
    private const string CancelledError = "org.freedesktop.PolicyKit1.Error.Cancelled";

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new();

    public string Path => ObjectPath;

    public bool HandlesChildPaths => false;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([System.Text.Encoding.UTF8.GetBytes(IntrospectXml)], []);
            return ValueTask.CompletedTask;
        }

        var request = context.Request;
        if (request.InterfaceAsString != AgentInterface)
        {
            context.ReplyUnknownMethodError();
            return ValueTask.CompletedTask;
        }

        switch (request.MemberAsString)
        {
            case "BeginAuthentication":
                AgentRequest begin;
                try
                {
                    var reader = request.GetBodyReader();
                    begin = AgentRequest.Read(ref reader);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", ex.Message);
                    return ValueTask.CompletedTask;
                }

                context.DisposesAsynchronously = true;
                _ = BeginAsync(context, begin);
                break;

            case "CancelAuthentication":
                var cookie = request.GetBodyReader().ReadString();
                Cancel(cookie);
                Reply(context);
                break;

            default:
                context.ReplyUnknownMethodError();
                break;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Ends the request with this cookie: its dialog closes, and polkit is told it was canceled.</summary>
    internal void Cancel(string cookie)
    {
        if (_pending.TryGetValue(cookie, out var cancel))
            cancel.Cancel();
    }

    private async Task BeginAsync(MethodContext context, AgentRequest request)
    {
        using var cancel = new CancellationTokenSource();
        _pending[request.Cookie] = cancel;
        try
        {
            var authenticated = await RunAsync(request, cancel.Token).ConfigureAwait(false);
            if (context.NoReplyExpected)
                return;
            if (authenticated)
                Reply(context);
            else
                context.ReplyError(CancelledError, "Authentication was canceled.");
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"Authentication for {request.ActionId} failed unexpectedly.");
            if (!context.NoReplyExpected)
                context.ReplyError("org.freedesktop.PolicyKit1.Error.Failed", ex.Message);
        }
        finally
        {
            _pending.TryRemove(request.Cookie, out _);
            context.Dispose();
        }
    }

    /// <summary>Waits its turn, then puts <paramref name="request"/> to the person.</summary>
    internal async Task<bool> RunAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            logger.ZLogInformation($"Authentication requested for {request.ActionId}.");
            return await presenter.AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private static void Reply(MethodContext context)
    {
        if (context.NoReplyExpected)
            return;
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    private const string IntrospectXml = """
        <interface name="org.freedesktop.PolicyKit1.AuthenticationAgent">
          <method name="BeginAuthentication">
            <arg type="s" name="action_id" direction="in"/>
            <arg type="s" name="message" direction="in"/>
            <arg type="s" name="icon_name" direction="in"/>
            <arg type="a{ss}" name="details" direction="in"/>
            <arg type="s" name="cookie" direction="in"/>
            <arg type="a(sa{sv})" name="identities" direction="in"/>
          </method>
          <method name="CancelAuthentication">
            <arg type="s" name="cookie" direction="in"/>
          </method>
        </interface>
        """;
}
