using Microsoft.Extensions.Logging;
using Wlrix.Packages.Backends.Parsing;
using Wlrix.Packages.Models;
using Wlrix.Packages.Processes;
using ZLogger;

namespace Wlrix.Packages.Backends;

/// <summary>Debian, Ubuntu and their derivatives.</summary>
/// <remarks>
/// Written against apt's and dpkg's documented machine-readable output and covered by fixture
/// tests, but not exercised on a Debian system — see this project's README. The commands it
/// runs are all read-only, so the worst an unnoticed format change costs is an empty list.
/// </remarks>
public sealed class AptBackend(IProcessRunner runner, ILogger<AptBackend> logger) : IPackageBackend
{
    /// <summary>Where one-line sources live.</summary>
    private const string SourcesList = "/etc/apt/sources.list";

    /// <summary>Where the rest of them live, in either format.</summary>
    private const string SourcesDirectory = "/etc/apt/sources.list.d";

    /// <summary>
    /// How many search hits get a version and size. <c>apt-cache show</c> over the batch is one
    /// process, but its output is a stanza per package and a broad query on a full Debian
    /// archive matches tens of thousands.
    /// </summary>
    private const int DetailLookupLimit = 250;

    public string Id => "apt";

    public BackendCapabilities Capabilities =>
        BackendCapabilities.InstallFromRepository
        | BackendCapabilities.InstallFromFile
        | BackendCapabilities.Upgrade
        | BackendCapabilities.ListRepositories
        | BackendCapabilities.ModifyRepositories;

    public async Task<IReadOnlyList<PackageInfo>> SearchAsync(string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // --names-only, because apt-cache's default searches every description and a common word
        // matches most of the archive.
        var result = await runner.RunAsync("apt-cache",
                ["search", "--names-only", "--", System.Text.RegularExpressions.Regex.Escape(query)],
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            logger.ZLogWarning($"apt-cache search failed: {result.StandardError.Trim()}");
            return [];
        }

        var hits = AptOutput.ParseSearch(result.StandardOutput);
        if (hits.Count == 0)
            return [];

        var installed = await InstalledVersionsAsync(cancellationToken).ConfigureAwait(false);
        var stanzas = hits.Count <= DetailLookupLimit
            ? await ShowAsync(hits.Select(hit => hit.Name), cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, AptStanza>(StringComparer.Ordinal);

        return hits.Select(hit =>
        {
            var stanza = stanzas.GetValueOrDefault(hit.Name);
            var available = stanza?.Version ?? string.Empty;
            var installedVersion = installed.GetValueOrDefault(hit.Name);

            return new PackageInfo(hit.Name, available, installedVersion, hit.Summary,
                stanza?.Section ?? string.Empty, stanza?.SizeKilobytes ?? 0,
                Status(available, installedVersion));
        }).ToList();
    }

    public async Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync("dpkg-query",
            ["-W", $"-f={AptOutput.DpkgQueryFormat}"], cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded && result.StandardOutput.Trim().Length == 0)
        {
            logger.ZLogWarning($"dpkg-query failed: {result.StandardError.Trim()}");
            return [];
        }

        return AptOutput.ParseInstalled(result.StandardOutput)
            .OrderBy(package => package.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<PackageInfo>> ListUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        // A simulated upgrade rather than `apt list --upgradable`: apt prints a warning that its
        // CLI is not a stable interface, and means it. The Inst/Remv lines of a simulation are
        // apt's oldest machine-readable output and are what a transaction plan reads too.
        var result = await runner.RunAsync("apt-get",
            ["-s", "-q", "-o", "Debug::NoLocking=1", "upgrade"], cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            logger.ZLogWarning($"apt-get -s upgrade failed: {result.StandardError.Trim()}");
            return [];
        }

        var updates = AptOutput.ParseSimulation(result.StandardOutput);
        if (updates.Count == 0)
            return [];

        var stanzas = await ShowAsync(updates.Select(update => update.Name), cancellationToken)
            .ConfigureAwait(false);

        return updates
            .Select(update => stanzas.TryGetValue(update.Name, out var stanza)
                ? update with { InstalledSizeKilobytes = stanza.SizeKilobytes, Summary = stanza.Summary }
                : update)
            .ToList();
    }

    public async Task<PackageDetails?> DescribeAsync(string name,
        CancellationToken cancellationToken = default)
    {
        var stanzas = await ShowAsync([name], cancellationToken).ConfigureAwait(false);
        if (!stanzas.TryGetValue(name, out var stanza))
            return null;

        return new PackageDetails(
            name,
            stanza.Version,
            stanza.Description,
            // apt keeps licenses in the package's copyright file rather than its metadata, so
            // there is nothing to report here without unpacking the package.
            License: string.Empty,
            stanza.Homepage,
            stanza.Section,
            stanza.SizeKilobytes,
            DownloadSizeKilobytes: 0,
            stanza.Depends.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<RepositoryInfo>();

        try
        {
            if (File.Exists(SourcesList))
                results.AddRange(AptOutput.ParseSourcesList(
                    Path.GetFileName(SourcesList), File.ReadAllText(SourcesList)));

            if (Directory.Exists(SourcesDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(SourcesDirectory).OrderBy(p => p, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(path);
                    var content = File.ReadAllText(path);

                    // The two formats are told apart by extension, which is how apt does it:
                    // .list is the one-line form and .sources the deb822 one.
                    results.AddRange(Path.GetExtension(path) == ".sources"
                        ? AptOutput.ParseDeb822Sources(name, content)
                        : AptOutput.ParseSourcesList(name, content));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.ZLogWarning(ex, $"Could not read the apt sources.");
        }

        return Task.FromResult<IReadOnlyList<RepositoryInfo>>(results);
    }

    /// <summary>What is installed right now, by name.</summary>
    private async Task<Dictionary<string, string>> InstalledVersionsAsync(CancellationToken cancellationToken)
    {
        var installed = await ListInstalledAsync(cancellationToken).ConfigureAwait(false);
        return installed.ToDictionary(package => package.Name, package => package.Version,
            StringComparer.Ordinal);
    }

    /// <summary>One batched <c>apt-cache show</c>, tolerant of names it does not know.</summary>
    private async Task<IReadOnlyDictionary<string, AptStanza>> ShowAsync(IEnumerable<string> names,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "show", "--" };
        arguments.AddRange(names.Distinct(StringComparer.Ordinal));
        if (arguments.Count == 2)
            return new Dictionary<string, AptStanza>(StringComparer.Ordinal);

        try
        {
            var result = await runner.RunAsync("apt-cache", arguments, cancellationToken)
                .ConfigureAwait(false);

            // apt-cache exits non-zero when any one name is unknown, having printed the rest.
            return AptOutput.ParseShow(result.StandardOutput);
        }
        catch (PackageProcessException ex)
        {
            logger.ZLogWarning(ex, $"Could not read package details.");
            return new Dictionary<string, AptStanza>(StringComparer.Ordinal);
        }
    }

    private static PackageStatus Status(string available, string? installed) => (available, installed) switch
    {
        (_, null) => PackageStatus.New,
        ("", _) => PackageStatus.Installed,
        _ when available == installed => PackageStatus.SameVersion,
        // Ordering two Debian versions properly is dpkg's own comparison algorithm, epochs and
        // tildes and all. Whether a difference is up or down only matters where apt has already
        // said so, and there it arrives through ParseSimulation.
        _ => PackageStatus.UpgradeAvailable,
    };
}
