using System.Buffers.Binary;

namespace Wlrix.Files.Core.Mime;

/// <summary>
/// The content-sniffing half of shared-mime-info: the compiled <c>magic</c> file.
/// </summary>
/// <remarks>
/// The format <c>update-mime-database</c> writes, which is not the text one from the source
/// XML and is not documented anywhere but in the specification's appendix and xdgmime's
/// reader. The whole file is:
///
/// <code>
/// "MIME-Magic\0\n"
/// [priority:mime/type]\n
/// [indent]&gt;offset=&lt;length as two big-endian bytes&gt;&lt;value&gt;[&amp;mask][~word][+range]\n
/// ...
/// </code>
///
/// <para>
/// <b>The value is binary and may contain a newline</b>, which is why every line is read by
/// the length that precedes it and never by scanning for the terminator. Getting that wrong
/// does not fail loudly; it silently mis-parses the rest of the file.
/// </para>
///
/// <para>
/// Rules nest by indent, and a rule with children matches only when the rule <em>and</em> one
/// of its children match. That is the whole point of the nesting: DocBook's first rule is
/// <c>&lt;?xml</c>, and without the requirement every XML document in the world would be
/// DocBook.
/// </para>
/// </remarks>
public sealed class MimeMagic
{
    private static readonly byte[] Header = "MIME-Magic\0\n"u8.ToArray();

    private readonly List<Section> _sections = [];

    /// <summary>How many bytes of a file any rule could possibly look at.</summary>
    /// <remarks>
    /// The caller reads this much and no more. Without it the choice is between reading whole
    /// files to answer a question about their first bytes, and picking a round number that is
    /// wrong for somebody.
    /// </remarks>
    public int MaxExtent { get; private set; }

    /// <summary>True when nothing was loaded, so callers can skip reading files at all.</summary>
    public bool IsEmpty => _sections.Count == 0;

    /// <summary>
    /// Loads and merges the <c>magic</c> files in the given <c>mime</c> directories.
    /// </summary>
    /// <remarks>
    /// Every directory contributes; a rule's priority decides, not which directory it came
    /// from. That differs from the name-based tables, where the first directory wins outright,
    /// and it is what the specification asks for.
    /// </remarks>
    public static MimeMagic Load(IEnumerable<string> mimeDirectories)
    {
        var magic = new MimeMagic();
        foreach (var directory in mimeDirectories)
        {
            var path = Path.Combine(directory, "magic");
            byte[] bytes;
            try
            {
                if (!File.Exists(path))
                    continue;
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            magic.Parse(bytes);
        }

        // Highest priority first, so the first section that matches is the answer.
        magic._sections.Sort(static (a, b) => b.Priority.CompareTo(a.Priority));
        return magic;
    }

    /// <summary>
    /// The type whose rules match <paramref name="data"/>, or null if none do.
    /// </summary>
    /// <param name="data">
    /// The start of the file, up to <see cref="MaxExtent"/> bytes. A short read is fine: rules
    /// reaching past the end simply do not match.
    /// </param>
    public string? Match(ReadOnlySpan<byte> data)
    {
        foreach (var section in _sections)
        {
            if (Matches(section.Rules, 0, data))
                return section.MimeType;
        }

        return null;
    }

    /// <summary>The best match, with the priority it matched at.</summary>
    /// <remarks>
    /// The priority is what lets a caller decide whether sniffing is confident enough to
    /// overrule something else. Nothing does that today — the name decides first here — but
    /// answering it costs nothing and not answering it would make that change a rewrite.
    /// </remarks>
    public (string MimeType, int Priority)? MatchWithPriority(ReadOnlySpan<byte> data)
    {
        foreach (var section in _sections)
        {
            if (Matches(section.Rules, 0, data))
                return (section.MimeType, section.Priority);
        }

        return null;
    }

    /// <summary>
    /// Whether any rule at <paramref name="index"/>'s indent level matches, with its children.
    /// </summary>
    private static bool Matches(List<Rule> rules, int start, ReadOnlySpan<byte> data)
    {
        if (rules.Count == 0)
            return false;

        var indent = rules[start].Indent;
        for (var i = start; i < rules.Count && rules[i].Indent >= indent; i++)
        {
            if (rules[i].Indent != indent)
                continue;
            if (!rules[i].Matches(data))
                continue;

            // A rule with children is a precondition, not an answer.
            var child = i + 1;
            if (child < rules.Count && rules[child].Indent > indent)
            {
                if (Matches(rules, child, data))
                    return true;
                continue;
            }

            return true;
        }

        return false;
    }

    private void Parse(byte[] bytes)
    {
        if (bytes.Length < Header.Length || !bytes.AsSpan(0, Header.Length).SequenceEqual(Header))
            return;

        var at = Header.Length;
        Section? section = null;

        while (at < bytes.Length)
        {
            if (bytes[at] == (byte)'[')
            {
                var close = Array.IndexOf(bytes, (byte)']', at);
                if (close < 0)
                    return;

                var text = System.Text.Encoding.UTF8.GetString(bytes, at + 1, close - at - 1);
                var colon = text.IndexOf(':');
                if (colon <= 0
                    || !int.TryParse(text[..colon], out var priority))
                {
                    return;
                }

                section = new Section(priority, text[(colon + 1)..]);
                _sections.Add(section);

                at = close + 1;
                if (at < bytes.Length && bytes[at] == (byte)'\n')
                    at++;
                continue;
            }

            if (section is null || !TryParseRule(bytes, ref at, out var rule))
                return;

            section.Rules.Add(rule);
            MaxExtent = Math.Max(MaxExtent, rule.Extent);
        }
    }

    private static bool TryParseRule(byte[] bytes, ref int at, out Rule rule)
    {
        rule = default!;

        var indent = ReadNumber(bytes, ref at);
        if (at >= bytes.Length || bytes[at] != (byte)'>')
            return false;
        at++;

        var offset = ReadNumber(bytes, ref at);
        if (at >= bytes.Length || bytes[at] != (byte)'=')
            return false;
        at++;

        if (at + 2 > bytes.Length)
            return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at, 2));
        at += 2;

        if (at + length > bytes.Length)
            return false;
        var value = bytes[at..(at + length)];
        at += length;

        byte[]? mask = null;
        if (at < bytes.Length && bytes[at] == (byte)'&')
        {
            at++;
            if (at + length > bytes.Length)
                return false;
            mask = bytes[at..(at + length)];
            at += length;
        }

        var word = 1;
        if (at < bytes.Length && bytes[at] == (byte)'~')
        {
            at++;
            word = ReadNumber(bytes, ref at);
        }

        var range = 1;
        if (at < bytes.Length && bytes[at] == (byte)'+')
        {
            at++;
            range = ReadNumber(bytes, ref at) + 1;
        }

        if (at < bytes.Length && bytes[at] == (byte)'\n')
            at++;

        // Values are stored big-endian. On a little-endian host a multi-byte word has to be
        // swapped before it can be compared against bytes read straight off the disk.
        if (word > 1 && BitConverter.IsLittleEndian)
        {
            SwapWords(value, word);
            if (mask is not null)
                SwapWords(mask, word);
        }

        rule = new Rule(indent, offset, Math.Max(1, range), value, mask);
        return true;
    }

    private static void SwapWords(byte[] bytes, int word)
    {
        for (var i = 0; i + word <= bytes.Length; i += word)
            Array.Reverse(bytes, i, word);
    }

    private static int ReadNumber(byte[] bytes, ref int at)
    {
        var value = 0;
        var any = false;
        while (at < bytes.Length && bytes[at] >= (byte)'0' && bytes[at] <= (byte)'9')
        {
            value = (value * 10) + (bytes[at] - '0');
            at++;
            any = true;
        }

        return any ? value : 0;
    }

    private sealed class Section(int priority, string mimeType)
    {
        public int Priority { get; } = priority;
        public string MimeType { get; } = mimeType;
        public List<Rule> Rules { get; } = [];
    }

    private sealed class Rule(int indent, int offset, int range, byte[] value, byte[]? mask)
    {
        public int Indent { get; } = indent;

        /// <summary>The furthest byte this rule can read.</summary>
        public int Extent { get; } = offset + range - 1 + value.Length;

        public bool Matches(ReadOnlySpan<byte> data)
        {
            for (var start = offset; start < offset + range; start++)
            {
                if (start + value.Length > data.Length)
                    break;

                if (MatchesAt(data[start..]))
                    return true;
            }

            return false;
        }

        private bool MatchesAt(ReadOnlySpan<byte> data)
        {
            if (mask is null)
                return data[..value.Length].SequenceEqual(value);

            for (var i = 0; i < value.Length; i++)
            {
                if ((data[i] & mask[i]) != (value[i] & mask[i]))
                    return false;
            }

            return true;
        }
    }
}
