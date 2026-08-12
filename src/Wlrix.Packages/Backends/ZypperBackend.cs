using Microsoft.Extensions.Logging;
using Wlrix.Packages.Backends.Parsing;
using Wlrix.Packages.Models;
using Wlrix.Packages.Processes;
using ZLogger;

namespace Wlrix.Packages.Backends;

/// <summary>openSUSE and SUSE Linux Enterprise.</summary>
/// <remarks>
/// Written against zypper's documented XML output and covered by fixture tests, but not
/// exercised on a SUSE system — see this project's README.
/// </remarks>
public sealed class ZypperBackend(IProcessRunner runner, ILogger<ZypperBackend> logger)
    : IPackageBackend
{
    // Every read goes out with these. --xmlout for a parseable answer, and
    // --non-interactive so a stale repository signature asks nothing and simply fails.
    private static readonly string[] Common = ["--xmlout", "--non-interactive"];

    public string Id => "zypper";

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

        // No --match-substrings: that is zypper's default for a bare term, and naming it as well
        // as passing "--" keeps the query a search term rather than an option.
        var result = await RunAsync(["search", "--details", "--type", "package", "--", query],
            cancellationToken).ConfigureAwait(false);

        // 104 is zypper's documented "nothing matched", which is an answer.
        if (result is null)
            return [];

        return ZypperOutput.ParseSearch(result);
    }

    public async Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            ["search", "--details", "--installed-only", "--type", "package"],
            cancellationToken).ConfigureAwait(false);

        if (result is null)
            return [];

        return ZypperOutput.ParseSearch(result)
            .Select(package => package with { Status = PackageStatus.Installed })
            .OrderBy(package => package.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<PackageInfo>> ListUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["list-updates", "--type", "package"], cancellationToken)
            .ConfigureAwait(false);

        return result is null ? [] : ZypperOutput.ParseUpdates(result);
    }

    public async Task<PackageDetails?> DescribeAsync(string name,
        CancellationToken cancellationToken = default)
    {
        // Not --xmlout here: zypper wraps `info` in XML but leaves the body as the same
        // human-readable block it prints without it, so the wrapper buys nothing and the
        // block parser reads it either way.
        var result = await runner.RunAsync("zypper", ["--non-interactive", "info", "--", name],
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded ? ZypperOutput.ParseDetails(result.StandardOutput) : null;
    }

    public async Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["repos", "--details"], cancellationToken).ConfigureAwait(false);
        return result is null ? [] : ZypperOutput.ParseRepositories(result);
    }

    /// <summary>
    /// Runs zypper and returns its stdout, or <c>null</c> when there is nothing to parse.
    ///
    /// zypper's exit codes are documented and worth honoring: 104 is "no matches", which is a
    /// result, and 106 is "a repository could not be refreshed", after which it still prints
    /// what it does know. Everything else is logged and answered with nothing.
    /// </summary>
    private async Task<string?> RunAsync(IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var full = new List<string>(Common);
        full.AddRange(arguments);

        var result = await runner.RunAsync("zypper", full, cancellationToken).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 or 106 => result.StandardOutput,
            104 => null,
            _ => Fail(result),
        };

        string? Fail(ProcessResult failed)
        {
            logger.ZLogWarning(
                $"zypper {string.Join(' ', arguments)} exited {failed.ExitCode}: {failed.StandardError.Trim()}");
            return null;
        }
    }
}
