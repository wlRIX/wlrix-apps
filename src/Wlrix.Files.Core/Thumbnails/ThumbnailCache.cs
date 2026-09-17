using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Wlrix.Files.Core.Platform;

namespace Wlrix.Files.Core.Thumbnails;

/// <summary>The sizes the thumbnail specification defines, and their directory names.</summary>
public enum ThumbnailSize
{
    Normal = 128,
    Large = 256,
    XLarge = 512,
    XXLarge = 1024
}

/// <summary>
/// The shared thumbnail cache, as the freedesktop specification defines it.
/// </summary>
/// <remarks>
/// Shared is the operative word: <c>$XDG_CACHE_HOME/thumbnails</c> is the same directory every
/// other file manager on the machine uses, so a thumbnail made here is one Nautilus does not
/// have to make again, and vice versa. That only works if the filename agrees exactly — it is
/// the MD5 of <see cref="Location.ToUriString"/>, which is why that method matches GLib's
/// escaping character for character.
///
/// <para>
/// Validity is <c>Thumb::MTime</c> against the source file's modification time in whole
/// seconds. Not the thumbnail's own mtime, which would make a cache copied between machines
/// look stale, and not a content hash, which would mean reading the file to find out whether
/// we need to read the file.
/// </para>
/// </remarks>
public sealed class ThumbnailCache
{
    /// <summary>What goes in the <c>Software</c> chunk, so another tool can see who made it.</summary>
    public const string Software = "wlRIX Files";

    /// <summary>The application name the spec's <c>fail/</c> subdirectory is keyed by.</summary>
    public const string FailureOwner = "wlrix-files";

    /// <summary>The suffix an in-progress write uses before it is moved into place.</summary>
    private const string PartSuffix = ".wlrix-new";

    private readonly string _root;

    /// <param name="root">
    /// The cache directory. Defaults to <c>$XDG_CACHE_HOME/thumbnails</c>, which is what makes
    /// this the shared one rather than a private copy.
    /// </param>
    public ThumbnailCache(string? root = null) => _root = root ?? DefaultRoot();

    /// <summary>Where the cache lives.</summary>
    public string Root => _root;

    private static string DefaultRoot()
    {
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrEmpty(cache) || !Path.IsPathRooted(cache))
        {
            cache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }
        return Path.Combine(cache, "thumbnails");
    }

    /// <summary>The MD5 the spec names a thumbnail file by.</summary>
    public static string HashOf(Location location) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(location.ToUriString())));

    /// <summary>Where a thumbnail of this size for this location would live.</summary>
    public string PathFor(Location location, ThumbnailSize size) =>
        Path.Combine(_root, DirectoryFor(size), HashOf(location) + ".png");

    /// <summary>
    /// Where the marker recording "this file cannot be thumbnailed" would live.
    /// </summary>
    /// <remarks>
    /// Keyed by application, per the spec, because the reason a file failed is usually that
    /// <i>this</i> program could not read it. A shared failure directory would have one
    /// application's missing codec suppress every other application's attempt.
    /// </remarks>
    public string FailurePathFor(Location location) =>
        Path.Combine(_root, "fail", FailureOwner, HashOf(location) + ".png");

    private static string DirectoryFor(ThumbnailSize size) => size switch
    {
        ThumbnailSize.Normal => "normal",
        ThumbnailSize.Large => "large",
        ThumbnailSize.XLarge => "x-large",
        _ => "xx-large"
    };

    /// <summary>The smallest standard size that is at least <paramref name="pixels"/> across.</summary>
    public static ThumbnailSize SizeFor(int pixels) => pixels switch
    {
        <= (int)ThumbnailSize.Normal => ThumbnailSize.Normal,
        <= (int)ThumbnailSize.Large => ThumbnailSize.Large,
        <= (int)ThumbnailSize.XLarge => ThumbnailSize.XLarge,
        _ => ThumbnailSize.XXLarge
    };

    /// <summary>
    /// The cached thumbnail for a file, or null if there is none that is still valid.
    /// </summary>
    /// <param name="modified">The source file's modification time.</param>
    public byte[]? TryLoad(Location location, ThumbnailSize size, DateTimeOffset modified)
    {
        var path = PathFor(location, size);
        try
        {
            if (!File.Exists(path))
                return null;

            var bytes = File.ReadAllBytes(path);
            var text = PngText.Read(bytes);
            return Matches(text, modified) ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether a cached thumbnail's recorded mtime still matches the source.</summary>
    /// <remarks>
    /// Whole seconds, because that is what the spec stores and what a Unix mtime is. Comparing
    /// with more precision would treat every thumbnail as stale.
    /// </remarks>
    private static bool Matches(IReadOnlyDictionary<string, string> text, DateTimeOffset modified) =>
        text.TryGetValue(PngText.MTimeKey, out var recorded)
        && long.TryParse(recorded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
        && seconds == modified.ToUnixTimeSeconds();

    /// <summary>Whether this file has already been found impossible to thumbnail.</summary>
    /// <remarks>
    /// Without this, a broken or enormous file is re-attempted on every scroll past it, which
    /// is the difference between one wasted second and a directory that never settles.
    /// </remarks>
    public bool HasFailed(Location location, DateTimeOffset modified)
    {
        try
        {
            var path = FailurePathFor(location);
            if (!File.Exists(path))
                return false;
            // The marker records an mtime too, so replacing the file with a good one clears
            // the failure rather than being suppressed by it for ever.
            return Matches(PngText.Read(File.ReadAllBytes(path)), modified);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Stores a rendered thumbnail, with the metadata the spec requires.</summary>
    /// <param name="png">The encoded image, without text chunks; they are added here.</param>
    public void Store(Location location, ThumbnailSize size, byte[] png,
        DateTimeOffset modified, long bytes, string mimeType)
    {
        var text = new List<KeyValuePair<string, string>>
        {
            new(PngText.UriKey, location.ToUriString()),
            new(PngText.MTimeKey, modified.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new(PngText.SizeKey, bytes.ToString(CultureInfo.InvariantCulture)),
            new(PngText.MimeKey, mimeType),
            new(PngText.SoftwareKey, Software)
        };
        WriteAtomically(PathFor(location, size), PngText.Write(png, text));
    }

    /// <summary>Records that this file could not be thumbnailed.</summary>
    /// <param name="png">
    /// A minimal valid PNG. The spec calls for a real image so that a viewer opening the
    /// marker directory does not choke; the contents are never displayed.
    /// </param>
    public void StoreFailure(Location location, byte[] png, DateTimeOffset modified)
    {
        var text = new List<KeyValuePair<string, string>>
        {
            new(PngText.UriKey, location.ToUriString()),
            new(PngText.MTimeKey, modified.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new(PngText.SoftwareKey, Software)
        };
        WriteAtomically(FailurePathFor(location), PngText.Write(png, text));
    }

    /// <summary>
    /// Writes a cache file so that no other process ever sees a partial one.
    /// </summary>
    /// <remarks>
    /// The directory is 0700 and the file 0600, which the spec requires: a thumbnail is a
    /// picture of the user's file, and a world-readable cache of those in a shared
    /// <c>/home</c> would be a disclosure of content the file itself may not permit.
    /// </remarks>
    private static void WriteAtomically(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path)!;
        try
        {
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var temporary = path + PartSuffix;
            File.WriteAllBytes(temporary, contents);
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written is a slower file manager, not a broken one.
            TryDelete(path + PartSuffix);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Whether a location may be thumbnailed at all.
    /// </summary>
    /// <remarks>
    /// Local files on local storage, and nothing else. Two gates rather than one: a location
    /// has to have a path at all, and that path has to be on something cheap to read —
    /// thumbnailing a directory on a network mount means pulling every file across the wire to
    /// make pictures nobody asked for, and doing it to <c>/proc</c> means reading kernel
    /// interfaces one byte at a time.
    ///
    /// <para>
    /// <see cref="Mount.IsLocalStorage"/> rather than <see cref="Mount.IsRealFilesystem"/>.
    /// The latter is the devices rail's question and answers this one wrongly in both
    /// directions: it excludes <c>tmpfs</c> and includes <c>nfs</c>.
    /// </para>
    /// </remarks>
    public static bool CanThumbnail(Location location, MountTable? mounts)
    {
        if (!location.TryGetLocalPath(out var path))
            return false;
        if (mounts is null)
            return true;
        return mounts.Find(path) is { IsLocalStorage: true };
    }
}
