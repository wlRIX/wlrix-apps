namespace Wlrix.Files.Core.Remote;

/// <summary>
/// Turns a remote location into a mounted filesystem, asking for a password if it needs one.
/// </summary>
/// <remarks>
/// One instance per scheme, registered with <see cref="FileSystemProvider"/>. The provider
/// caches by mount key and calls this once per server, so this is also where a connection is
/// first authenticated — which is why it holds the credential store and the prompt rather than
/// the session doing so.
/// </remarks>
public sealed class RemoteFileSystemFactory(
    string scheme,
    ICredentialStore credentials,
    ICredentialPrompt? prompt = null,
    RemoteOptions options = default) : IFileSystemFactory
{
    /// <summary>The three schemes this can serve.</summary>
    public static IReadOnlyList<string> Schemes { get; } = ["smb", "sftp", "ftp"];

    /// <inheritdoc/>
    public string Scheme { get; } = scheme;

    /// <inheritdoc/>
    public async Task<IFileSystem> CreateAsync(Location location, CancellationToken cancellationToken)
    {
        var share = ShareRef.For(location);
        var known = await credentials.GetAsync(share, cancellationToken).ConfigureAwait(false);

        // Anonymous is tried first only where it is a real login. SFTP has none, so asking
        // there would be a guaranteed round trip to a refusal.
        var attempt = known ?? (SupportsAnonymous ? ShareCredentials.Anonymously : null);
        if (attempt is null)
        {
            attempt = await AskAsync(share, cancellationToken).ConfigureAwait(false)
                      ?? throw new FileOperationException(FileErrorKind.AccessDenied, location, "no credentials");
        }

        while (true)
        {
            var mounted = new RemoteFileSystem(location.MountKey, CapabilitiesOf(attempt),
                () => Session(location, attempt!), options);
            try
            {
                // Proved before the mount is handed out, so a wrong password is a dialog now
                // rather than an error on the first directory the user clicks.
                await mounted.ExistsAsync(Location.Parse($"{location.MountKey}/{share.Share}"), cancellationToken)
                    .ConfigureAwait(false);
                return mounted;
            }
            catch (FileOperationException ex) when (ex.Kind == FileErrorKind.AccessDenied)
            {
                await mounted.DisposeAsync().ConfigureAwait(false);
                // Asked again with the refusal in hand, which is how a guest-denied share
                // turns into a username prompt rather than a dead end.
                attempt = await AskAsync(share, cancellationToken, ex.Message).ConfigureAwait(false);
                if (attempt is null)
                    throw;
            }
            catch
            {
                await mounted.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>Whether this protocol has an anonymous login worth trying first.</summary>
    /// <remarks>
    /// SMB has guest and FTP has the <c>anonymous</c> account. SFTP runs inside an
    /// authenticated SSH session and has neither.
    /// </remarks>
    private bool SupportsAnonymous => !string.Equals(Scheme, "sftp", StringComparison.Ordinal);

    private async Task<ShareCredentials?> AskAsync(ShareRef share, CancellationToken cancellationToken, string? why = null)
    {
        if (prompt is null)
            return null;

        var answer = await prompt.AskAsync(share, why, credentials.IsPersistent, cancellationToken).ConfigureAwait(false);
        if (answer is null)
            return null;
        if (answer.Remember)
        {
            await credentials.SaveAsync(share with { Username = answer.Credentials.Username },
                answer.Credentials, cancellationToken).ConfigureAwait(false);
        }
        return answer.Credentials;
    }

    private IRemoteSession Session(Location location, ShareCredentials with) => Scheme switch
    {
        "smb" => new SmbSession(location.Host ?? string.Empty, location.Port, with),
        "sftp" => new SftpSession(location.Host ?? string.Empty, location.Port, with),
        "ftp" => new FtpSession(location.Host ?? string.Empty, location.Port, with),
        _ => throw new FileOperationException(FileErrorKind.Unsupported, location, $"no backend for '{Scheme}'")
    };

    /// <summary>
    /// What the protocol can do, before anything has connected.
    /// </summary>
    /// <remarks>
    /// Asked of a throwaway session rather than hard-coded here, so the answer lives beside
    /// the implementation that has to honor it. The operations engine needs this before the
    /// first connection, to decide whether a move can be a rename.
    /// </remarks>
    private FileSystemCapabilities CapabilitiesOf(ShareCredentials with) =>
        Session(Location.Parse($"{Scheme}://placeholder/"), with).Capabilities;
}

/// <summary>Asks the user for a password.</summary>
/// <remarks>
/// An interface in Core with the implementation in the app, for the usual reason: Core must
/// not know a dialog exists, and CI must be able to mount a fake share without one.
/// </remarks>
public interface ICredentialPrompt
{
    /// <summary>
    /// Asks for credentials for a share. Answers null if the user canceled.
    /// </summary>
    /// <param name="share">What is being connected to.</param>
    /// <param name="why">Why the last attempt failed, or null on the first ask.</param>
    /// <param name="canRemember">
    /// Whether saving would survive a restart. False means the dialog must say so rather than
    /// offer a Remember box that quietly forgets.
    /// </param>
    Task<CredentialAnswer?> AskAsync(ShareRef share, string? why, bool canRemember, CancellationToken cancellationToken);
}

/// <summary>What the user typed.</summary>
public sealed record CredentialAnswer(ShareCredentials Credentials, bool Remember);
