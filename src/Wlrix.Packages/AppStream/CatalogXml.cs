using System.Globalization;
using System.Xml;

namespace Wlrix.Packages.AppStream;

/// <summary>
/// Reads an AppStream catalog in its XML form, which is what Arch and openSUSE ship.
///
/// A streaming read that skips what it does not want: Arch's <c>extra.xml.gz</c> is 30 MB of XML,
/// nearly all of it descriptions and translations. Only a component's direct children count,
/// because <c>developer</c> has a <c>name</c> of its own, and in 40 languages.
/// </summary>
internal static class CatalogXml
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
    };

    /// <summary>Parses one catalog.</summary>
    /// <param name="stream">The decompressed catalog.</param>
    /// <param name="language">The user's language, as a culture name.</param>
    /// <exception cref="XmlException">The file is not well-formed.</exception>
    /// <exception cref="InvalidDataException">The file is XML, but not a catalog.</exception>
    public static ParsedCatalog Read(Stream stream, string language)
    {
        using var reader = XmlReader.Create(stream, Settings);
        reader.MoveToContent();

        if (reader.LocalName != "components")
            throw new InvalidDataException($"Expected <components>, found <{reader.LocalName}>.");

        var origin = reader.GetAttribute("origin") ?? string.Empty;
        var priority = ParseInt(reader.GetAttribute("priority"), 0);
        var components = new List<AppStreamComponent>();

        foreach (var _ in Children(reader))
        {
            if (reader.LocalName != "component")
            {
                reader.Skip();
                continue;
            }

            if (ReadComponent(reader, language, priority) is { } component)
                components.Add(component);
        }

        return new ParsedCatalog(origin, components);
    }

    private static AppStreamComponent? ReadComponent(XmlReader reader, string language, int filePriority)
    {
        var kind = reader.GetAttribute("type") ?? string.Empty;
        var priority = ParseInt(reader.GetAttribute("priority"), filePriority);

        // A merge component amends another catalog's entry rather than describing software.
        var isMerge = reader.GetAttribute("merge") is not null;

        string? id = null;
        string? stock = null;
        var name = new BestTranslation(language);
        var summary = new BestTranslation(language);
        var packages = new List<string>();
        var categories = new List<string>();
        var icons = new List<CachedIcon>();

        foreach (var _ in Children(reader))
        {
            switch (reader.LocalName)
            {
                case "id":
                    id = Text(reader);
                    break;
                case "name":
                    name.Offer(reader.XmlLang, Text(reader));
                    break;
                case "summary":
                    summary.Offer(reader.XmlLang, Text(reader));
                    break;
                case "pkgname":
                    packages.Add(Text(reader));
                    break;
                case "icon":
                    ReadIcon(reader, icons, ref stock);
                    break;
                case "categories":
                    foreach (var __ in Children(reader))
                    {
                        if (reader.LocalName == "category")
                            categories.Add(Text(reader));
                        else
                            reader.Skip();
                    }

                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        packages.RemoveAll(package => package.Length == 0);

        return isMerge || string.IsNullOrEmpty(id) || packages.Count == 0
            ? null
            : new AppStreamComponent(id, kind, packages, name.Value, summary.Value, categories, stock, icons, priority);
    }

    private static void ReadIcon(XmlReader reader, List<CachedIcon> icons, ref string? stock)
    {
        var type = reader.GetAttribute("type");
        var width = ParseInt(reader.GetAttribute("width"), 0);
        var height = ParseInt(reader.GetAttribute("height"), 0);
        var scale = ParseInt(reader.GetAttribute("scale"), 1);
        var text = Text(reader);

        if (!CatalogRules.IsPathSegment(text))
            return;

        if (type == "stock")
            stock = text;
        else if (type == "cached")
            icons.Add(new CachedIcon(text, width, height, scale));
    }

    /// <summary>
    /// Steps through an element's child elements, leaving the reader on each. The loop body must
    /// consume the child, whether by reading it or by skipping it, and the reader finishes past
    /// the parent's end tag.
    /// </summary>
    private static IEnumerable<int> Children(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            yield break;
        }

        var index = 0;
        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement && !reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element)
                yield return index++;
            else
                reader.Read();
        }

        if (!reader.EOF)
            reader.ReadEndElement();
    }

    private static string Text(XmlReader reader) => reader.ReadElementContentAsString().Trim();

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
