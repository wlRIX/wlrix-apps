using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Wlrix.Files.Core.Remote;
using Wlrix.Files.Services;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>
/// The Secret Service session algorithm, which is the part of the keyring store that can be
/// wrong quietly.
/// </summary>
/// <remarks>
/// A wrong key does not throw; it stores a password the daemon decrypts into rubbish, and the
/// failure surfaces days later as "my saved password stopped working". Verified live against
/// gnome-keyring and <c>secret-tool</c> as well — these pin the pieces so a later edit cannot
/// undo that without saying so.
/// </remarks>
public class SecretCryptoTests
{
    /// <summary>
    /// Both sides of a Diffie-Hellman exchange reach the same AES key.
    /// </summary>
    /// <remarks>
    /// The whole algorithm in one assertion: if the prime, the generator, the byte order or the
    /// derivation is wrong on either side, these differ.
    /// </remarks>
    [Fact]
    public void TwoSidesAgreeOnAKey()
    {
        var ours = SecretCrypto.NewPrivateKey();
        var theirs = SecretCrypto.NewPrivateKey();

        var mine = SecretCrypto.SharedKey(ours, SecretCrypto.PublicKey(theirs));
        var yours = SecretCrypto.SharedKey(theirs, SecretCrypto.PublicKey(ours));

        Assert.Equal(mine, yours);
        // AES-128, as the algorithm's own name says.
        Assert.Equal(16, mine.Length);
    }

    [Fact]
    public void TwoExchangesDoNotProduceTheSameKey()
    {
        var theirs = SecretCrypto.PublicKey(SecretCrypto.NewPrivateKey());

        Assert.NotEqual(
            SecretCrypto.SharedKey(SecretCrypto.NewPrivateKey(), theirs),
            SecretCrypto.SharedKey(SecretCrypto.NewPrivateKey(), theirs));
    }

    /// <summary>
    /// A public key is always the full width of the modulus.
    /// </summary>
    /// <remarks>
    /// The defect this guards is intermittent and therefore nasty: BigInteger drops leading
    /// zeros, so roughly one exchange in 256 would send 127 bytes, the daemon would hash a
    /// different buffer, and the session would fail at random. A hundred keys is enough to meet
    /// the case a third of the time; the direct check below is what actually pins it.
    /// </remarks>
    [Fact]
    public void EveryPublicKeyIsTheModulusWidth()
    {
        for (var i = 0; i < 100; i++)
            Assert.Equal(SecretCrypto.KeyLength, SecretCrypto.PublicKey(SecretCrypto.NewPrivateKey()).Length);
    }

    [Fact]
    public void ASmallValueIsLeftPaddedRatherThanShortened()
    {
        var wire = SecretCrypto.ToWire(new BigInteger(0x1234));

        Assert.Equal(SecretCrypto.KeyLength, wire.Length);
        // Big-endian and right-aligned: the value is at the end, zeros before it.
        Assert.Equal(0x12, wire[^2]);
        Assert.Equal(0x34, wire[^1]);
        Assert.All(wire[..^2], b => Assert.Equal(0, b));
    }

    /// <summary>
    /// Every digit of the group's prime, pinned.
    /// </summary>
    /// <remarks>
    /// The constant cannot be derived cheaply and a single mistyped digit would leave an
    /// algorithm that still looks like Diffie-Hellman and agrees with nobody. The hash is of the
    /// value that was verified live against gnome-keyring, so it pins that rather than merely
    /// pinning whatever is written above it today.
    /// </remarks>
    [Fact]
    public void ThePrimeIsTheOneTheAlgorithmNames()
    {
        var digest = Convert.ToHexString(SHA256.HashData(SecretCrypto.PrimeBytes));

        Assert.Equal(
            "3F35A3F5F6C4376A744ACAD409BB22F8D897F949D2311D885ADAA890981B67A0",
            digest);
        Assert.Equal(SecretCrypto.KeyLength, SecretCrypto.PrimeBytes.Length);
        // RFC 2409's group has its top and bottom 64 bits all ones.
        Assert.All(SecretCrypto.PrimeBytes[..8], b => Assert.Equal(0xFF, b));
        Assert.All(SecretCrypto.PrimeBytes[^8..], b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void ASecretSurvivesTheRoundTrip()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        const string secret = "hunter2";

        var (parameters, value) = SecretCrypto.Encrypt(key, secret);

        Assert.Equal(secret, SecretCrypto.Decrypt(key, parameters, value));
    }

    /// <summary>A password is not ASCII, and the encoding is part of the contract.</summary>
    [Fact]
    public void ANonAsciiSecretSurvivesTheRoundTrip()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        const string secret = "秘密のパスワード ünïcode";

        var (parameters, value) = SecretCrypto.Encrypt(key, secret);

        Assert.Equal(secret, SecretCrypto.Decrypt(key, parameters, value));
        // Stored as UTF-8, which is what the item's text/plain content type promises.
        Assert.Equal(Encoding.UTF8.GetByteCount(secret), Encoding.UTF8.GetBytes(secret).Length);
    }

    [Fact]
    public void EachEncryptionGetsItsOwnInitializationVector()
    {
        var key = RandomNumberGenerator.GetBytes(16);

        var (first, firstValue) = SecretCrypto.Encrypt(key, "same");
        var (second, secondValue) = SecretCrypto.Encrypt(key, "same");

        Assert.Equal(16, first.Length);
        Assert.NotEqual(first, second);
        // And therefore the same password does not produce the same bytes twice, which is the
        // reason the IV is there at all.
        Assert.NotEqual(firstValue, secondValue);
    }

    [Fact]
    public void TheWrongKeyDoesNotQuietlyProduceAPassword()
    {
        var (parameters, value) = SecretCrypto.Encrypt(RandomNumberGenerator.GetBytes(16), "hunter2");

        // Padding validation is what catches it. The store treats that as the keyring being
        // unreadable rather than as a defect, because an item another application encrypted for
        // a session of its own lands here too.
        Assert.ThrowsAny<CryptographicException>(
            () => SecretCrypto.Decrypt(RandomNumberGenerator.GetBytes(16), parameters, value));
    }
}

/// <summary>
/// The attributes an item carries, which are the whole of whether another application can find
/// what wlRIX saved and the other way round.
/// </summary>
public class SecretAttributeTests
{
    private static readonly ShareRef Share = new("smb", "server", Port: -1, Share: "docs", Username: "vic");

    [Fact]
    public void EveryItemCarriesTheSchemaAndTheServer()
    {
        var exact = SecretServiceCredentialStore.Attributes(Share, exact: true);

        Assert.Equal("org.gnome.keyring.NetworkPassword", exact["xdg:schema"]);
        Assert.Equal("smb", exact["protocol"]);
        Assert.Equal("server", exact["server"]);
        Assert.Equal("vic", exact["user"]);
        Assert.Equal("docs", exact["object"]);
    }

    /// <summary>
    /// The relaxed search asks for less, and Secret Service matches an item whose attributes
    /// are a superset — so fewer attributes find more items, including ones another application
    /// wrote with attributes we never heard of.
    /// </summary>
    [Fact]
    public void TheRelaxedSearchIsASubsetOfWhatIsWritten()
    {
        var exact = SecretServiceCredentialStore.Attributes(Share, exact: true);
        var relaxed = SecretServiceCredentialStore.Attributes(Share, exact: false);

        Assert.True(relaxed.Count < exact.Count);
        foreach (var (key, value) in relaxed)
            Assert.Equal(value, exact[key]);
    }

    /// <summary>The share is SMB's, and asking for it would hide a server-wide password.</summary>
    [Fact]
    public void TheRelaxedSearchDoesNotNameTheShare()
    {
        var relaxed = SecretServiceCredentialStore.Attributes(Share, exact: false);

        Assert.False(relaxed.ContainsKey("object"));
        Assert.Equal("vic", relaxed["user"]);
    }

    /// <summary>
    /// A navigation carries no username, because Location strips userinfo. Naming an empty one
    /// would match nothing at all.
    /// </summary>
    [Fact]
    public void AnUnknownUsernameIsLeftOutRatherThanSentEmpty()
    {
        var attributes = SecretServiceCredentialStore.Attributes(Share with { Username = "" }, exact: true);

        Assert.False(attributes.ContainsKey("user"));
    }

    /// <summary>
    /// A port nobody else writes is an attribute that stops the item matching anyone's search
    /// but our own.
    /// </summary>
    [Fact]
    public void ThePortIsOnlyNamedWhenTheCallerChoseOne()
    {
        Assert.False(SecretServiceCredentialStore.Attributes(Share, exact: true).ContainsKey("port"));
        Assert.Equal(
            "2222",
            SecretServiceCredentialStore.Attributes(Share with { Port = 2222 }, exact: true)["port"]);
    }
}
