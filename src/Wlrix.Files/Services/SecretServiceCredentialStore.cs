using System.Globalization;
using System.Net.Sockets;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;
using Wlrix.Files.Core.Remote;
using Wlrix.Files.DBus;
using ZLogger;

namespace Wlrix.Files.Services;

/// <summary>
/// Share passwords in the session keyring, so "Remember" means past this run.
/// </summary>
/// <remarks>
/// <para>
/// The plan flagged this as an integration hole and guessed wrong about it. It supposed that
/// the default collection would be locked with nothing able to unlock it, because
/// <c>wlrix-session</c> does not start <c>gnome-keyring-daemon</c>. It does not need to:
/// <c>gnome-keyring-daemon.service</c> is a systemd <b>user</b> unit wanted by
/// <c>default.target</c>, so it runs whatever desktop is on top — and
/// <c>wlrix-greeter</c>'s own PAM stack already carries <c>pam_gnome_keyring.so</c> on both
/// <c>auth</c> and <c>session</c>, which unlocks the login keyring with the password the user
/// just typed. Measured on a live wlRIX session: the login collection is unlocked.
/// </para>
/// <para>
/// So a locked collection is the unusual case rather than the expected one, and this declines
/// it rather than driving a prompt: unlocking means handing <c>gcr-prompter</c> a window handle
/// to be modal to, and Avalonia cannot export one a foreign toolkit could import. The
/// application falls back to <see cref="TransientCredentialStore"/> and the connect dialog says
/// plainly that the password will not be remembered — which is the behavior that shipped in M7,
/// now reached only when it is true.
/// </para>
/// <para>
/// <b>Items are written in GNOME's network-password schema</b>, so a password saved here is one
/// Nautilus or gvfs can find, and theirs is one this can find. Same reasoning as the thumbnail
/// cache: the freedesktop standard is only worth following if it is followed exactly enough
/// that two implementations meet. Lookups deliberately ask for <i>fewer</i> attributes than
/// they write, because Secret Service matches an item whose attributes are a superset of the
/// query — so another application's extra attributes cannot hide its item from us.
/// </para>
/// </remarks>
public sealed class SecretServiceCredentialStore : ICredentialStore, IDisposable
{
    private const string BusName = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";

    /// <summary>The path a method answers with when it needs nothing from the user.</summary>
    private const string NoPrompt = "/";

    /// <summary>libsecret's compatibility schema for a network password.</summary>
    /// <remarks>
    /// The attribute every GNOME item carries to say what shape the rest of them are. Without
    /// it a search would match items belonging to entirely different applications.
    /// </remarks>
    private const string SchemaAttribute = "xdg:schema";
    private const string Schema = "org.gnome.keyring.NetworkPassword";

    private readonly DBusConnection _connection;
    private readonly ObjectPath _collection;
    private readonly ObjectPath _session;
    private readonly byte[] _key;
    private readonly ILogger _logger;

    private SecretServiceCredentialStore(
        DBusConnection connection, ObjectPath collection, ObjectPath session, byte[] key, ILogger logger)
    {
        _connection = connection;
        _collection = collection;
        _session = session;
        _key = key;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool IsPersistent => true;

    /// <summary>
    /// Opens the keyring, or answers null if it cannot be used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blocking, and called once when the first window asks whether passwords can be
    /// remembered. That question has to be answered before the connect dialog is drawn, because
    /// the dialog's whole honesty rests on it — and the work behind it is three round trips to a
    /// daemon on a Unix socket. The same shape as the instance guard, which blocks on the bus
    /// in <c>Program.Main</c> before Avalonia starts.
    /// </para>
    /// <para>
    /// The timeout is the load-bearing part. A keyring that is wedged rather than absent would
    /// otherwise hang the first window rather than the connect dialog, with nothing on screen to
    /// say why.
    /// </para>
    /// </remarks>
    public static ICredentialStore? TryOpen(ILogger logger, TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
        try
        {
            return OpenAsync(logger, deadline.Token).GetAwaiter().GetResult();
        }
        // DBusExceptionBase rather than a list of its subclasses, and that is the whole lesson
        // of this catch: a bus that cannot be reached at all throws DBusConnectFailedException,
        // which is not an error *reply* and was not in the list the first version enumerated.
        // It went unhandled out of a DI factory and took the application down before its first
        // window -- found by deliberately pointing the app at a bus that was not there, which
        // is the case this whole method exists to survive.
        catch (Exception ex) when (ex is DBusExceptionBase or SocketException or IOException
                                       or InvalidOperationException or OperationCanceledException
                                       or ObjectDisposedException)
        {
            logger.ZLogInformation($"no usable keyring ({ex.GetType().Name}); passwords will be held in memory only");
            return null;
        }
    }

    private static async Task<ICredentialStore?> OpenAsync(ILogger logger, CancellationToken cancellationToken)
    {
        var address = DBusAddress.Session ?? throw new InvalidOperationException("no session bus address");
        var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
        var keep = false;
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var service = new Service(connection, BusName, new ObjectPath(ServicePath));

            // The default alias rather than "login" by name: which collection is the default is
            // the keyring's business, and a machine can have several.
            var collection = await service.ReadAliasAsync("default").ConfigureAwait(false);
            if (collection.ToString() == NoPrompt)
            {
                logger.ZLogInformation($"the keyring has no default collection; passwords will be held in memory only");
                return null;
            }

            // Asked before anything is stored, not after a save has failed. A locked collection
            // needs a prompt this application has no way to put on screen, so the honest answer
            // is to say now that passwords will not be remembered.
            if (await new Collection(connection, BusName, collection).GetLockedAsync().ConfigureAwait(false))
            {
                logger.ZLogInformation($"the default keyring collection is locked; passwords will be held in memory only");
                return null;
            }

            var privateKey = SecretCrypto.NewPrivateKey();
            var (output, session) = await service
                .OpenSessionAsync(SecretCrypto.Algorithm, VariantValue.Array(SecretCrypto.PublicKey(privateKey)))
                .ConfigureAwait(false);

            var key = SecretCrypto.SharedKey(privateKey, output.GetArray<byte>());
            logger.ZLogInformation($"keyring ready; share passwords will be remembered");
            keep = true;
            return new SecretServiceCredentialStore(connection, collection, session, key, logger);
        }
        finally
        {
            if (!keep)
                connection.Dispose();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Two searches rather than one, for the reason the transient store gives: what asks for a
    /// credential is a navigation to a URI, and a URI carries no username because
    /// <c>Location</c> strips userinfo. The exact search answers "this account on this share";
    /// the relaxed one answers "whatever worked for this share last time".
    /// </remarks>
    public async Task<ShareCredentials?> GetAsync(ShareRef share, CancellationToken cancellationToken = default)
    {
        try
        {
            var service = new Service(_connection, BusName, new ObjectPath(ServicePath));

            var found = await FindAsync(service, Attributes(share, exact: true)).ConfigureAwait(false)
                        ?? await FindAsync(service, Attributes(share, exact: false)).ConfigureAwait(false);
            if (found is not { } path)
                return null;

            var item = new Item(_connection, BusName, path);
            var (_, parameters, value, _) = await item.GetSecretAsync(_session).ConfigureAwait(false);

            // The username comes back off the item rather than out of the request, which is the
            // whole point of the relaxed search: the caller did not know it.
            var attributes = await item.GetAttributesAsync().ConfigureAwait(false);
            var username = attributes.GetValueOrDefault("user", share.Username);

            return new ShareCredentials(username, SecretCrypto.Decrypt(_key, parameters, value));
        }
        catch (Exception ex) when (IsBusFailure(ex))
        {
            _logger.ZLogWarning($"could not read a password from the keyring: {ex.GetType().Name}");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SaveAsync(
        ShareRef share, ShareCredentials credentials, CancellationToken cancellationToken = default)
    {
        // Nothing to keep, and an anonymous login is not a secret. Storing an empty password
        // under this share would also shadow a real one saved later.
        if (credentials.Anonymous || credentials.Password.Length == 0)
            return false;

        try
        {
            var attributes = Attributes(share with { Username = credentials.Username }, exact: true);
            var (parameters, value) = SecretCrypto.Encrypt(_key, credentials.Password);

            var properties = new Dictionary<string, VariantValue>(StringComparer.Ordinal)
            {
                ["org.freedesktop.Secret.Item.Label"] = share.Describe,
                ["org.freedesktop.Secret.Item.Attributes"] =
                    new Dict<string, string>(attributes).AsVariantValue(),
            };

            var collection = new Collection(_connection, BusName, _collection);
            // replace: true, so connecting again with a new password updates the item rather
            // than leaving two that match the same search and answering with whichever came
            // back first.
            var (_, prompt) = await collection
                .CreateItemAsync(properties, (_session, parameters, value, "text/plain"), replace: true)
                .ConfigureAwait(false);

            if (prompt.ToString() != NoPrompt)
            {
                _logger.ZLogWarning($"the keyring wants to ask something before saving; not saving");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (IsBusFailure(ex))
        {
            _logger.ZLogWarning($"could not save a password to the keyring: {ex.GetType().Name}");
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(ShareRef share, CancellationToken cancellationToken = default)
    {
        try
        {
            var service = new Service(_connection, BusName, new ObjectPath(ServicePath));
            if (await FindAsync(service, Attributes(share, exact: true)).ConfigureAwait(false) is not { } path)
                return;

            await new Item(_connection, BusName, path).DeleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBusFailure(ex))
        {
            _logger.ZLogWarning($"could not remove a password from the keyring: {ex.GetType().Name}");
        }
    }

    /// <summary>The first unlocked item matching, or null.</summary>
    /// <remarks>
    /// Locked items are ignored rather than unlocked, for the reason the whole store declines a
    /// locked collection: reading one means a prompt there is no way to put on screen.
    /// </remarks>
    private static async Task<ObjectPath?> FindAsync(Service service, Dictionary<string, string> attributes)
    {
        var (unlocked, _) = await service.SearchItemsAsync(attributes).ConfigureAwait(false);
        // The cast is needed: ObjectPath is a struct, so the conditional has no common type
        // between it and the null without being told which nullable to produce.
        return unlocked.Length > 0 ? unlocked[0] : (ObjectPath?)null;
    }

    /// <summary>
    /// The item's attributes, in GNOME's network-password schema.
    /// </summary>
    /// <param name="share">Which share.</param>
    /// <param name="exact">
    /// Whether to name the account and the share as well as the server. False is the relaxed
    /// search — <b>fewer</b> attributes match <b>more</b> items, because Secret Service matches
    /// a superset. That asymmetry is what makes this interoperate: an item written by another
    /// application with attributes we never heard of is still found, so long as it carries
    /// these.
    /// </param>
    internal static Dictionary<string, string> Attributes(ShareRef share, bool exact)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SchemaAttribute] = Schema,
            ["protocol"] = share.Scheme,
            ["server"] = share.Host,
        };

        if (share.Username.Length > 0)
            attributes["user"] = share.Username;

        if (exact)
        {
            // The share is part of the identity for SMB, which authenticates per share. Left
            // out of the relaxed search on purpose: an item saved for one share of a server is
            // usually the right password for another.
            if (share.Share.Length > 0)
                attributes["object"] = share.Share;

            // Only when the caller named one. A port written here when the protocol's default
            // was used would be an attribute nobody else sets, and the item would stop matching
            // anyone's search but our own.
            if (share.Port >= 0)
                attributes["port"] = share.Port.ToString(CultureInfo.InvariantCulture);
        }

        return attributes;
    }

    /// <summary>Whether an exception is the keyring being unreachable rather than a defect here.</summary>
    private static bool IsBusFailure(Exception ex) =>
        // The base of the whole family, not the error-reply leaf: the keyring going away
        // mid-operation throws a connection exception, not a reply.
        ex is DBusExceptionBase or SocketException or IOException
            or InvalidOperationException or ObjectDisposedException
            // A wrong key, or an item another application encrypted for a session of its own.
            or System.Security.Cryptography.CryptographicException
            or ArgumentException;

    public void Dispose() => _connection.Dispose();
}
