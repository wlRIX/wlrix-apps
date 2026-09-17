using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Wlrix.Files.Core;

/// <summary>
/// Where something is. A canonical absolute URI, and the key everything in the
/// file manager is indexed by.
/// </summary>
/// <remarks>
/// One type for local and remote alike: <c>file:///home/vic/x</c>,
/// <c>smb://host/share/dir</c>, <c>ftp://host/pub</c>, <c>sftp://host/home/vic</c>.
/// Code that only works on real paths has to say so by calling
/// <see cref="TryGetLocalPath"/>; everything else stays scheme-agnostic.
///
/// <para>
/// <b>Credentials never appear in a Location.</b> Userinfo is stripped on
/// construction, because locations are written to logs, hashed into thumbnail
/// filenames, and serialized into the bookmarks and session files. A password
/// that reached any of those would be very hard to get back out again.
/// </para>
///
/// <para>
/// Equality is ordinal over the canonical string, which is what lets the
/// Classic-mode window registry, the watcher's de-duplication and the
/// per-directory view state all key off the same value.
/// </para>
/// </remarks>
public sealed class Location : IEquatable<Location>
{
    /// <summary>The scheme wlRIX uses for the local filesystem.</summary>
    public const string FileScheme = "file";

    private readonly string _canonical;

    private Location(string scheme, string? host, int port, string path, string canonical)
    {
        Scheme = scheme;
        Host = host;
        Port = port;
        Path = path;
        _canonical = canonical;
    }

    /// <summary>The URI scheme, lowercased: <c>file</c>, <c>smb</c>, <c>ftp</c>, <c>sftp</c>.</summary>
    public string Scheme { get; }

    /// <summary>The host, lowercased, or <c>null</c> for <c>file</c>.</summary>
    public string? Host { get; }

    /// <summary>The port, or -1 when the scheme's default applies.</summary>
    public int Port { get; }

    /// <summary>
    /// The POSIX path within the filesystem, always starting with <c>/</c>, never
    /// ending with one (except at the root), and with <c>.</c> and <c>..</c> already
    /// resolved away.
    /// </summary>
    public string Path { get; }

    /// <summary>True for the root of its filesystem, which has no parent.</summary>
    public bool IsRoot => Path == "/";

    /// <summary>Whether this names something on the local filesystem.</summary>
    public bool IsLocal => Scheme == FileScheme;

    /// <summary>
    /// Identifies which <c>IFileSystem</c> instance serves this location:
    /// <c>file://</c>, or <c>scheme://host[:port]</c> for a remote.
    /// </summary>
    /// <remarks>
    /// Two locations sharing a mount key are reachable through one connection, which
    /// is what makes a server-side rename possible between them. The share name is
    /// deliberately *not* part of it: SMB reaches every share on a host over the same
    /// session, so splitting per share would open a redundant connection each time.
    /// </remarks>
    public string MountKey => Host is null
        ? $"{Scheme}://"
        : Port < 0 ? $"{Scheme}://{Host}" : $"{Scheme}://{Host}:{Port}";

    /// <summary>The last path component, or <see cref="string.Empty"/> at the root.</summary>
    public string Name
    {
        get
        {
            if (IsRoot)
                return string.Empty;
            var slash = Path.LastIndexOf('/');
            return Path[(slash + 1)..];
        }
    }

    /// <summary>The containing directory, or <c>null</c> if this is already the root.</summary>
    public Location? Parent
    {
        get
        {
            if (IsRoot)
                return null;
            var slash = Path.LastIndexOf('/');
            return Build(Scheme, Host, Port, slash == 0 ? "/" : Path[..slash]);
        }
    }

    /// <summary>The child of this location named <paramref name="name"/>.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty, contains a separator, or is <c>.</c> / <c>..</c> —
    /// none of which name a child, and all of which would otherwise let a crafted
    /// filename walk out of the directory it was listed from.
    /// </exception>
    public Location Child(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/'))
            throw new ArgumentException($"not a single path component: '{name}'", nameof(name));
        return Build(Scheme, Host, Port, IsRoot ? "/" + name : Path + "/" + name);
    }

    /// <summary>Whether <paramref name="other"/> is this location or somewhere beneath it.</summary>
    public bool Contains(Location other)
    {
        if (!string.Equals(MountKey, other.MountKey, StringComparison.Ordinal))
            return false;
        if (IsRoot)
            return true;
        return other.Path.Length > Path.Length
               && other.Path.StartsWith(Path, StringComparison.Ordinal)
               && other.Path[Path.Length] == '/';
    }

    /// <summary>
    /// The local filesystem path, when there is one.
    /// </summary>
    /// <remarks>
    /// The escape hatch, and used deliberately sparingly — thumbnails, <c>xdg-open</c>
    /// and handing an <c>IStorageItem</c> to a drag are the only things that genuinely
    /// cannot work in terms of a URI. Everything else must stay scheme-agnostic or
    /// remote locations quietly stop working.
    /// </remarks>
    public bool TryGetLocalPath([NotNullWhen(true)] out string? path)
    {
        path = IsLocal ? Path : null;
        return path is not null;
    }

    /// <summary>Parses a URI, or a bare absolute path as <c>file:</c>.</summary>
    public static Location Parse(string text)
    {
        if (TryParse(text, out var location))
            return location;
        throw new FormatException($"not a usable location: '{text}'");
    }

    /// <summary>Parses a URI, or a bare absolute path as <c>file:</c>.</summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out Location? location)
    {
        location = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // A bare absolute path is the common case from argv, a config file or a
        // drop, and spelling out file:// for it every time would be noise.
        if (text[0] == '/')
        {
            location = FromLocalPath(text);
            return true;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return false;

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme == FileScheme)
        {
            // A file: URI has no meaningful authority for us. "file://host/path"
            // is a remote in the RFC's eyes but nothing here can act on it.
            if (!string.IsNullOrEmpty(uri.Host))
                return false;
            location = FromLocalPath(Uri.UnescapeDataString(uri.AbsolutePath));
            return true;
        }

        if (string.IsNullOrEmpty(uri.Host))
            return false;

        // uri.UserInfo is read and discarded: see the note on credentials above.
        location = Build(scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port,
            Uri.UnescapeDataString(uri.AbsolutePath));
        return true;
    }

    /// <summary>Wraps an absolute local path.</summary>
    /// <exception cref="ArgumentException">The path is not absolute.</exception>
    public static Location FromLocalPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
            throw new ArgumentException($"not an absolute path: '{path}'", nameof(path));
        return Build(FileScheme, null, -1, path);
    }

    private static Location Build(string scheme, string? host, int port, string path)
    {
        var normalized = NormalizePath(path);
        var canonical = host is null
            ? $"{scheme}://{Escape(normalized)}"
            : port < 0
                ? $"{scheme}://{host}{Escape(normalized)}"
                : $"{scheme}://{host}:{port}{Escape(normalized)}";
        return new Location(scheme, host, port, normalized, canonical);
    }

    /// <summary>
    /// Folds a path to its canonical form: one leading slash, no empty or <c>.</c>
    /// components, <c>..</c> resolved, and no trailing slash.
    /// </summary>
    /// <remarks>
    /// A <c>..</c> that would climb above the root is dropped rather than treated as
    /// an error, which is what the kernel does with <c>/..</c> and keeps a stray one
    /// in a config file from being fatal. It can never escape, because there is
    /// nothing above the root to escape to.
    /// </remarks>
    internal static string NormalizePath(string path)
    {
        var parts = new List<string>();
        foreach (var range in path.AsSpan().Split('/'))
        {
            var part = path.AsSpan()[range];
            if (part.Length == 0 || part is ".")
                continue;
            if (part is "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part.ToString());
        }

        return parts.Count == 0 ? "/" : "/" + string.Join('/', parts);
    }

    /// <summary>
    /// The characters a path component keeps unescaped, beyond the unreserved set.
    /// </summary>
    /// <remarks>
    /// What GLib's <c>g_filename_to_uri</c> leaves alone, determined by asking it rather than
    /// by reading RFC 3986 — the two disagree. <c>;</c> is an RFC sub-delimiter and perfectly
    /// legal in a path segment, and GLib escapes it anyway; following the spec here would
    /// produce a different hash from every GTK application on the machine for any file with a
    /// semicolon in its name.
    ///
    /// <para>
    /// Matching GLib is the whole point and is not cosmetic. The freedesktop thumbnail cache
    /// is <i>shared</i>: its filenames are the MD5 of this string, so a file called
    /// <c>holiday, 2019.jpg</c> encoded our way and GLib's way hashes to two different names.
    /// Every other file manager on the machine would then be unable to see a thumbnail we
    /// made, and we unable to see theirs — which defeats most of the reason to implement the
    /// spec rather than keep a private cache. Verified against <c>g_filename_to_uri</c>
    /// output; see the tests.
    /// </para>
    /// </remarks>
    private const string PathSafe = "!$&\'()*+,=:@";

    /// <summary>
    /// Percent-encodes a path for the canonical form, per-component so the
    /// separators survive.
    /// </summary>
    private static string Escape(string path)
    {
        if (path == "/")
            return "/";
        var sb = new StringBuilder(path.Length + 8);
        foreach (var range in path.AsSpan()[1..].Split('/'))
        {
            sb.Append('/');
            EscapeComponent(path.AsSpan()[1..][range], sb);
        }
        return sb.ToString();
    }

    /// <summary>Percent-encodes one path component, UTF-8 byte by UTF-8 byte.</summary>
    /// <remarks>
    /// Hand-rolled rather than <c>Uri.EscapeDataString</c>, which escapes every one of
    /// <see cref="PathSafe"/> as well and so produces a different string — and therefore a
    /// different thumbnail filename — from every GLib-based application on the system.
    /// </remarks>
    private static void EscapeComponent(ReadOnlySpan<char> component, StringBuilder into)
    {
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in component.EnumerateRunes())
        {
            if (rune.IsAscii && (char.IsAsciiLetterOrDigit((char)rune.Value)
                                 || "-._~".Contains((char)rune.Value, StringComparison.Ordinal)
                                 || PathSafe.Contains((char)rune.Value, StringComparison.Ordinal)))
            {
                into.Append((char)rune.Value);
                continue;
            }

            var written = rune.EncodeToUtf8(utf8);
            for (var i = 0; i < written; i++)
                into.Append('%').Append(utf8[i].ToString("X2", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// The canonical percent-encoded URI: what gets hashed to name a thumbnail file,
    /// and what goes into that file's <c>Thumb::URI</c>.
    /// </summary>
    /// <remarks>
    /// Both come from here on purpose. The thumbnail spec requires the two to agree,
    /// and computing them separately is the classic way to end up with a cache that
    /// never hits because one side escaped a space and the other did not.
    /// </remarks>
    public string ToUriString() => _canonical;

    /// <inheritdoc/>
    public override string ToString() => _canonical;

    /// <inheritdoc/>
    public bool Equals(Location? other) =>
        other is not null && string.Equals(_canonical, other._canonical, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Location);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_canonical);

    public static bool operator ==(Location? a, Location? b) => a?.Equals(b) ?? b is null;

    public static bool operator !=(Location? a, Location? b) => !(a == b);
}
