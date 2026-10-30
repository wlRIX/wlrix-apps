using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Wlrix.Common;

namespace Wlrix.Files.Core.Remote;

/// <summary>What a server presented to prove who it is: an SSH host key or a TLS certificate.</summary>
/// <param name="Host">The name it was reached by, which is what a key is remembered under.</param>
/// <param name="Port">The port it was reached on.</param>
/// <param name="KeyType"><c>ssh-ed25519</c> and so on, or <see cref="CertificateType"/>.</param>
/// <param name="Key">The public key blob, or the certificate's SHA-256 hash.</param>
/// <param name="DefaultPort">The port that needs no spelling out in a known-hosts line.</param>
public sealed record ServerKey(string Host, int Port, string KeyType, byte[] Key, int DefaultPort)
{
    /// <summary>The key type a pinned certificate is recorded under, which OpenSSH would skip.</summary>
    public const string CertificateType = "x509-sha256";

    /// <summary>The name a known-hosts line uses: <c>host</c>, or <c>[host]:port</c> off the default port.</summary>
    public string HostEntry => Port == DefaultPort ? Host.ToLowerInvariant() : $"[{Host.ToLowerInvariant()}]:{Port}";

    /// <summary>The fingerprint, spelled as <c>ssh-keygen -l</c> and OpenSSH's own prompt spell it.</summary>
    public string Fingerprint =>
        $"SHA256:{Convert.ToBase64String(KeyType == CertificateType ? Key : SHA256.HashData(Key)).TrimEnd('=')}";

    /// <summary>The server as a person would write it.</summary>
    public string Server => Port == DefaultPort ? Host : $"{Host}:{Port}";

    /// <summary>An SSH host key, its type read from the blob itself.</summary>
    /// <remarks>
    /// Not from the name the library negotiated: that can be <c>rsa-sha2-512</c> for a key
    /// recorded as <c>ssh-rsa</c>, and the same key would then look like a different one.
    /// </remarks>
    public static ServerKey ForSsh(string host, int port, byte[] blob)
    {
        var type = string.Empty;
        if (blob.Length >= 4)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(blob);
            if (length > 0 && length <= blob.Length - 4)
                type = Encoding.ASCII.GetString(blob, 4, length);
        }

        return new ServerKey(host, port, type, blob, SftpSession.DefaultPort);
    }

    /// <summary>A TLS certificate, pinned by hash.</summary>
    public static ServerKey ForCertificate(string host, int port, X509Certificate certificate) =>
        new(host, port, CertificateType, certificate.GetCertHash(HashAlgorithmName.SHA256), FtpSession.DefaultPort);
}

/// <summary>What is known about a key a server presented.</summary>
public enum ServerKeyStanding
{
    /// <summary>This very key is on record for this server.</summary>
    Known,

    /// <summary>Nothing of this type is on record for this server.</summary>
    Unknown,

    /// <summary>A different key of this type is on record, which is what interception looks like.</summary>
    Changed,

    /// <summary>The key is marked <c>@revoked</c>. Not a question anybody gets to answer.</summary>
    Revoked,
}

/// <summary>A server refused because its key is not on record; nothing secret has been sent.</summary>
public sealed class UntrustedServerKeyException(ServerKey key, ServerKeyStanding standing)
    : Exception(standing == ServerKeyStanding.Changed
        ? $"the identity of {key.Server} has changed ({key.Fingerprint})"
        : $"the identity of {key.Server} is not trusted ({key.Fingerprint})")
{
    public ServerKey Key { get; } = key;

    public ServerKeyStanding Standing { get; } = standing;
}

/// <summary>Asks the user whether to trust a server.</summary>
/// <remarks>In Core with the implementation in the app, for the same reason as <see cref="ICredentialPrompt"/>.</remarks>
public interface IServerTrustPrompt
{
    /// <summary>Answers true if the user wants to connect and have the key remembered.</summary>
    Task<bool> AskAsync(ServerKey key, ServerKeyStanding standing, CancellationToken cancellationToken);
}

/// <summary>Which servers' keys have been seen, in OpenSSH's <c>known_hosts</c> format.</summary>
/// <remarks>
/// Reads the user's <c>~/.ssh/known_hosts</c>, so a server they have already accepted with
/// <c>ssh</c> asks nothing here, but never writes it: that file is OpenSSH's, and what somebody
/// accepts in a file manager should not change what <c>ssh</c> trusts. Acceptances go to a file of
/// this application's own, which is consulted first.
/// </remarks>
public sealed class KnownHosts(IReadOnlyList<string> readOnlyFiles, string ownFile)
{
    private readonly Lock _gate = new();

    /// <summary>The user's real files.</summary>
    public static KnownHosts ForUser() => new(
        [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts")],
        Path.Combine(ApplicationPaths.AppData, "files", "known_hosts"));

    /// <summary>What is on record about <paramref name="key"/>.</summary>
    public ServerKeyStanding Standing(ServerKey key)
    {
        var standing = ServerKeyStanding.Unknown;
        lock (_gate)
        {
            foreach (var file in readOnlyFiles.Prepend(ownFile))
            {
                foreach (var entry in Read(file))
                {
                    if (!entry.Matches(key.Host, key.Port, key.DefaultPort))
                        continue;
                    var same = entry.Key is { } recorded && recorded.AsSpan().SequenceEqual(key.Key);
                    if (entry.Revoked)
                    {
                        if (same)
                            return ServerKeyStanding.Revoked;
                        continue;
                    }

                    if (same)
                        standing = ServerKeyStanding.Known;
                    else if (entry.KeyType == key.KeyType && standing != ServerKeyStanding.Known)
                        standing = ServerKeyStanding.Changed;
                }
            }
        }

        return standing;
    }

    /// <summary>Records <paramref name="key"/>, in place of any other of its type for this server.</summary>
    public void Trust(ServerKey key)
    {
        lock (_gate)
        {
            var kept = new List<string>();
            if (File.Exists(ownFile))
            {
                kept.AddRange(File.ReadAllLines(ownFile).Where(line =>
                    Entry.Parse(line) is not { } entry
                    || entry.KeyType != key.KeyType
                    || !entry.Matches(key.Host, key.Port, key.DefaultPort)));
            }

            kept.Add($"{key.HostEntry} {key.KeyType} {Convert.ToBase64String(key.Key)}");

            var directory = Path.GetDirectoryName(ownFile)!;
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var temporary = $"{ownFile}.{Environment.ProcessId}.tmp";
            using (var stream = new FileStream(temporary, new FileStreamOptions
                   {
                       Mode = FileMode.Create,
                       Access = FileAccess.Write,
                       UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                   }))
            using (var writer = new StreamWriter(stream))
            {
                foreach (var line in kept)
                    writer.Write(line + "\n");
            }

            File.Move(temporary, ownFile, overwrite: true);
        }
    }

    private static IEnumerable<Entry> Read(string file)
    {
        string[] lines;
        try
        {
            if (!File.Exists(file))
                yield break;
            lines = File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable file knows nothing, which makes the server unknown and the user asked.
            yield break;
        }

        foreach (var line in lines)
        {
            if (Entry.Parse(line) is { } entry)
                yield return entry;
        }
    }

    /// <summary>One line: <c>[@marker] hosts type base64 [comment]</c>.</summary>
    private sealed record Entry(string Hosts, string KeyType, byte[]? Key, bool Revoked)
    {
        public static Entry? Parse(string line)
        {
            var fields = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || fields[0].StartsWith('#'))
                return null;

            var revoked = false;
            if (fields[0].StartsWith('@'))
            {
                // @cert-authority vouches for certificates, which this does not check.
                if (fields[0] != "@revoked")
                    return null;
                revoked = true;
                fields = fields[1..];
            }

            if (fields.Length < 3)
                return null;

            byte[]? key;
            try
            {
                key = Convert.FromBase64String(fields[2]);
            }
            catch (FormatException)
            {
                key = null;
            }

            return new Entry(fields[0], fields[1], key, revoked);
        }

        /// <summary>Whether this line is about the server, by OpenSSH's rules for its host field.</summary>
        public bool Matches(string host, int port, int defaultPort)
        {
            host = host.ToLowerInvariant();
            var name = port == defaultPort ? host : $"[{host}]:{port}";

            if (Hosts.StartsWith("|1|", StringComparison.Ordinal))
                return MatchesHashed(Hosts, name);

            var matched = false;
            foreach (var pattern in Hosts.Split(','))
            {
                var negated = pattern.StartsWith('!');
                if (!Glob(negated ? pattern[1..] : pattern, name))
                    continue;
                if (negated)
                    return false;
                matched = true;
            }

            return matched;
        }

        private static bool MatchesHashed(string field, string name)
        {
            var parts = field.Split('|');
            if (parts.Length != 4)
                return false;
            try
            {
                var hash = HMACSHA1.HashData(Convert.FromBase64String(parts[2]), Encoding.UTF8.GetBytes(name));
                return hash.AsSpan().SequenceEqual(Convert.FromBase64String(parts[3]));
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary><c>*</c> and <c>?</c>, the only wildcards the format has.</summary>
        private static bool Glob(string pattern, string text)
        {
            pattern = pattern.ToLowerInvariant();
            int p = 0, t = 0, star = -1, resume = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    resume = t;
                }
                else if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
                {
                    p++;
                    t++;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    t = ++resume;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == '*')
                p++;
            return p == pattern.Length;
        }
    }
}
