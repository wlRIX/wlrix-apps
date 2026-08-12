using System.Xml;
using System.Xml.Linq;
using Wlrix.Packages.Models;

namespace Wlrix.Packages.Backends.Parsing;

/// <summary>
/// Turns zypper's output into models.
///
/// zypper is the one of the three with a real machine-readable mode: <c>--xmlout</c> wraps every
/// listing in a documented schema, so these read XML rather than columns. The exception is
/// <c>zypper info</c>, whose body stays human-readable inside the XML wrapper, and which goes
/// through <see cref="FieldBlocks"/> like pacman's.
/// </summary>
internal static class ZypperOutput
{
    /// <summary>
    /// <c>zypper --xmlout search --details</c>: a <c>&lt;solvable&gt;</c> per hit, carrying its
    /// own installed state. Non-package solvables (patterns, products, srcpackages) are dropped
    /// — they are not things this window installs.
    /// </summary>
    internal static IReadOnlyList<PackageInfo> ParseSearch(string output)
    {
        var results = new List<PackageInfo>();

        foreach (var solvable in Elements(output, "solvable"))
        {
            if (Attribute(solvable, "kind") is not ("package" or ""))
                continue;

            var name = Attribute(solvable, "name");
            if (name.Length == 0)
                continue;

            var version = Attribute(solvable, "edition");
            var installed = Attribute(solvable, "status") == "installed";

            results.Add(new PackageInfo(
                name,
                version,
                installed ? version : null,
                Attribute(solvable, "summary"),
                Attribute(solvable, "repository"),
                InstalledSizeKilobytes: 0,
                installed ? PackageStatus.SameVersion : PackageStatus.New));
        }

        return results;
    }

    /// <summary>
    /// <c>zypper --xmlout list-updates</c>: an <c>&lt;update&gt;</c> per upgradable package,
    /// with the new version in <c>edition</c> and the installed one in <c>edition-old</c>.
    /// </summary>
    internal static IReadOnlyList<PackageInfo> ParseUpdates(string output)
    {
        var results = new List<PackageInfo>();

        foreach (var update in Elements(output, "update"))
        {
            if (Attribute(update, "kind") is not ("package" or ""))
                continue;

            var name = Attribute(update, "name");
            if (name.Length == 0)
                continue;

            var installed = Attribute(update, "edition-old");
            results.Add(new PackageInfo(
                name,
                Attribute(update, "edition"),
                installed.Length > 0 ? installed : null,
                Attribute(update, "summary"),
                // The source repository is a child element rather than an attribute here.
                update.Elements().FirstOrDefault(child => child.Name.LocalName == "source")
                    is { } source
                    ? Attribute(source, "alias")
                    : string.Empty,
                InstalledSizeKilobytes: 0,
                installed.Length > 0 ? PackageStatus.UpgradeAvailable : PackageStatus.New));
        }

        return results;
    }

    /// <summary>
    /// <c>zypper --xmlout repos</c>: a <c>&lt;repo&gt;</c> per source, whose address is a
    /// <c>&lt;url&gt;</c> child.
    /// </summary>
    internal static IReadOnlyList<RepositoryInfo> ParseRepositories(string output)
    {
        var results = new List<RepositoryInfo>();

        foreach (var repo in Elements(output, "repo"))
        {
            var alias = Attribute(repo, "alias");
            if (alias.Length == 0)
                continue;

            var name = Attribute(repo, "name");
            var url = repo.Elements().FirstOrDefault(child => child.Name.LocalName == "url")?.Value.Trim()
                      ?? string.Empty;

            results.Add(new RepositoryInfo(
                alias,
                name.Length > 0 ? name : alias,
                url,
                Attribute(repo, "enabled") is "1" or "true"));
        }

        return results;
    }

    /// <summary>
    /// <c>zypper info</c>: a <c>Key : Value</c> block, the same shape as pacman's, wrapped in
    /// whatever preamble zypper printed about refreshing its metadata.
    /// </summary>
    internal static PackageDetails? ParseDetails(string output)
    {
        foreach (var block in FieldBlocks.Parse(output))
        {
            var name = block.Value("Name");
            if (name.Length == 0)
                continue;

            return new PackageDetails(
                name,
                block.Value("Version"),
                block.Value("Description"),
                block.Value("License"),
                // zypper 1.14 renamed this field; read whichever one is present.
                block.Value("URL") is { Length: > 0 } url ? url : block.Value("Upstream URL"),
                block.Value("Repository"),
                SizeParser.ToKilobytes(block.Value("Installed Size")),
                SizeParser.ToKilobytes(block.Value("Download Size")),
                Dependencies: []);
        }

        return null;
    }

    /// <summary>
    /// Every element with the given local name, or nothing at all if the output is not XML.
    ///
    /// zypper prints warnings outside its XML root when a repository is stale, which makes the
    /// document unparseable. That is a listing that comes back empty, not an exception thrown
    /// through the UI — the caller logs the raw output and shows no rows.
    /// </summary>
    private static IEnumerable<XElement> Elements(string output, string localName)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(output);
        }
        catch (XmlException)
        {
            return [];
        }

        return document.Descendants().Where(element => element.Name.LocalName == localName);
    }

    private static string Attribute(XElement element, string name) =>
        element.Attribute(name)?.Value.Trim() ?? string.Empty;
}
