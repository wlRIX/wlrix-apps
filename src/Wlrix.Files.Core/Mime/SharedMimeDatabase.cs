using System.Globalization;
using System.IO.Enumeration;
using System.Xml;
using System.Xml.Linq;

namespace Wlrix.Files.Core.Mime;

/// <summary>
/// The freedesktop shared-mime-info database: what type a file is, and what icon names to
/// try for it.
/// </summary>
/// <remarks>
/// Reads the compiled files <c>update-mime-database</c> produces — <c>globs2</c>,
/// <c>aliases</c>, <c>subclasses</c>, <c>icons</c> and <c>generic-icons</c> — from
/// <c>$XDG_DATA_HOME/mime</c> and each <c>$XDG_DATA_DIRS/mime</c>, earlier directories
/// winning.
///
/// <para>
/// <b>The name decides first and the contents decide second.</b> <see cref="Resolve"/> is
/// name-based and is what a listing uses, because reading the first bytes of every file would
/// undo the work that keeps a large directory fast and would be a round trip each on a share.
/// <see cref="ResolveWithContent"/> adds the <c>magic</c> tier and is for one file at a time —
/// opening it, or showing its properties.
/// </para>
///
/// <para>
/// Being glob-first means agreeing with <c>gio</c> rather than with
/// <c>xdg-mime query filetype</c> on compound extensions: <c>backup.tar.gz</c> is
/// <c>application/x-compressed-tar</c> here, where sniffing the magic answers
/// <c>application/gzip</c>. That is the same disagreement
/// <c>com.wlrix.archiver.desktop</c> has to register both sides of.
/// </para>
/// </remarks>
public sealed class SharedMimeDatabase
{
    /// <summary>What an unrecognized file is called.</summary>
    public const string Default = "application/octet-stream";

    /// <summary>Directories, per the spec, that have no type of their own.</summary>
    public const string Directory = "inode/directory";

    public const string Symlink = "inode/symlink";

    private readonly Dictionary<string, List<MimeGlob>> _literals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MimeGlob>> _extensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MimeGlob> _others = [];
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _subclasses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _icons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _genericIcons = new(StringComparer.Ordinal);

    /// <summary>Where to look for the per-type XML that carries the human-readable name.</summary>
    private readonly List<string> _directories = [];

    /// <summary>The content-sniffing tier. Never consulted unless a caller hands over bytes.</summary>
    private MimeMagic _magic = MimeMagic.Load([]);

    /// <summary>
    /// Descriptions already read, keyed by language and type, including the ones that were
    /// not there.
    /// </summary>
    private readonly Dictionary<string, string?> _descriptions = new(StringComparer.Ordinal);

    private SharedMimeDatabase()
    {
    }

    /// <summary>Loads from the XDG data directories.</summary>
    public static SharedMimeDatabase Load() => Load(DefaultSearchPaths());

    /// <summary>
    /// Loads from the given <c>mime</c> directories, earliest winning.
    /// </summary>
    /// <remarks>
    /// The seam the tests use. They must never read <c>/usr/share/mime</c>: CI's
    /// shared-mime-info version differs from any developer's, and an assertion about what
    /// <c>.md</c> maps to would fail for reasons having nothing to do with this code.
    /// </remarks>
    public static SharedMimeDatabase Load(IEnumerable<string> mimeDirectories)
    {
        var db = new SharedMimeDatabase();

        // Materialized because it is walked twice — once for the name tables, once for magic —
        // and DefaultSearchPaths is a generator that would otherwise re-read the environment.
        mimeDirectories = mimeDirectories as IReadOnlyList<string> ?? [.. mimeDirectories];

        foreach (var dir in mimeDirectories)
        {
            db._directories.Add(dir);
            db.LoadGlobs(Path.Combine(dir, "globs2"));
            db.LoadPairs(Path.Combine(dir, "aliases"), ' ', (alias, canonical) => db._aliases.TryAdd(alias, canonical));
            db.LoadPairs(Path.Combine(dir, "subclasses"), ' ', (sub, super) =>
            {
                if (!db._subclasses.TryGetValue(sub, out var supers))
                    db._subclasses[sub] = supers = [];
                if (!supers.Contains(super, StringComparer.Ordinal))
                    supers.Add(super);
            });
            db.LoadPairs(Path.Combine(dir, "icons"), ':', (type, icon) => db._icons.TryAdd(type, icon));
            db.LoadPairs(Path.Combine(dir, "generic-icons"), ':', (type, icon) => db._genericIcons.TryAdd(type, icon));
        }

        // Magic is merged across every directory rather than first-wins, because a rule's
        // priority decides and a later directory may hold a more confident one.
        db._magic = MimeMagic.Load(mimeDirectories);

        // Longest suffix first, so ".tar.gz" is considered before ".gz".
        foreach (var list in db._extensions.Values)
            list.Sort(static (a, b) => b.Weight.CompareTo(a.Weight));
        db._others.Sort(static (a, b) =>
        {
            var byWeight = b.Weight.CompareTo(a.Weight);
            return byWeight != 0 ? byWeight : b.Pattern.Length.CompareTo(a.Pattern.Length);
        });
        return db;
    }

    /// <summary>The <c>mime</c> directories, in precedence order.</summary>
    public static IEnumerable<string> DefaultSearchPaths()
    {
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(home))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
                home = Path.Combine(profile, ".local", "share");
        }
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, "mime");

        var dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrEmpty(dirs))
            dirs = "/usr/local/share:/usr/share";
        foreach (var dir in dirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir, "mime");
    }

    /// <summary>The type of a directory entry.</summary>
    /// <remarks>
    /// The stat kind decides first: a directory named <c>notes.txt</c> is a directory, and no
    /// amount of glob matching should say otherwise.
    /// </remarks>
    public string Resolve(FileEntry entry) => entry.Kind switch
    {
        FileKind.Directory => Directory,
        FileKind.Symlink => Symlink,
        FileKind.Fifo or FileKind.Socket or FileKind.BlockDevice or FileKind.CharDevice => Default,
        _ => ResolveByName(entry.Name) ?? Default
    };

    /// <summary>What an empty file is called.</summary>
    /// <remarks>
    /// No magic rule can match nothing, so both readers special-case it — and they disagree.
    /// <c>xdg-mime</c> answers this; <c>gio</c> answers <c>application/x-zerosize</c>, and
    /// neither is registered as an alias of the other. This follows <c>xdg-mime</c> because it
    /// is in the <c>inode/</c> family the rest of the stat tier already uses.
    /// </remarks>
    public const string Empty = "inode/x-empty";

    /// <summary>How many bytes of a file <see cref="Sniff"/> wants.</summary>
    /// <remarks>
    /// Taken from the rules themselves rather than being a round number: it is exactly how far
    /// the furthest rule can reach, so reading more cannot change an answer and reading less
    /// can.
    /// </remarks>
    public int SniffLength => _magic.MaxExtent;

    /// <summary>True when there is no magic to consult, so a caller need not read anything.</summary>
    public bool CanSniff => !_magic.IsEmpty;

    /// <summary>
    /// The type of some file contents, or null when the bytes decide nothing.
    /// </summary>
    /// <param name="head">
    /// The first <see cref="SniffLength"/> bytes of the file, or all of it if it is shorter.
    /// An empty span means an empty file, which is an answer of its own.
    /// </param>
    public string? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.IsEmpty)
            return Empty;

        return _magic.Match(head) ?? (LooksLikeText(head) ? "text/plain" : null);
    }

    /// <summary>
    /// The type of an entry, sniffing the contents when the name does not settle it.
    /// </summary>
    /// <remarks>
    /// The name still decides first, which is the order <see cref="Resolve"/> documents and the
    /// order <c>gio</c> uses. Sniffing is the tier underneath it, for the files that have no
    /// useful name — <c>README</c>, <c>configure</c>, anything a build wrote without an
    /// extension — and those are exactly the files that otherwise come out as a generic blob
    /// with no application willing to open them.
    ///
    /// <para>
    /// Deliberately not called for a listing. Reading the first bytes of every file in a
    /// directory is the thing the whole enumeration design exists to avoid, and on a share it
    /// would be a round trip each. One file, on purpose, is what this is for.
    /// </para>
    /// </remarks>
    public string ResolveWithContent(FileEntry entry, ReadOnlySpan<byte> head)
    {
        if (entry.Kind is not (FileKind.File or FileKind.Unknown))
            return Resolve(entry);

        return ResolveByName(entry.Name) ?? Sniff(head) ?? Default;
    }

    /// <summary>
    /// Whether the bytes read as text rather than as a binary.
    /// </summary>
    /// <remarks>
    /// The fallback that stops every extensionless note file coming out as a blob nothing will
    /// open. No magic rule can match plain text — there is nothing distinctive to match — so
    /// both readers guess, and this guesses the same way: a NUL or a control character that is
    /// not whitespace means binary, and anything else is text. High bytes are left alone
    /// because they are ordinary UTF-8.
    /// </remarks>
    private static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        foreach (var b in head)
        {
            if (b >= 0x20 || b is 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x1B)
                continue;
            return false;
        }

        return true;
    }

    /// <summary>The type a filename implies, or null when nothing matches.</summary>
    public string? ResolveByName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;

        // 1. An exact filename. "core" and "Makefile" are types in their own right and must
        //    not lose to some wildcard that also matches.
        if (_literals.TryGetValue(fileName, out var literal))
            return Best(literal, fileName);

        // 2. The longest matching extension. Walking from the leftmost dot rightwards yields
        //    the longest suffix first, which is what makes ".tar.gz" beat ".gz".
        for (var i = fileName.IndexOf('.'); i >= 0 && i < fileName.Length - 1; i = fileName.IndexOf('.', i + 1))
        {
            var suffix = fileName[i..];
            if (_extensions.TryGetValue(suffix, out var candidates) && Best(candidates, fileName) is { } byExtension)
                return byExtension;
        }

        // 3. Anything else, already ordered by weight then pattern length.
        foreach (var glob in _others)
        {
            if (Matches(glob, fileName))
                return glob.MimeType;
        }

        return null;
    }

    /// <summary>Picks the best of several patterns that matched the same name.</summary>
    private static string? Best(List<MimeGlob> candidates, string fileName)
    {
        // Already weight-sorted, so the first that genuinely matches wins. The check matters
        // because the case-sensitive entries share a bucket with case-insensitive ones.
        foreach (var glob in candidates)
        {
            if (Matches(glob, fileName))
                return glob.MimeType;
        }
        return null;
    }

    private static bool Matches(MimeGlob glob, string fileName)
    {
        var comparison = glob.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return glob.Kind switch
        {
            MimeGlobKind.Literal => string.Equals(glob.Pattern, fileName, comparison),
            MimeGlobKind.Extension => fileName.EndsWith(glob.Suffix, comparison),
            _ => FileSystemName.MatchesSimpleExpression(glob.Pattern, fileName, ignoreCase: !glob.CaseSensitive)
        };
    }

    /// <summary>Resolves an alias to the type it stands for.</summary>
    public string Canonicalize(string mimeType) =>
        _aliases.TryGetValue(mimeType, out var canonical) ? canonical : mimeType;

    /// <summary>
    /// The type and everything it inherits from, most specific first.
    /// </summary>
    /// <remarks>
    /// What an Open With list walks: a C source file is a <c>text/plain</c> too, so the text
    /// editors belong on its menu — below anything that claims C specifically. Breadth-first
    /// with a visited set for the same reason <see cref="IsSubclassOf"/> uses one: the graph
    /// is a DAG in principle and a cycle in a malformed package would otherwise hang.
    /// </remarks>
    public IReadOnlyList<string> Ancestry(string mimeType)
    {
        var start = Canonicalize(mimeType);
        var order = new List<string> { start };
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            if (!_subclasses.TryGetValue(queue.Dequeue(), out var supers))
                continue;
            foreach (var super in supers)
            {
                if (!seen.Add(super))
                    continue;
                order.Add(super);
                queue.Enqueue(super);
            }
        }
        return order;
    }

    /// <summary>Whether <paramref name="mimeType"/> is <paramref name="super"/> or derives from it.</summary>
    /// <remarks>
    /// Walks the subclass graph breadth-first with a visited set. The graph is a DAG in
    /// principle and a cycle in a malformed package would otherwise hang the caller.
    /// </remarks>
    public bool IsSubclassOf(string mimeType, string super)
    {
        mimeType = Canonicalize(mimeType);
        super = Canonicalize(super);
        if (string.Equals(mimeType, super, StringComparison.Ordinal))
            return true;

        var seen = new HashSet<string>(StringComparer.Ordinal) { mimeType };
        var queue = new Queue<string>();
        queue.Enqueue(mimeType);

        while (queue.Count > 0)
        {
            if (!_subclasses.TryGetValue(queue.Dequeue(), out var supers))
                continue;
            foreach (var candidate in supers)
            {
                if (string.Equals(candidate, super, StringComparison.Ordinal))
                    return true;
                if (seen.Add(candidate))
                    queue.Enqueue(candidate);
            }
        }

        return false;
    }

    /// <summary>
    /// Icon names to try for a type, most specific first.
    /// </summary>
    /// <remarks>
    /// Four tiers, and every one earns its place: the <c>icons</c> override for types that
    /// name their own; the spec's <c>type/subtype</c> to <c>type-subtype</c> transformation;
    /// the <c>generic-icons</c> hint; and finally <c>&lt;media&gt;-x-generic</c>, which is
    /// what actually renders for most files because a theme carries a dozen of those and not
    /// a thousand specific ones.
    /// </remarks>
    public IReadOnlyList<string> IconNamesFor(string mimeType)
    {
        var canonical = Canonicalize(mimeType);
        var names = new List<string>(4);

        if (_icons.TryGetValue(canonical, out var explicitIcon))
            names.Add(explicitIcon);

        names.Add(canonical.Replace('/', '-'));

        if (_genericIcons.TryGetValue(canonical, out var generic))
            names.Add(generic);

        var slash = canonical.IndexOf('/');
        if (slash > 0)
            names.Add($"{canonical[..slash]}-x-generic");

        return names;
    }

    private void LoadGlobs(string path)
    {
        foreach (var line in ReadLines(path))
        {
            // weight:mimetype:pattern[:flags]  -- the pattern may itself contain a colon,
            // so this splits from the left a fixed number of times rather than on every one.
            var first = line.IndexOf(':');
            if (first <= 0)
                continue;
            var second = line.IndexOf(':', first + 1);
            if (second < 0)
                continue;

            if (!int.TryParse(line.AsSpan(0, first), out var weight))
                continue;
            var mimeType = line[(first + 1)..second];

            var rest = line[(second + 1)..];
            var caseSensitive = false;
            // Flags are a trailing ":cs" (and historically ":cs" only). Anything else after a
            // colon is part of the pattern.
            if (rest.EndsWith(":cs", StringComparison.Ordinal))
            {
                caseSensitive = true;
                rest = rest[..^3];
            }
            if (rest.Length == 0)
                continue;

            var glob = new MimeGlob(weight, mimeType, rest, caseSensitive);
            switch (glob.Kind)
            {
                case MimeGlobKind.Literal:
                    Add(_literals, glob.Pattern, glob);
                    break;
                case MimeGlobKind.Extension:
                    Add(_extensions, glob.Suffix, glob);
                    break;
                default:
                    _others.Add(glob);
                    break;
            }
        }

        static void Add(Dictionary<string, List<MimeGlob>> into, string key, MimeGlob glob)
        {
            if (!into.TryGetValue(key, out var list))
                into[key] = list = [];
            list.Add(glob);
        }
    }

    private void LoadPairs(string path, char separator, Action<string, string> add)
    {
        foreach (var line in ReadLines(path))
        {
            var split = line.IndexOf(separator);
            if (split <= 0 || split == line.Length - 1)
                continue;
            add(line[..split], line[(split + 1)..]);
        }
    }

    /// <summary>Reads a database file, skipping comments and blanks.</summary>
    /// <remarks>
    /// <summary>
    /// What a type is called in words: <c>image/png</c> answers "PNG image".
    /// </summary>
    /// <remarks>
    /// Read from <c>&lt;mime dir&gt;/&lt;media&gt;/&lt;subtype&gt;.xml</c>, one file per type,
    /// which is where shared-mime-info keeps the descriptions and their translations. That is
    /// also why this is not loaded up front: there are two thousand of those files here, and a
    /// properties dialog asks about one.
    ///
    /// <para>
    /// Answers null when there is no description, rather than inventing one from the type
    /// name. The caller shows the type itself in that case, which is honest — a made-up
    /// "X-Bzip Compressed Tar" reads like a real product name and is not.
    /// </para>
    /// </remarks>
    public string? DescriptionFor(string mimeType)
    {
        // Keyed by language as well as type. A session does not change language while it
        // runs, but a test that asks in two does, and a cache that answered the first one's
        // translation to the second would hide exactly the bug it was written to catch.
        var canonical = Canonicalize(mimeType);
        var key = $"{CultureInfo.CurrentUICulture.Name}\n{canonical}";

        lock (_descriptions)
        {
            if (_descriptions.TryGetValue(key, out var cached))
                return cached;
        }

        var description = ReadDescription(canonical);

        lock (_descriptions)
            _descriptions[key] = description;

        return description;
    }

    /// <summary>The languages to accept a description in, best first.</summary>
    /// <remarks>
    /// The spec tags translations with <c>xml:lang</c> and leaves the English one untagged, so
    /// the empty string at the end of this list is the fallback rather than a placeholder.
    /// A regional culture asks for its parent too: a <c>ja-JP</c> session should take the
    /// <c>ja</c> description rather than skipping straight to English.
    /// </remarks>
    private static IEnumerable<string> PreferredLanguages()
    {
        for (var culture = CultureInfo.CurrentUICulture; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
            yield return culture.Name;
        yield return string.Empty;
    }

    /// <summary>Whether a string is a single, ordinary path component.</summary>
    private static bool IsPathComponent(string text) =>
        text.Length > 0
        && text is not ("." or "..")
        && !text.AsSpan().ContainsAny(Path.GetInvalidFileNameChars());

    private string? ReadDescription(string mimeType)
    {
        var slash = mimeType.IndexOf('/');
        if (slash <= 0)
            return null;

        // Both halves become one path component each, and the type reaching here came off
        // disk. Checking them separately matters: the slash between them is a legal
        // separator and an illegal filename character at once, so testing the whole string
        // rejects everything.
        var media = mimeType[..slash];
        var subtype = mimeType[(slash + 1)..];
        if (!IsPathComponent(media) || !IsPathComponent(subtype))
            return null;

        var relative = Path.Combine(media, subtype + ".xml");

        foreach (var directory in _directories)
        {
            var path = Path.Combine(directory, relative);
            XElement root;
            try
            {
                if (!File.Exists(path))
                    continue;
                root = XElement.Load(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                continue;
            }

            var comments = root
                .Elements()
                .Where(static e => e.Name.LocalName == "comment")
                .ToList();
            if (comments.Count == 0)
                continue;

            foreach (var language in PreferredLanguages())
            {
                var match = comments.FirstOrDefault(e =>
                    string.Equals(
                        (string?)e.Attribute(XNamespace.Xml + "lang") ?? string.Empty,
                        language,
                        StringComparison.OrdinalIgnoreCase));
                if (match is not null && !string.IsNullOrWhiteSpace(match.Value))
                    return match.Value.Trim();
            }
        }

        return null;
    }

    /// A missing file is normal, not an error: a data directory may exist without a compiled
    /// mime database under it, and <c>icons</c> is often absent entirely.
    /// </remarks>
    private static IEnumerable<string> ReadLines(string path)
    {
        string[] lines;
        try
        {
            if (!File.Exists(path))
                return [];
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return lines.Where(static line => line.Length > 0 && line[0] != '#');
    }
}
