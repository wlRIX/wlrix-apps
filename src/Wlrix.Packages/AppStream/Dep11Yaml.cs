using System.Globalization;
using System.Text;

namespace Wlrix.Packages.AppStream;

/// <summary>
/// Reads an AppStream catalog in its YAML form (DEP-11), which is what Debian and Ubuntu ship.
///
/// A line reader for the few fields wanted rather than a YAML parser. Debian's files run to tens
/// of megabytes of descriptions this has no use for, a general parser would build all of them,
/// and the fields read here are flat. It understands what those fields can legally hold: quoted
/// scalars, scalars continued on deeper lines, and block scalars.
/// </summary>
internal static class Dep11Yaml
{
    /// <summary>Parses one catalog.</summary>
    /// <param name="reader">The decompressed catalog.</param>
    /// <param name="language">The user's language, as a culture name.</param>
    /// <exception cref="InvalidDataException">The file does not start with a DEP-11 header.</exception>
    public static ParsedCatalog Read(TextReader reader, string language)
    {
        var parser = new Parser(language);
        while (reader.ReadLine() is { } line)
            parser.Line(line.TrimEnd());

        return parser.Finish();
    }

    private sealed class Parser(string language)
    {
        private readonly List<AppStreamComponent> _components = [];
        private bool _sawHeader;
        private string _origin = string.Empty;
        private int _filePriority;

        private Document _document = new(language);
        private string _section = string.Empty;
        private string _iconKey = string.Empty;

        // The translation being read, kept open until a line shallower than it arrives, since
        // that is the only thing that says its value has ended.
        private string? _translationTag;
        private int _translationIndent;
        private readonly StringBuilder _translation = new();

        public void Line(string line)
        {
            if (line.Length == 0 || line[0] == '#')
                return;

            if (line == "---" || line == "...")
            {
                EndDocument();
                return;
            }

            var indent = line.Length - line.TrimStart(' ').Length;
            var content = line[indent..];

            if (_translationTag is not null)
            {
                if (indent > _translationIndent)
                {
                    _translation.Append(' ').Append(content);
                    return;
                }

                EndTranslation();
            }

            if (indent == 0 && !content.StartsWith("- ", StringComparison.Ordinal))
                TopLevel(content);
            else
                InSection(content, indent);
        }

        public ParsedCatalog Finish()
        {
            EndDocument();
            if (!_sawHeader)
                throw new InvalidDataException("No DEP-11 header.");

            return new ParsedCatalog(_origin, _components);
        }

        private void TopLevel(string content)
        {
            var (key, value) = SplitKey(content);
            _section = key;
            _iconKey = string.Empty;

            switch (key)
            {
                case "File":
                    _sawHeader = true;
                    break;
                case "Origin":
                    _origin = Scalar(value);
                    break;
                case "Priority":
                    _filePriority = ParseInt(Scalar(value), 0);
                    _document.Priority = _filePriority;
                    break;
                case "ID":
                    _document.Id = Scalar(value);
                    break;
                case "Package":
                    _document.Package = Scalar(value);
                    break;
                case "Type":
                    _document.Kind = Scalar(value);
                    break;
                case "Merge":
                    _document.IsMerge = true;
                    break;
            }
        }

        private void InSection(string content, int indent)
        {
            switch (_section)
            {
                case "Name" or "Summary":
                    var (tag, value) = SplitKey(content);
                    _translationTag = tag;
                    _translationIndent = indent;
                    _translation.Clear().Append(value);
                    break;
                case "Categories" when content.StartsWith("- ", StringComparison.Ordinal):
                    _document.Categories.Add(Scalar(content[2..]));
                    break;
                case "Icon":
                    InIcon(content);
                    break;
            }
        }

        private void InIcon(string content)
        {
            var isItem = content.StartsWith("- ", StringComparison.Ordinal);
            var (key, value) = SplitKey(isItem ? content[2..] : content);

            switch (key)
            {
                case "stock" or "cached" or "local" or "remote" when !isItem:
                    _iconKey = key;
                    if (key == "stock" && CatalogRules.IsPathSegment(Scalar(value)))
                        _document.StockIcon = Scalar(value);
                    else if (key == "cached" && value.Length > 0)
                        _document.AddCached(Scalar(value));
                    return;
            }

            if (_iconKey != "cached")
                return;

            if (isItem)
                _document.BeginCached();

            switch (key)
            {
                case "name":
                    _document.CachedName = Scalar(value);
                    break;
                case "width":
                    _document.CachedWidth = ParseInt(Scalar(value), 0);
                    break;
                case "height":
                    _document.CachedHeight = ParseInt(Scalar(value), 0);
                    break;
                case "scale":
                    _document.CachedScale = ParseInt(Scalar(value), 1);
                    break;
            }
        }

        private void EndTranslation()
        {
            var text = Scalar(_translation.ToString());
            if (_section == "Name")
                _document.Name.Offer(_translationTag, text);
            else
                _document.Summary.Offer(_translationTag, text);

            _translationTag = null;
        }

        private void EndDocument()
        {
            if (_translationTag is not null)
                EndTranslation();

            if (_document.ToComponent() is { } component)
                _components.Add(component);

            _document = new Document(language) { Priority = _filePriority };
            _section = string.Empty;
            _iconKey = string.Empty;
        }
    }

    private sealed class Document(string language)
    {
        private readonly List<CachedIcon> _cached = [];

        public string? Id;
        public string? Package;
        public string Kind = string.Empty;
        public bool IsMerge;
        public int Priority;
        public BestTranslation Name = new(language);
        public BestTranslation Summary = new(language);
        public readonly List<string> Categories = [];
        public string? StockIcon;

        public string? CachedName;
        public int CachedWidth;
        public int CachedHeight;
        public int CachedScale = 1;

        public void BeginCached()
        {
            EndCached();
            CachedWidth = 0;
            CachedHeight = 0;
            CachedScale = 1;
        }

        public void AddCached(string name)
        {
            if (CatalogRules.IsPathSegment(name))
                _cached.Add(new CachedIcon(name, 0, 0, 1));
        }

        public AppStreamComponent? ToComponent()
        {
            EndCached();
            return IsMerge || string.IsNullOrEmpty(Id) || string.IsNullOrEmpty(Package)
                ? null
                : new AppStreamComponent(Id, Kind, [Package], Name.Value, Summary.Value, Categories, StockIcon,
                    _cached, Priority);
        }

        private void EndCached()
        {
            if (CatalogRules.IsPathSegment(CachedName))
                _cached.Add(new CachedIcon(CachedName!, CachedWidth, CachedHeight, CachedScale));

            CachedName = null;
        }
    }

    private static (string Key, string Value) SplitKey(string content)
    {
        var separator = content.IndexOf(": ", StringComparison.Ordinal);
        if (separator > 0)
            return (Scalar(content[..separator]), content[(separator + 2)..].Trim());

        return content.EndsWith(':') ? (Scalar(content[..^1]), string.Empty) : (content, string.Empty);
    }

    /// <summary>A scalar's value: unquoted, with any block indicator in front of it removed.</summary>
    private static string Scalar(string text)
    {
        var value = text.Trim();

        foreach (var indicator in new[] { ">-", ">+", "|-", "|+", ">", "|" })
        {
            if (value == indicator || value.StartsWith(indicator + " ", StringComparison.Ordinal))
                return value[indicator.Length..].Trim();
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return Unescape(value[1..^1]);

        return value;
    }

    private static string Unescape(string text)
    {
        if (!text.Contains('\\'))
            return text;

        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i == text.Length - 1)
            {
                result.Append(text[i]);
                continue;
            }

            var escape = text[++i];
            switch (escape)
            {
                case 'n':
                    result.Append(' ');
                    break;
                case 't':
                    result.Append('\t');
                    break;
                case 'u' when i + 4 < text.Length
                              && int.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber,
                                  CultureInfo.InvariantCulture, out var code):
                    result.Append((char)code);
                    i += 4;
                    break;
                default:
                    result.Append(escape);
                    break;
            }
        }

        return result.ToString();
    }

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
