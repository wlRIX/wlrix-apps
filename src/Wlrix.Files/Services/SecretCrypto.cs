using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Wlrix.Files.Services;

/// <summary>
/// The Secret Service's <c>dh-ietf1024-sha256-aes128-cbc-pkcs7</c> session algorithm.
/// </summary>
/// <remarks>
/// <para>
/// A password handed to the keyring travels over the session bus, and the spec's other
/// algorithm — <c>plain</c> — puts it there in the clear. Anyone who can read that bus is
/// already this user and could ask the keyring themselves, so this is not a defence against an
/// attacker; it is a defence against an <b>accident</b>. <c>dbus-monitor --session</c> is an
/// ordinary thing to have running while debugging something else, and a password scrolling past
/// in somebody's terminal is a leak that no amount of care elsewhere takes back.
/// </para>
/// <para>
/// It is also what libsecret negotiates by default, so wlRIX is not the one client on the
/// system asking for the weaker option.
/// </para>
/// <para>
/// Pure and static on purpose: every step here is a place to be subtly wrong, and none of it
/// needs a bus to test. The end-to-end check is stronger still — a wrong prime or a wrong
/// padding yields a key the daemon does not share, so the password reads back as rubbish rather
/// than as itself, which <c>secret-tool</c> notices immediately.
/// </para>
/// </remarks>
internal static class SecretCrypto
{
    /// <summary>
    /// The 1024-bit MODP group, RFC 2409's Second Oakley Group.
    /// </summary>
    /// <remarks>
    /// Named by the algorithm itself — <c>ietf1024</c> — so it is not a parameter and not a
    /// choice. Both sides have it hard-coded or neither can agree on anything.
    /// </remarks>
    private const string PrimeHex =
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD1" +
        "29024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245" +
        "E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE65381" +
        "FFFFFFFFFFFFFFFF";

    /// <summary>The group's generator, which for this group is 2.</summary>
    private static readonly BigInteger Generator = 2;

    private static readonly BigInteger Prime = Parse(PrimeHex);

    /// <summary>The group's prime as the wire's bytes, so a test can pin every digit of it.</summary>
    internal static byte[] PrimeBytes => ToWire(Prime);

    /// <summary>The modulus in bytes. Every public key and the shared secret are this long.</summary>
    internal const int KeyLength = 128;

    /// <summary>The AES key the exchange produces.</summary>
    private const int AesKeyLength = 16;

    /// <summary>The session algorithm's name on the wire.</summary>
    public const string Algorithm = "dh-ietf1024-sha256-aes128-cbc-pkcs7";

    /// <summary>A fresh private exponent.</summary>
    /// <remarks>
    /// The full width of the modulus, as libsecret's is. A short exponent would still work and
    /// still interoperate, which is exactly why it is worth being deliberate about.
    /// </remarks>
    public static BigInteger NewPrivateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(KeyLength);
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }

    /// <summary>The public key to send, as the wire's big-endian bytes.</summary>
    public static byte[] PublicKey(BigInteger privateKey) =>
        ToWire(BigInteger.ModPow(Generator, privateKey, Prime));

    /// <summary>The AES key both sides end up with.</summary>
    /// <param name="privateKey">Ours.</param>
    /// <param name="theirPublicKey">Theirs, as it came off the bus.</param>
    public static byte[] SharedKey(BigInteger privateKey, byte[] theirPublicKey)
    {
        var theirs = new BigInteger(theirPublicKey, isUnsigned: true, isBigEndian: true);
        var shared = ToWire(BigInteger.ModPow(theirs, privateKey, Prime));

        // HKDF-SHA256 with a null salt and empty info, which is what the spec says in as many
        // words. .NET's null salt is HKDF's own "a string of HashLen zeros", so the two agree.
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, AesKeyLength, salt: null, info: null);
    }

    /// <summary>Encrypts a secret for the wire.</summary>
    /// <returns>The IV, which travels in the secret's <c>parameters</c>, and the ciphertext.</returns>
    public static (byte[] Parameters, byte[] Value) Encrypt(byte[] key, string secret)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        var plain = Encoding.UTF8.GetBytes(secret);
        return (aes.IV, aes.EncryptCbc(plain, aes.IV, PaddingMode.PKCS7));
    }

    /// <summary>Reads a secret off the wire.</summary>
    public static string Decrypt(byte[] key, byte[] parameters, byte[] value)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return Encoding.UTF8.GetString(aes.DecryptCbc(value, parameters, PaddingMode.PKCS7));
    }

    /// <summary>
    /// A number as the fixed-width big-endian bytes the protocol expects.
    /// </summary>
    /// <remarks>
    /// <b>Left-padded to the modulus length, and that is not cosmetic.</b>
    /// <see cref="BigInteger"/> drops leading zeros, so roughly one exchange in 256 produces a
    /// shared secret a byte short — and a shorter buffer hashes to a different key, so the
    /// session works nearly always and fails at random. The kind of defect that gets blamed on
    /// the keyring.
    /// </remarks>
    internal static byte[] ToWire(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == KeyLength)
            return bytes;

        var padded = new byte[KeyLength];
        // A value longer than the modulus cannot happen -- everything here is reduced mod
        // Prime -- so the copy is always a right-alignment.
        bytes.CopyTo(padded, KeyLength - bytes.Length);
        return padded;
    }

    private static BigInteger Parse(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }
}
