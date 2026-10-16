using System.Globalization;
using Tmds.DBus.Protocol;

namespace Wlrix.PolkitAgent.Services;

/// <summary>How registering with polkitd went.</summary>
internal enum RegistrationResult
{
    Registered,

    /// <summary>The session already has an agent. Not an error: there is nothing for this one to do.</summary>
    AlreadyRegistered,
}

/// <summary>
/// Tells polkitd that this process answers its authentication requests for this login session.
///
/// Registration is per session (<c>unix-session</c> subject): polkitd then sends every
/// <c>auth_admin</c> question that comes from a process in this session to our object, over the
/// system bus. One agent per session, so a second copy is refused, and says so rather than
/// failing.
/// </summary>
internal sealed class AgentRegistration(DBusConnection connection) : IAsyncDisposable
{
    private const string Destination = "org.freedesktop.PolicyKit1";
    private const string AuthorityPath = "/org/freedesktop/PolicyKit1/Authority";
    private const string AuthorityInterface = "org.freedesktop.PolicyKit1.Authority";

    private string? _sessionId;

    public async Task<RegistrationResult> RegisterAsync(string sessionId)
    {
        try
        {
            await connection.CallMethodAsync(Build("RegisterAuthenticationAgent", sessionId, register: true))
                .ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex) when (ex.ErrorMessage.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return RegistrationResult.AlreadyRegistered;
        }

        _sessionId = sessionId;
        return RegistrationResult.Registered;
    }

    /// <summary>
    /// The login session this process belongs to. <c>XDG_SESSION_ID</c> when pam_systemd set it,
    /// which it does for every greetd login; otherwise logind is asked which session our pid is in.
    /// </summary>
    public async Task<string> SessionIdAsync()
    {
        if (Environment.GetEnvironmentVariable("XDG_SESSION_ID") is { Length: > 0 } id)
            return id;

        var path = await connection.CallMethodAsync(BuildGetSession(), static (message, _) =>
            message.GetBodyReader().ReadObjectPathAsString()).ConfigureAwait(false);
        return await connection.CallMethodAsync(BuildGetId(path), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            return reader.ReadVariantValue().GetString();
        }).ConfigureAwait(false);
    }

    private MessageBuffer BuildGetSession()
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader("org.freedesktop.login1", "/org/freedesktop/login1",
            "org.freedesktop.login1.Manager", "GetSessionByPID", "u");
        writer.WriteUInt32((uint)Environment.ProcessId);
        return writer.CreateMessage();
    }

    private MessageBuffer BuildGetId(string sessionPath)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader("org.freedesktop.login1", sessionPath,
            "org.freedesktop.DBus.Properties", "Get", "ss");
        writer.WriteString("org.freedesktop.login1.Session");
        writer.WriteString("Id");
        return writer.CreateMessage();
    }

    /// <summary>
    /// <c>RegisterAuthenticationAgent((sa{sv}) subject, s locale, s object_path)</c>, or
    /// <c>UnregisterAuthenticationAgent((sa{sv}) subject, s object_path)</c>.
    /// </summary>
    private MessageBuffer Build(string member, string sessionId, bool register)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Destination, AuthorityPath, AuthorityInterface, member,
            register ? "(sa{sv})ss" : "(sa{sv})s");
        writer.WriteStructureStart();
        writer.WriteString("unix-session");
        writer.WriteDictionary(new Dictionary<string, VariantValue> { ["session-id"] = VariantValue.String(sessionId) });
        if (register)
            writer.WriteString(Locale());
        writer.WriteString(AuthenticationAgent.ObjectPath);
        return writer.CreateMessage();
    }

    /// <summary>
    /// The locale polkitd should write the action messages in, as a POSIX name (<c>ja_JP.UTF-8</c>).
    /// The environment's own, when it has one, since that is what the session runs in.
    /// </summary>
    private static string Locale() =>
        Environment.GetEnvironmentVariable("LC_ALL") is { Length: > 0 } all ? all
        : Environment.GetEnvironmentVariable("LC_MESSAGES") is { Length: > 0 } messages ? messages
        : Environment.GetEnvironmentVariable("LANG") is { Length: > 0 } lang ? lang
        : CultureInfo.CurrentUICulture.Name.Replace('-', '_') + ".UTF-8";

    public async ValueTask DisposeAsync()
    {
        if (_sessionId is not { } sessionId)
            return;
        _sessionId = null;
        try
        {
            await connection.CallMethodAsync(Build("UnregisterAuthenticationAgent", sessionId, register: false))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or ObjectDisposedException)
        {
            // polkitd forgets an agent whose connection goes away anyway.
        }
    }
}
