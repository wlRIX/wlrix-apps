namespace Wlrix.Files.Core.Remote;

/// <summary>
/// Which share a credential belongs to.
/// </summary>
/// <remarks>
/// The username is part of the identity, because one host can be reached as two different
/// people and folding those together would hand the wrong password to one of them. The path
/// below the share is not: a credential authenticates a connection, not a directory.
/// </remarks>
/// <param name="Scheme">smb, ftp or sftp.</param>
/// <param name="Host">The server.</param>
/// <param name="Port">The port, or −1 for the protocol's default.</param>
/// <param name="Share">The SMB share name. Empty for FTP and SFTP, which have no such concept.</param>
/// <param name="Username">Who is connecting. Empty means "not decided yet".</param>
public sealed record ShareRef(
    string Scheme,
    string Host,
    int Port = -1,
    string Share = "",
    string Username = "")
{
    /// <summary>The share a location belongs to.</summary>
    /// <remarks>
    /// The first path component is the share name for SMB and part of the path for everything
    /// else — SMB authenticates per share where FTP and SFTP authenticate per connection.
    /// </remarks>
    public static ShareRef For(Location location, string username = "")
    {
        var share = string.Equals(location.Scheme, "smb", StringComparison.Ordinal)
            ? location.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty
            : string.Empty;

        return new ShareRef(location.Scheme, location.Host ?? string.Empty, location.Port, share, username);
    }

    /// <summary>A stable string naming this share, for a keyring attribute or a log line.</summary>
    /// <remarks>
    /// Deliberately not a URI: this is an identifier, and something that looked like a URI
    /// would eventually be parsed as one and handed to <see cref="Location"/>, which strips
    /// the userinfo that is half the identity here.
    /// </remarks>
    public string Key =>
        $"{Scheme}|{Host}|{(Port < 0 ? string.Empty : Port.ToString(System.Globalization.CultureInfo.InvariantCulture))}|{Share}|{Username}";

    /// <summary>
    /// The same share without saying who is connecting.
    /// </summary>
    /// <remarks>
    /// A credential is saved under the username it belongs to, but the thing that later asks
    /// for one is a <i>navigation</i> — a URI, which by construction carries no username,
    /// because <see cref="Location"/> strips userinfo. Looking up by the full key would then
    /// never find what was just saved, and the connection would fall back to anonymous and be
    /// refused. This is the key that answers "whatever worked for this share last time".
    /// </remarks>
    public string ShareKey =>
        $"{Scheme}|{Host}|{(Port < 0 ? string.Empty : Port.ToString(System.Globalization.CultureInfo.InvariantCulture))}|{Share}";

    /// <summary>What the connect dialog shows above the password field.</summary>
    public string Describe => Share.Length > 0 ? $"{Scheme}://{Host}/{Share}" : $"{Scheme}://{Host}";
}

/// <summary>What is needed to log in.</summary>
/// <remarks>
/// A class holding a password, and it lives no longer than the connection it opens. It is
/// never written to <c>shares.json</c>, never included in a <see cref="Location"/>, and never
/// logged — the three places a credential leaks from in a file manager.
/// </remarks>
/// <param name="Username">Who. Ignored when <paramref name="Anonymous"/>.</param>
/// <param name="Password">The secret.</param>
/// <param name="Anonymous">
/// SMB guest, or FTP's <c>anonymous</c> account. <b>SFTP has no anonymous mode</b>, and the
/// connect dialog says so rather than offering a checkbox that can only fail.
/// </param>
public sealed record ShareCredentials(string Username, string Password, bool Anonymous = false)
{
    /// <summary>The anonymous login, for the protocols that have one.</summary>
    public static ShareCredentials Anonymously { get; } = new(string.Empty, string.Empty, Anonymous: true);

    /// <summary>Never the password. Guards against a credential reaching a log by accident.</summary>
    public override string ToString() => Anonymous ? "anonymous" : $"{Username}:<hidden>";
}

/// <summary>Where passwords come from, and whether they are remembered.</summary>
public interface ICredentialStore
{
    /// <summary>
    /// Whether a saved credential survives a restart.
    /// </summary>
    /// <remarks>
    /// The connect dialog asks so it can say. False means "Remember" holds the password until
    /// the application exits and no longer, and telling the user that plainly is the whole
    /// point of the property — a checkbox that silently forgets is worse than one that is
    /// honestly labeled.
    /// </remarks>
    bool IsPersistent { get; }

    Task<ShareCredentials?> GetAsync(ShareRef share, CancellationToken cancellationToken = default);

    /// <summary>Remembers a credential. Answers false if it could not be stored.</summary>
    Task<bool> SaveAsync(ShareRef share, ShareCredentials credentials, CancellationToken cancellationToken = default);

    Task RemoveAsync(ShareRef share, CancellationToken cancellationToken = default);
}

/// <summary>
/// Credentials held in memory for as long as the application runs.
/// </summary>
/// <remarks>
/// The fallback when there is no working keyring, and deliberately not a file. An obfuscated
/// password on disk is worse than this, because it looks safe: anybody who can read the file
/// can read the password, and the user was told it was stored securely. Forgetting on exit is
/// a limitation somebody can understand and work around.
/// </remarks>
public sealed class TransientCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, ShareCredentials> _held = new(StringComparer.Ordinal);

    // Which username was last used for each share, so a lookup that does not name one finds
    // the credential that worked rather than nothing.
    private readonly Dictionary<string, string> _lastUsed = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public bool IsPersistent => false;

    /// <inheritdoc/>
    /// <inheritdoc/>
    /// <remarks>
    /// Falls back to the last credential used for the share when the caller does not name a
    /// username. That is the ordinary case rather than an edge one: what asks for a credential
    /// is a navigation to a URI, and a URI never carries a username.
    /// </remarks>
    public Task<ShareCredentials?> GetAsync(ShareRef share, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_held.TryGetValue(share.Key, out var exact))
                return Task.FromResult<ShareCredentials?>(exact);

            if (share.Username.Length == 0
                && _lastUsed.TryGetValue(share.ShareKey, out var key)
                && _held.TryGetValue(key, out var recent))
            {
                return Task.FromResult<ShareCredentials?>(recent);
            }
            return Task.FromResult<ShareCredentials?>(null);
        }
    }

    /// <inheritdoc/>
    public Task<bool> SaveAsync(ShareRef share, ShareCredentials credentials, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _held[share.Key] = credentials;
            _lastUsed[share.ShareKey] = share.Key;
        }
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task RemoveAsync(ShareRef share, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _held.Remove(share.Key);
            // Only if it is still the one being pointed at, or removing an old credential
            // would unstick a newer one.
            if (_lastUsed.TryGetValue(share.ShareKey, out var key) && key == share.Key)
                _lastUsed.Remove(share.ShareKey);
        }
        return Task.CompletedTask;
    }
}
