using System.Runtime.InteropServices;

namespace Wlrix.Settings.Users.Services;

/// <summary>
/// Hashing a password the way <c>/etc/shadow</c> wants it, through libxcrypt.
/// </summary>
/// <remarks>
/// AccountsService's <c>SetPassword</c> takes the <em>hashed</em> password, so the plain text
/// never crosses the bus. libxcrypt picks the hash: <c>crypt_gensalt</c> with a null prefix gives
/// the system's preferred method (yescrypt on current distributions), with fresh random salt.
/// </remarks>
internal static partial class Crypt
{
    private const string Library = "libcrypt.so.2";

    [LibraryImport(Library, EntryPoint = "crypt_gensalt", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GenSalt(string? prefix, ulong count, IntPtr randomBytes, int byteCount);

    [LibraryImport(Library, EntryPoint = "crypt", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CryptRaw(string phrase, string setting);

    private static readonly Lock Gate = new();

    /// <summary>The hash for <paramref name="password"/>, or an exception saying why there is none.</summary>
    public static string Hash(string password)
    {
        // Both functions hand back static buffers; one caller at a time.
        lock (Gate)
        {
            var salt = Marshal.PtrToStringUTF8(GenSalt(null, 0, IntPtr.Zero, 0))
                ?? throw new InvalidOperationException("libxcrypt made no salt");
            var hash = Marshal.PtrToStringUTF8(CryptRaw(password, salt));
            // libxcrypt's failure token starts with '*'; it is never a valid hash.
            if (string.IsNullOrEmpty(hash) || hash[0] == '*')
                throw new InvalidOperationException("libxcrypt could not hash the password");
            return hash;
        }
    }

    /// <summary>Whether <paramref name="password"/> matches <paramref name="hash"/>. For tests.</summary>
    internal static bool Matches(string password, string hash)
    {
        lock (Gate)
            return Marshal.PtrToStringUTF8(CryptRaw(password, hash)) == hash;
    }
}
