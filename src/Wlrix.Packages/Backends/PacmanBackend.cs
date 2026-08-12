using Microsoft.Extensions.Logging;
using Wlrix.Packages.Backends.Parsing;
using Wlrix.Packages.Models;
using Wlrix.Packages.Processes;
using ZLogger;

namespace Wlrix.Packages.Backends;

/// <summary>Arch Linux and its derivatives.</summary>
public sealed class PacmanBackend(IProcessRunner runner, ILogger<PacmanBackend> logger)
    : IPackageBackend
{
    /// <summary>Where pacman keeps the configuration this reads its repository list from.</summary>
    private const string ConfigurationPath = "/etc/pacman.conf";

    /// <summary>
    /// How many search hits get their sizes filled in. A <c>-Si</c> over the whole result set is
    /// one process either way, but its output is a block per package, and a two-letter query
    /// against a full Arch mirror set matches thousands. Past this point the Size column is left
    /// unknown rather than making the user wait to see a list they are going to narrow anyway.
    /// </summary>
    private const int SizeLookupLimit = 250;

    public string Id => "pacman";

    // No ModifyRepositories: pacman has no command for it, and the only way to add one is to
    // edit /etc/pacman.conf. That file is hand-owned, holds the user's mirror choices and their
    // comments, and a half-understood editor turning it into something pacman rejects would
    // cost them their whole configuration -- the same failure wlrix-settings-daemon exists to
    // stop happening to compositor.toml.
    public BackendCapabilities Capabilities =>
        BackendCapabilities.InstallFromRepository
        | BackendCapabilities.InstallFromFile
        | BackendCapabilities.Upgrade
        | BackendCapabilities.ListRepositories;

    public async Task<IReadOnlyList<PackageInfo>> SearchAsync(string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // -Ss takes a regex. The query is a user's search term, not a pattern, so it is escaped
        // -- otherwise typing "g++" is a syntax error rather than a search.
        var result = await runner.RunAsync("pacman", ["-Ss", "--", System.Text.RegularExpressions.Regex.Escape(query)],
            cancellationToken).ConfigureAwait(false);

        // pacman exits 1 when a search matches nothing, which is an answer and not a failure.
        if (!result.Succeeded && result.StandardOutput.Trim().Length == 0)
            return [];

        var packages = PacmanOutput.ParseSearch(result.StandardOutput);
        return await WithSizesAsync(packages, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        // -Qi rather than -Q: one process either way, and it carries the sizes and descriptions
        // the list wants. The installed database is local, so this is fast even at 2,000
        // packages.
        var result = await runner.RunAsync("pacman", ["-Qi"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            logger.ZLogWarning($"pacman -Qi failed: {result.StandardError.Trim()}");
            return [];
        }

        return PacmanOutput.ParseDetails(result.StandardOutput).Values
            .Select(details => new PackageInfo(details.Name, details.Version, details.Version,
                details.Description, details.Repository, details.InstalledSizeKilobytes,
                PackageStatus.Installed))
            .OrderBy(package => package.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<PackageInfo>> ListUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync("pacman", ["-Qu"], cancellationToken).ConfigureAwait(false);

        // Exit 1 with no output is "everything is up to date", not a failure.
        if (!result.Succeeded && result.StandardOutput.Trim().Length == 0)
            return [];

        var packages = PacmanOutput.ParseUpdates(result.StandardOutput);
        return await WithSizesAsync(packages, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PackageDetails?> DescribeAsync(string name,
        CancellationToken cancellationToken = default)
    {
        // -Si first: it knows the download size and the repository, which -Qi does not. Falling
        // back to -Qi covers a package that is installed but no longer in any repository, which
        // on a rolling distribution is an ordinary state rather than an error.
        foreach (var flag in new[] { "-Si", "-Qi" })
        {
            var result = await runner.RunAsync("pacman", [flag, "--", name], cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded
                && PacmanOutput.ParseDetails(result.StandardOutput).TryGetValue(name, out var details))
                return details;
        }

        return null;
    }

    public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(PacmanOutput.ParseConfiguration(File.ReadAllText(ConfigurationPath)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.ZLogWarning(ex, $"Could not read {ConfigurationPath}.");
            return Task.FromResult<IReadOnlyList<RepositoryInfo>>([]);
        }
    }

    /// <summary>
    /// Fills in the sizes a listing did not carry, with one <c>-Si</c> over the whole batch.
    /// Failure here is not failure of the listing: the Size column goes back to unknown and the
    /// user still gets their search results.
    /// </summary>
    private async Task<IReadOnlyList<PackageInfo>> WithSizesAsync(
        IReadOnlyList<PackageInfo> packages, CancellationToken cancellationToken)
    {
        if (packages.Count is 0 or > SizeLookupLimit)
            return packages;

        var arguments = new List<string> { "-Si", "--" };
        arguments.AddRange(packages.Select(package => package.Name).Distinct(StringComparer.Ordinal));

        ProcessResult result;
        try
        {
            result = await runner.RunAsync("pacman", arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (PackageProcessException ex)
        {
            logger.ZLogWarning(ex, $"Could not read package sizes.");
            return packages;
        }

        // -Si exits non-zero if any one name is unknown, having printed the rest; the output is
        // still worth parsing for the packages it did find.
        var details = PacmanOutput.ParseDetails(result.StandardOutput);
        if (details.Count == 0)
            return packages;

        return packages
            .Select(package => details.TryGetValue(package.Name, out var found)
                ? package with { InstalledSizeKilobytes = found.InstalledSizeKilobytes }
                : package)
            .ToList();
    }
}
