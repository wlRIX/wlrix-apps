using System.Security.Cryptography;
using System.Text;
using Wlrix.Files.Core.Remote;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>The record of which servers have proved who they are.</summary>
public sealed class KnownHostsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("wlrix-known-hosts-").FullName;

    private string Ssh => Path.Combine(_directory, "ssh_known_hosts");

    private string Own => Path.Combine(_directory, "own", "known_hosts");

    private KnownHosts Hosts => new([Ssh], Own);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>An SSH key blob: the length-prefixed type, then some bytes that differ per key.</summary>
    private static byte[] Blob(string type, byte seed)
    {
        var name = Encoding.ASCII.GetBytes(type);
        var blob = new byte[4 + name.Length + 8];
        blob[3] = (byte)name.Length;
        name.CopyTo(blob, 4);
        blob[^1] = seed;
        return blob;
    }

    private static ServerKey Key(string host = "nas.example", int port = 22, string type = "ssh-ed25519", byte seed = 1) =>
        ServerKey.ForSsh(host, port, Blob(type, seed));

    private static string Line(string hosts, ServerKey key) =>
        $"{hosts} {key.KeyType} {Convert.ToBase64String(key.Key)}";

    [Fact]
    public void AServerNeverSeenIsUnknown() =>
        Assert.Equal(ServerKeyStanding.Unknown, Hosts.Standing(Key()));

    [Fact]
    public void AKeyAcceptedWithSshIsKnown()
    {
        File.WriteAllText(Ssh, Line("nas.example,192.0.2.1", Key()) + " comment\n");
        Assert.Equal(ServerKeyStanding.Known, Hosts.Standing(Key()));
        Assert.Equal(ServerKeyStanding.Known, Hosts.Standing(Key("192.0.2.1")));
    }

    [Fact]
    public void ADifferentKeyOfTheSameTypeIsAChange()
    {
        File.WriteAllText(Ssh, Line("nas.example", Key()) + "\n");
        Assert.Equal(ServerKeyStanding.Changed, Hosts.Standing(Key(seed: 2)));
    }

    /// <summary>
    /// A server has several keys and which one is negotiated varies, so a key of another type is
    /// not evidence of anything. Calling it a change would teach people to click through.
    /// </summary>
    [Fact]
    public void AKeyOfAnotherTypeIsOnlyUnknown()
    {
        File.WriteAllText(Ssh, Line("nas.example", Key()) + "\n");
        Assert.Equal(ServerKeyStanding.Unknown, Hosts.Standing(Key(type: "ecdsa-sha2-nistp256")));
    }

    [Fact]
    public void AnotherPortIsAnotherServer()
    {
        File.WriteAllText(Ssh, Line("nas.example", Key()) + "\n");
        Assert.Equal(ServerKeyStanding.Unknown, Hosts.Standing(Key(port: 2222)));

        File.WriteAllText(Ssh, Line("[nas.example]:2222", Key()) + "\n");
        Assert.Equal(ServerKeyStanding.Known, Hosts.Standing(Key(port: 2222)));
    }

    [Fact]
    public void HashedHostNamesAreMatched()
    {
        var salt = new byte[20];
        salt[0] = 7;
        var hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes("nas.example"));
        var field = $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}";
        File.WriteAllText(Ssh, Line(field, Key()) + "\n");

        Assert.Equal(ServerKeyStanding.Known, Hosts.Standing(Key()));
        Assert.Equal(ServerKeyStanding.Unknown, Hosts.Standing(Key("other.example")));
    }

    [Fact]
    public void WildcardsAndNegationFollowOpenSsh()
    {
        File.WriteAllText(Ssh, Line("*.example,!secret.example", Key()) + "\n");
        Assert.Equal(ServerKeyStanding.Known, Hosts.Standing(Key("nas.example")));
        Assert.Equal(ServerKeyStanding.Unknown, Hosts.Standing(Key("secret.example")));
    }

    [Fact]
    public void ARevokedKeyStaysRevokedWhateverElseIsOnRecord()
    {
        File.WriteAllText(Ssh, $"@revoked {Line("nas.example", Key())}\n{Line("nas.example", Key())}\n");
        Assert.Equal(ServerKeyStanding.Revoked, Hosts.Standing(Key()));
    }

    [Fact]
    public void MalformedLinesAreSkipped()
    {
        File.WriteAllText(Ssh, $"# a comment\n\nnonsense\nnas.example ssh-ed25519 not*base64\n{Line("nas.example", Key())}\n");
        Assert.Equal(ServerKeyStanding.Known, Hosts.Standing(Key()));
    }

    /// <summary>Accepting a key must stop the question, and must not touch OpenSSH's own file.</summary>
    [Fact]
    public void TrustingAKeyRemembersItInTheFileOfOurOwn()
    {
        var before = Line("nas.example", Key()) + "\n";
        File.WriteAllText(Ssh, before);

        Hosts.Trust(Key(seed: 2));

        Assert.Equal(ServerKeyStanding.Known, Hosts.Standing(Key(seed: 2)));
        Assert.Equal(before, File.ReadAllText(Ssh));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Own));
    }

    [Fact]
    public void TrustingANewKeyRetiresTheOldOne()
    {
        Hosts.Trust(Key());
        Hosts.Trust(Key(seed: 2));

        Assert.Equal(ServerKeyStanding.Known, Hosts.Standing(Key(seed: 2)));
        Assert.Equal(ServerKeyStanding.Changed, Hosts.Standing(Key()));
        Assert.Single(File.ReadAllLines(Own));
    }

    /// <summary>The same spelling as `ssh-keygen -l`, so the two can be compared by eye.</summary>
    [Fact]
    public void TheFingerprintIsSpelledTheWayOpenSshSpellsIt()
    {
        var key = Key();
        var expected = Convert.ToBase64String(SHA256.HashData(key.Key)).TrimEnd('=');
        Assert.Equal($"SHA256:{expected}", key.Fingerprint);
    }

    /// <summary>The type comes from the blob, so an RSA key negotiated as rsa-sha2-512 is still ssh-rsa.</summary>
    [Fact]
    public void TheKeyTypeIsReadFromTheBlob() =>
        Assert.Equal("ssh-rsa", ServerKey.ForSsh("h", 22, Blob("ssh-rsa", 1)).KeyType);
}
