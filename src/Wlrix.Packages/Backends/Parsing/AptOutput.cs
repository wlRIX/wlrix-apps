using System.Text.RegularExpressions;
using Wlrix.Packages.Models;

namespace Wlrix.Packages.Backends.Parsing;

/// <summary>What one <c>apt-cache show</c> stanza says.</summary>
/// <param name="Version">The version of this stanza (a package may have several).</param>
/// <param name="SizeKilobytes">The <c>Installed-Size</c> field, which dpkg documents as kilobytes.</param>
/// <param name="Section">The archive section, which stands in for a repository name.</param>
/// <param name="Summary">The first line of the description.</param>
/// <param name="Description">The whole description.</param>
/// <param name="Homepage">The upstream address.</param>
/// <param name="Depends">The dependency field, unparsed.</param>
internal sealed record AptStanza(
    string Version,
    long SizeKilobytes,
    string Section,
    string Summary,
    string Description,
    string Homepage,
    string Depends);

/// <summary>
/// Turns apt's and dpkg's output into models.
///
/// The commands behind these are chosen for stability rather than convenience.
/// <c>dpkg-query -W -f=</c> and <c>apt-cache show</c> print formats meant to be read by
/// programs; <c>apt list</c> prints one that apt itself warns is not a stable interface, and is
/// not used here.
/// </summary>
internal static partial class AptOutput
{
    /// <summary>The format string handed to <c>dpkg-query -W</c>. Tab-separated, one line each.</summary>
    internal const string DpkgQueryFormat =
        "${db:Status-Abbrev}\t${binary:Package}\t${Version}\t${Installed-Size}\t${binary:Summary}\n";

    /// <summary>An <c>apt-cache search</c> line: <c>name - summary</c>.</summary>
    [GeneratedRegex("""^(?<name>\S+)\s+-\s+(?<summary>.*)$""", RegexOptions.ExplicitCapture)]
    private static partial Regex SearchLine { get; }

    /// <summary>
    /// An <c>apt-get -s</c> action line:
    /// <c>Inst bash [5.2-1] (5.2-2 Debian:12/stable [amd64])</c>, or <c>Remv bash [5.2-1]</c>.
    /// </summary>
    [GeneratedRegex(
        """^(?<action>Inst|Remv|Purg)\s+(?<name>\S+)(?:\s+\[(?<current>[^\]]+)\])?(?:\s+\((?<candidate>\S+)\s*(?<origin>[^)]*)\))?""",
        RegexOptions.ExplicitCapture)]
    private static partial Regex SimulationLine { get; }

    /// <summary>
    /// <c>dpkg-query -W</c> in <see cref="DpkgQueryFormat"/>. Only <c>ii</c> packages are
    /// returned: the database also lists packages that are removed-but-configured
    /// (<c>rc</c>), which are not installed software and would pad the list with things the
    /// user cannot see anywhere else on their system.
    /// </summary>
    internal static IReadOnlyList<PackageInfo> ParseInstalled(string output)
    {
        var results = new List<PackageInfo>();

        foreach (var raw in output.Split('\n'))
        {
            var fields = raw.TrimEnd('\r').Split('\t');
            if (fields.Length < 5 || fields[0].Trim() != "ii")
                continue;

            results.Add(new PackageInfo(
                fields[1].Trim(),
                fields[2].Trim(),
                fields[2].Trim(),
                fields[4].Trim(),
                Repository: string.Empty,
                SizeParser.ToKilobytes(fields[3].Trim()),
                PackageStatus.Installed));
        }

        return results;
    }

    /// <summary><c>apt-cache search</c>: name and one-line summary, in name order.</summary>
    internal static IReadOnlyList<(string Name, string Summary)> ParseSearch(string output)
    {
        var results = new List<(string, string)>();

        foreach (var raw in output.Split('\n'))
        {
            if (SearchLine.Match(raw.TrimEnd('\r')) is { Success: true } match)
                results.Add((match.Groups["name"].Value, match.Groups["summary"].Value.Trim()));
        }

        return results;
    }

    /// <summary>
    /// <c>apt-cache show</c>: RFC822 stanzas, blank-line separated, continuation lines indented.
    /// Where a package has several versions the first stanza wins, which is the candidate apt
    /// would install.
    /// </summary>
    internal static IReadOnlyDictionary<string, AptStanza> ParseShow(string output)
    {
        var results = new Dictionary<string, AptStanza>(StringComparer.Ordinal);

        foreach (var fields in Rfc822Stanzas(output))
        {
            var name = fields.GetValueOrDefault("Package", string.Empty);
            if (name.Length == 0 || results.ContainsKey(name))
                continue;

            var description = fields.GetValueOrDefault("Description")
                              ?? fields.GetValueOrDefault("Description-en")
                              ?? string.Empty;

            results[name] = new AptStanza(
                fields.GetValueOrDefault("Version", string.Empty),
                SizeParser.ToKilobytes(fields.GetValueOrDefault("Installed-Size")),
                fields.GetValueOrDefault("Section", string.Empty),
                description.Split('\n')[0].Trim(),
                description,
                fields.GetValueOrDefault("Homepage", string.Empty),
                fields.GetValueOrDefault("Depends", string.Empty));
        }

        return results;
    }

    /// <summary>
    /// <c>apt-get -s upgrade</c> or <c>-s install</c>: the <c>Inst</c> and <c>Remv</c> lines
    /// describing what would happen. This is both how updates are listed and how a transaction
    /// is planned, which is why it is one parser.
    /// </summary>
    internal static IReadOnlyList<PackageInfo> ParseSimulation(string output)
    {
        var results = new List<PackageInfo>();

        foreach (var raw in output.Split('\n'))
        {
            if (SimulationLine.Match(raw.TrimEnd('\r')) is not { Success: true } match)
                continue;

            if (match.Groups["action"].Value != "Inst")
                continue;

            var current = match.Groups["current"];
            var candidate = match.Groups["candidate"];
            var installed = current.Success ? current.Value : null;
            var available = candidate.Success ? candidate.Value : installed ?? string.Empty;

            results.Add(new PackageInfo(
                match.Groups["name"].Value,
                available,
                installed,
                Summary: string.Empty,
                // The origin reads "Debian:12/stable [amd64]"; the archive name ahead of the
                // architecture is the closest thing apt offers to a repository for this column.
                match.Groups["origin"].Value.Split('[')[0].Trim(),
                InstalledSizeKilobytes: 0,
                installed is null ? PackageStatus.New : PackageStatus.UpgradeAvailable));
        }

        return results;
    }

    /// <summary>
    /// The one-line format of <c>sources.list</c>:
    /// <c>deb [signed-by=…] https://deb.debian.org/debian bookworm main</c>. A commented line is
    /// read as a disabled source, which is how the file is used — the archive's own default
    /// ships its <c>deb-src</c> lines commented out.
    /// </summary>
    /// <param name="fileName">Which file these lines came from, used as the repository id.</param>
    internal static IReadOnlyList<RepositoryInfo> ParseSourcesList(string fileName, string content)
    {
        var results = new List<RepositoryInfo>();
        var index = 0;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            var commented = line.StartsWith('#');
            var text = commented ? line.TrimStart('#').Trim() : line;

            var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || fields[0] is not ("deb" or "deb-src"))
                continue;

            // Options come in brackets between the type and the URI, and may contain spaces.
            var uri = fields.FirstOrDefault(field =>
                field.Contains("://", StringComparison.Ordinal));
            if (uri is null)
                continue;

            var suite = fields.SkipWhile(field => field != uri).Skip(1).FirstOrDefault() ?? string.Empty;
            results.Add(new RepositoryInfo(
                $"{fileName}:{index++}",
                $"{fields[0]} {suite}".Trim(),
                uri,
                !commented));
        }

        return results;
    }

    /// <summary>
    /// The deb822 format of a <c>.sources</c> file — one stanza per source, with
    /// <c>Types</c>, <c>URIs</c>, <c>Suites</c> and an optional <c>Enabled</c>.
    /// </summary>
    /// <param name="fileName">Which file these stanzas came from, used as the repository id.</param>
    internal static IReadOnlyList<RepositoryInfo> ParseDeb822Sources(string fileName, string content)
    {
        var results = new List<RepositoryInfo>();
        var index = 0;

        foreach (var fields in Rfc822Stanzas(content))
        {
            var uris = fields.GetValueOrDefault("URIs", string.Empty).Trim();
            if (uris.Length == 0)
                continue;

            var types = fields.GetValueOrDefault("Types", "deb").Trim();
            var suites = fields.GetValueOrDefault("Suites", string.Empty).Trim();

            // Absent Enabled means enabled; the field only exists to turn a source off.
            var enabled = fields.GetValueOrDefault("Enabled", "yes").Trim();
            results.Add(new RepositoryInfo(
                $"{fileName}:{index++}",
                $"{types} {suites}".Trim(),
                uris.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? uris,
                !enabled.Equals("no", StringComparison.OrdinalIgnoreCase)));
        }

        return results;
    }

    /// <summary>
    /// The RFC822-ish stanza format apt uses everywhere: <c>Field: value</c>, continuation
    /// lines indented, a blank line between stanzas. Continuations keep their line breaks —
    /// unlike pacman's wrapped fields, a Description's paragraph structure is meaningful.
    /// </summary>
    private static IEnumerable<Dictionary<string, string>> Rfc822Stanzas(string content)
    {
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        string? lastKey = null;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.Trim().Length == 0)
            {
                if (current.Count > 0)
                {
                    yield return current;
                    current = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                lastKey = null;
                continue;
            }

            if (char.IsWhiteSpace(line[0]))
            {
                if (lastKey is not null)
                {
                    // A lone "." on a continuation line is deb822's empty paragraph.
                    var text = line.Trim();
                    current[lastKey] += "\n" + (text == "." ? string.Empty : text);
                }

                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            lastKey = line[..separator].Trim();
            current[lastKey] = line[(separator + 1)..].Trim();
        }

        if (current.Count > 0)
            yield return current;
    }
}
