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
    private const string Library = "libcrypt";

    /// <summary>
    /// libxcrypt's soname, by how it was built: <c>.so.2</c> without the glibc compatibility
    /// layer (Arch), <c>.so.1</c> with it (Debian, Ubuntu, Fedora). Both have <c>crypt_gensalt</c>.
    /// </summary>
    private static readonly string[] Sonames = ["libcrypt.so.2", "libcrypt.so.1"];

    static Crypt() => NativeLibrary.SetDllImportResolver(typeof(Crypt).Assembly, Resolve);

    private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Library)
            return IntPtr.Zero;
        foreach (var soname in Sonames)
        {
            if (NativeLibrary.TryLoad(soname, assembly, path, out var handle))
                return handle;
        }
        return IntPtr.Zero;
    }

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
