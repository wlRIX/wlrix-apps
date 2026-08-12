using System.Text.RegularExpressions;
using Wlrix.Packages.Models;

namespace Wlrix.Packages.Backends.Parsing;

/// <summary>
/// Turns pacman's output into models. Pure functions over strings — nothing here starts a
/// process — which is what lets the tests feed them recorded output.
/// </summary>
internal static partial class PacmanOutput
{
    /// <summary>
    /// A <c>pacman -Ss</c> heading line:
    /// <c>extra/zsh 5.9.2-1 (base-devel) [installed: 5.9.2-1.1]</c>. The group list and the
    /// installed marker are both optional, and the marker carries a version only when the
    /// installed one differs from the one on offer.
    /// </summary>
    [GeneratedRegex(
        """^(?<repo>[^/\s]+)/(?<name>\S+)\s+(?<version>\S+)(?:\s+\((?<groups>[^)]*)\))?(?:\s+\[installed(?::\s*(?<installed>[^\]]+))?\])?\s*$""",
        RegexOptions.ExplicitCapture)]
    private static partial Regex SearchHeading { get; }

    /// <summary>A <c>pacman -Qu</c> line: <c>bash 5.3.15-2 -&gt; 5.3.16-1</c>.</summary>
    [GeneratedRegex(
        """^(?<name>\S+)\s+(?<from>\S+)\s+->\s+(?<to>\S+)""",
        RegexOptions.ExplicitCapture)]
    private static partial Regex UpgradeLine { get; }

    /// <summary>
    /// <c>pacman -Ss</c>: a heading line per package, each followed by its indented description.
    /// </summary>
    internal static IReadOnlyList<PackageInfo> ParseSearch(string output)
    {
        var results = new List<PackageInfo>();
        Match? heading = null;
        var description = new List<string>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
                continue;

            if (char.IsWhiteSpace(line[0]))
            {
                // Indented: part of the current package's description.
                description.Add(line.Trim());
                continue;
            }

            Flush(results, heading, description);
            description.Clear();
            heading = SearchHeading.Match(line) is { Success: true } match ? match : null;
        }

        Flush(results, heading, description);
        return results;
    }

    private static void Flush(List<PackageInfo> results, Match? heading, List<string> description)
    {
        if (heading is null)
            return;

        var version = heading.Groups["version"].Value;

        // Three states, and the difference matters to the Status column. No marker at all means
        // not installed. A bare "[installed]" means the installed version is the one on offer.
        // "[installed: x]" means x is on the system and something else is on offer.
        var marker = heading.Groups["installed"];
        var isInstalled = heading.Value.Contains("[installed", StringComparison.Ordinal);
        var installedVersion = marker.Success ? marker.Value.Trim() : isInstalled ? version : null;

        results.Add(new PackageInfo(
            heading.Groups["name"].Value,
            version,
            installedVersion,
            string.Join(' ', description),
            heading.Groups["repo"].Value,
            InstalledSizeKilobytes: 0,
            Status(version, installedVersion)));
    }

    /// <summary><c>pacman -Q</c>: one <c>name version</c> per line.</summary>
    internal static IReadOnlyList<PackageInfo> ParseInstalled(string output)
    {
        var results = new List<PackageInfo>();

        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            results.Add(new PackageInfo(parts[0], parts[1], parts[1], string.Empty,
                Repository: string.Empty, InstalledSizeKilobytes: 0, PackageStatus.Installed));
        }

        return results;
    }

    /// <summary><c>pacman -Qu</c>: what an upgrade would change.</summary>
    internal static IReadOnlyList<PackageInfo> ParseUpdates(string output)
    {
        var results = new List<PackageInfo>();

        foreach (var raw in output.Split('\n'))
        {
            if (UpgradeLine.Match(raw.Trim()) is not { Success: true } match)
                continue;

            var installed = match.Groups["from"].Value;
            var available = match.Groups["to"].Value;
            results.Add(new PackageInfo(match.Groups["name"].Value, available, installed,
                string.Empty, Repository: string.Empty, InstalledSizeKilobytes: 0,
                Status(available, installed)));
        }

        return results;
    }

    /// <summary>
    /// <c>pacman -Qi</c> or <c>-Si</c> blocks, keyed by package name. Both spellings of the
    /// repository field are read: <c>-Si</c> calls it <c>Repository</c>, and <c>-Qi</c> either
    /// omits it or calls it <c>Installed From</c>.
    ///
    /// A package carried by more than one repository gets a block from each, and the first one
    /// wins: pacman prints them in the order its configuration lists the repositories, so the
    /// first is the one an install would actually take. On a CachyOS machine that is the
    /// difference between reporting the optimized build the system has and reporting the stock
    /// Arch one it does not.
    /// </summary>
    internal static IReadOnlyDictionary<string, PackageDetails> ParseDetails(string output)
    {
        var results = new Dictionary<string, PackageDetails>(StringComparer.Ordinal);

        foreach (var block in FieldBlocks.Parse(output))
        {
            var name = block.Value("Name");
            if (name.Length == 0 || results.ContainsKey(name))
                continue;

            var repository = block.Value("Repository");
            if (repository.Length == 0)
                repository = block.Value("Installed From");

            results[name] = new PackageDetails(
                name,
                block.Value("Version"),
                block.Value("Description"),
                block.Value("Licenses"),
                block.Value("URL"),
                repository,
                SizeParser.ToKilobytes(block.Value("Installed Size")),
                SizeParser.ToKilobytes(block.Value("Download Size")),
                block.List("Depends On"));
        }

        return results;
    }

    /// <summary>
    /// The repository sections of <c>pacman.conf</c>. Commented-out sections count as disabled
    /// repositories rather than as absent ones, because that is how the file is actually used:
    /// the default configuration ships <c>[multilib]</c> commented out, and a user looking for
    /// it wants to see it listed and switched off, not missing.
    /// </summary>
    internal static IReadOnlyList<RepositoryInfo> ParseConfiguration(string configuration)
    {
        var results = new List<RepositoryInfo>();
        string? section = null;
        var enabled = false;
        var url = string.Empty;

        foreach (var raw in configuration.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();

            // A commented line is read as if it were live, so a disabled repository still
            // reports its name and mirror; the comment is what makes it disabled.
            //
            // Only when the '#' is immediately followed by content, though. pacman.conf's own
            // prose explains the format with an indented example --
            //
            //     # Repository entries are of the format:
            //     #       [repo-name]
            //     #       Server = ServerName
            //
            // -- and a parser that read that as a disabled repository would list pacman's
            // documentation as software source number one. A real disabled entry is written
            // hard against the '#', as `#[multilib]`, because it was made by commenting out a
            // line that had no indentation to begin with.
            var commented = line.StartsWith('#');
            if (commented && (line.Length < 2 || char.IsWhiteSpace(line[1])))
                continue;

            var text = commented ? line.TrimStart('#').Trim() : line;
            if (text.Length == 0)
                continue;

            if (text.StartsWith('[') && text.EndsWith(']'))
            {
                Flush(results, section, enabled, url);
                section = text[1..^1].Trim();
                enabled = !commented;
                url = string.Empty;
                continue;
            }

            if (section is null)
                continue;

            var separator = text.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = text[..separator].Trim();
            if (key is "Server" or "Include" && url.Length == 0)
                url = text[(separator + 1)..].Trim();
        }

        Flush(results, section, enabled, url);
        return results;

        static void Flush(List<RepositoryInfo> results, string? section, bool enabled, string url)
        {
            // [options] is pacman's own settings block, not a repository.
            if (section is null or "options")
                return;

            results.Add(new RepositoryInfo(section, section, url, enabled));
        }
    }

    private static PackageStatus Status(string available, string? installed) => installed switch
    {
        null => PackageStatus.New,
        _ when installed == available => PackageStatus.SameVersion,
        // Comparing two pacman versions properly means implementing vercmp, epochs and all.
        // The listings that need the distinction (-Qu, and a search marker carrying a version)
        // have already been told by pacman which way round it is, so this only has to say that
        // the two differ.
        _ => PackageStatus.UpgradeAvailable,
    };
}
