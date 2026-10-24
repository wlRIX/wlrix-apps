using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Wlrix.Settings.International.Services;

/// <summary>
/// A GNU gettext <c>.mo</c> catalog, read for its translations of other projects' names.
/// </summary>
/// <remarks>
/// <para>
/// The lists this panel shows come from files that are written in English -- xkeyboard-config's
/// <c>evdev.xml</c>, iso-codes' country list -- and those projects ship their translations as
/// gettext catalogs under <c>/usr/share/locale</c>. Reading the catalog directly is what lets a
/// Japanese session see 日本語 in the layout list without this app carrying a copy of a few
/// thousand names that somebody else already translates.
/// </para>
/// <para>
/// Only what a lookup needs: the singular of each message, by its English text. A message with a
/// context (<c>ctx\x04id</c>) is looked up by its id alone, and plural forms are ignored.
/// </para>
/// </remarks>
internal sealed class MoCatalog
{
    private const uint Magic = 0x950412de;

    /// <summary>A catalog that translates nothing, for when there is none to read.</summary>
    public static MoCatalog Empty { get; } = new([]);

    private readonly Dictionary<string, string> _messages;

    private MoCatalog(Dictionary<string, string> messages) => _messages = messages;

    /// <summary>The translation of <paramref name="english"/>, or the English itself when there is none.</summary>
    public string Translate(string english) =>
        _messages.TryGetValue(english, out var translated) && translated.Length > 0 ? translated : english;

    public int Count => _messages.Count;

    /// <summary>Parse the bytes of a <c>.mo</c> file. Throws <see cref="InvalidDataException"/> on anything else.</summary>
    public static MoCatalog Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28)
            throw new InvalidDataException("too short to be a .mo file");

        // The magic number is written in the producing machine's byte order, so it says which
        // order everything else is in.
        bool little;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic)
            little = true;
        else if (BinaryPrimitives.ReadUInt32BigEndian(data) == Magic)
            little = false;
        else
            throw new InvalidDataException("not a .mo file");

        uint Read(ReadOnlySpan<byte> span, int offset) => little
            ? BinaryPrimitives.ReadUInt32LittleEndian(span[offset..])
            : BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);

        var count = (int)Read(data, 8);
        var originals = (int)Read(data, 12);
        var translations = (int)Read(data, 16);

        var messages = new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            // Each table entry is the string's length, then its offset.
            var original = String(data, offset: (int)Read(data, originals + i * 8 + 4), length: (int)Read(data, originals + i * 8));
            var translation = String(data, offset: (int)Read(data, translations + i * 8 + 4), length: (int)Read(data, translations + i * 8));

            // The empty msgid is the catalog's own header, not a message.
            if (original.Length == 0)
                continue;

            var key = original.Split('\0')[0];
            var context = key.IndexOf('\u0004');
            if (context >= 0)
                key = key[(context + 1)..];

            // Where a context makes two entries of one English word, the first wins: the lists
            // here only ever ask for the plain name.
            messages.TryAdd(key, translation.Split('\0')[0]);
        }
        return new MoCatalog(messages);
    }

    private static string String(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > data.Length)
            throw new InvalidDataException("a string runs past the end of the file");
        return Encoding.UTF8.GetString(data.Slice(offset, length));
    }

    /// <summary>
    /// The catalog for <paramref name="domain"/> in the UI language, or <see cref="Empty"/> when
    /// none is installed. <c>ja_JP</c> is tried before <c>ja</c>, as gettext itself does.
    /// </summary>
    public static MoCatalog Load(string domain, CultureInfo? culture = null, string root = "/usr/share/locale")
    {
        culture ??= CultureInfo.CurrentUICulture;
        if (culture.Name.Length == 0)
            return Empty;

        var names = new List<string> { culture.Name.Replace('-', '_') };
        if (culture.TwoLetterISOLanguageName is { Length: > 0 } language && !names.Contains(language))
            names.Add(language);

        foreach (var name in names)
        {
            var path = Path.Combine(root, name, "LC_MESSAGES", domain + ".mo");
            if (!File.Exists(path))
                continue;
            try
            {
                return Parse(File.ReadAllBytes(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A broken catalog costs the translation, not the list: try the next, then English.
            }
        }
        return Empty;
    }
}
