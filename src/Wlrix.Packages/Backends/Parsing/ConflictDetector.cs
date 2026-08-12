using System.Text.RegularExpressions;

namespace Wlrix.Packages.Backends.Parsing;

/// <summary>Something the package manager refused to do without being told how to proceed.</summary>
/// <param name="Package">What the conflict is about, where the line names it.</param>
/// <param name="Description">The package manager's own words, which are the most useful ones.</param>
public sealed record TransactionConflict(string Package, string Description);

/// <summary>
/// Picks conflicts out of a transaction's output.
///
/// It reads the running transaction rather than a dry run, and that is not the obvious choice —
/// but it is the only one that works for all three. <c>pacman -S --print</c> does not resolve
/// conflicts at all: it prints the package list and says nothing, and the conflict only appears
/// when the real transaction prepares. apt and zypper do report them in simulation, so for
/// those two this is a second chance rather than the only one.
///
/// The cost is that the Conflicts button lights up during a transaction instead of before it.
/// The alternative was a pre-flight check that is thorough on two backends and silent on the
/// third, which is worse: a user would learn to trust it and then be surprised by the one
/// system where it never says anything.
/// </summary>
public static partial class ConflictDetector
{
    /// <summary>pacman: <c>:: cowsay and cowsay-git are in conflict. Remove cowsay-git? [y/N]</c></summary>
    [GeneratedRegex(
        @"^::\s*(?<first>\S+)\s+and\s+(?<second>\S+)\s+are\s+in\s+conflict",
        RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase)]
    private static partial Regex PacmanConflict { get; }

    /// <summary>pacman, unresolvable: <c>error: failed to prepare transaction (could not satisfy dependencies)</c></summary>
    [GeneratedRegex(
        @"^error:\s*failed to (?:prepare|commit) transaction\s*\((?<reason>[^)]*)\)",
        RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase)]
    private static partial Regex PacmanTransactionFailure { get; }

    /// <summary>apt: <c>The following packages have unmet dependencies:</c> and its <c>Conflicts:</c> lines.</summary>
    [GeneratedRegex(
        @"^\s*(?<package>\S+)\s*:\s*(?:Conflicts|Breaks|Depends):\s*(?<detail>.+)$",
        RegexOptions.ExplicitCapture)]
    private static partial Regex AptUnmet { get; }

    /// <summary>zypper: <c>Problem: cowsay-3.04 conflicts with cowsay-git provided by …</c></summary>
    [GeneratedRegex(
        @"^\s*Problem:\s*(?<detail>.+)$",
        RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase)]
    private static partial Regex ZypperProblem { get; }

    /// <summary>
    /// The conflict <paramref name="line"/> reports, or <c>null</c> if it reports none.
    /// </summary>
    public static TransactionConflict? Read(string backend, string line) => backend switch
    {
        "pacman" => Pacman(line),
        "apt" => Apt(line),
        "zypper" => Zypper(line),
        _ => null,
    };

    private static TransactionConflict? Pacman(string line)
    {
        if (PacmanConflict.Match(line) is { Success: true } conflict)
        {
            return new TransactionConflict(
                conflict.Groups["first"].Value,
                $"{conflict.Groups["first"].Value} and {conflict.Groups["second"].Value} are in conflict.");
        }

        if (PacmanTransactionFailure.Match(line) is { Success: true } failure)
            return new TransactionConflict(string.Empty, failure.Groups["reason"].Value.Trim());

        return null;
    }

    private static TransactionConflict? Apt(string line) =>
        AptUnmet.Match(line) is { Success: true } match
            ? new TransactionConflict(match.Groups["package"].Value, match.Value.Trim())
            : null;

    private static TransactionConflict? Zypper(string line) =>
        ZypperProblem.Match(line) is { Success: true } match
            ? new TransactionConflict(string.Empty, match.Groups["detail"].Value.Trim())
            : null;
}
