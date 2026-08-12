using System.Text.RegularExpressions;

namespace Wlrix.Packages.Backends.Parsing;

/// <summary>
/// The three phases the Status pane's progress strip shows. They are the IRIX original's, and
/// they survive the translation because the shape of the work has not changed: work out what to
/// do, move the files, then let the system react to them.
/// </summary>
public enum TransactionPhase
{
    /// <summary>Refreshing lists, resolving dependencies, checking for conflicts.</summary>
    Initialize,

    /// <summary>Downloading and unpacking.</summary>
    Install,

    /// <summary>Hooks, triggers, and whatever else runs afterwards.</summary>
    PostInstall,
}

/// <summary>What one line of output says about how far along a transaction is.</summary>
/// <param name="Phase">Which phase the line belongs to.</param>
/// <param name="Percent">How far through that phase, if the line said; otherwise <c>null</c>.</param>
public sealed record ProgressUpdate(TransactionPhase Phase, double? Percent);

/// <summary>
/// Reads a package manager's running commentary for what phase it is in.
///
/// This is inference, not a protocol: none of the three reports progress in a machine-readable
/// way, so this watches for the phrases they print. That makes it the least reliable thing in
/// the library, which is why it drives only the progress strip. Nothing depends on it being
/// right — a missed line leaves a bar where it was, and the Log pane still has the truth.
/// </summary>
public static partial class TransactionProgress
{
    /// <summary>A counted step, which every one of the three spells as <c>(n/m)</c>.</summary>
    [GeneratedRegex(@"\(\s*(?<done>\d+)\s*/\s*(?<total>\d+)\s*\)", RegexOptions.ExplicitCapture)]
    private static partial Regex Counter { get; }

    /// <summary>zypper counts differently: <c>Installing: foo-1.0 (5/12)</c>, same shape.</summary>
    [GeneratedRegex(@"^(?<action>Retrieving|Installing|Removing)\b", RegexOptions.ExplicitCapture)]
    private static partial Regex ZypperAction { get; }

    /// <summary>
    /// What <paramref name="line"/> says, or <c>null</c> if it says nothing about progress.
    /// </summary>
    public static ProgressUpdate? Read(string backend, string line) => backend switch
    {
        "pacman" => Pacman(line),
        "apt" => Apt(line),
        "zypper" => Zypper(line),
        _ => null,
    };

    private static ProgressUpdate? Pacman(string line)
    {
        // Hooks first: they also carry an (n/m) counter, and they come after the install, so a
        // line matching both is a post-install one.
        if (line.Contains("hook", StringComparison.OrdinalIgnoreCase)
            || line.Contains("post-transaction", StringComparison.OrdinalIgnoreCase))
            return new ProgressUpdate(TransactionPhase.PostInstall, Percent(line));

        if (line.StartsWith("installing ", StringComparison.Ordinal)
            || line.StartsWith("upgrading ", StringComparison.Ordinal)
            || line.StartsWith("removing ", StringComparison.Ordinal)
            || line.Contains(") installing ", StringComparison.Ordinal)
            || line.Contains(") upgrading ", StringComparison.Ordinal)
            || line.Contains(") removing ", StringComparison.Ordinal)
            || line.Contains("Retrieving packages", StringComparison.Ordinal))
            return new ProgressUpdate(TransactionPhase.Install, Percent(line));

        if (line.Contains("Synchronizing package databases", StringComparison.Ordinal)
            || line.Contains("resolving dependencies", StringComparison.Ordinal)
            || line.Contains("looking for conflicting packages", StringComparison.Ordinal)
            || line.Contains("checking ", StringComparison.Ordinal))
            return new ProgressUpdate(TransactionPhase.Initialize, Percent(line));

        return null;
    }

    private static ProgressUpdate? Apt(string line)
    {
        // "Processing triggers for man-db" and friends: apt's post-install phase.
        if (line.StartsWith("Processing triggers", StringComparison.Ordinal)
            || line.StartsWith("Setting up ", StringComparison.Ordinal))
            return new ProgressUpdate(TransactionPhase.PostInstall, Percent(line));

        if (line.StartsWith("Get:", StringComparison.Ordinal)
            || line.StartsWith("Unpacking ", StringComparison.Ordinal)
            || line.StartsWith("Preparing to unpack ", StringComparison.Ordinal)
            || line.StartsWith("Removing ", StringComparison.Ordinal))
            return new ProgressUpdate(TransactionPhase.Install, Percent(line));

        if (line.StartsWith("Reading package lists", StringComparison.Ordinal)
            || line.StartsWith("Building dependency tree", StringComparison.Ordinal)
            || line.StartsWith("Reading state information", StringComparison.Ordinal)
            || line.StartsWith("Hit:", StringComparison.Ordinal))
            return new ProgressUpdate(TransactionPhase.Initialize, Percent(line));

        return null;
    }

    private static ProgressUpdate? Zypper(string line)
    {
        if (line.StartsWith("Running post-transaction", StringComparison.Ordinal)
            || line.StartsWith("Executing %posttrans", StringComparison.Ordinal)
            || line.StartsWith("Checking for file conflicts", StringComparison.Ordinal))
            return new ProgressUpdate(TransactionPhase.PostInstall, Percent(line));

        if (ZypperAction.IsMatch(line))
            return new ProgressUpdate(TransactionPhase.Install, Percent(line));

        if (line.StartsWith("Retrieving repository", StringComparison.Ordinal)
            || line.StartsWith("Loading repository data", StringComparison.Ordinal)
            || line.StartsWith("Reading installed packages", StringComparison.Ordinal)
            || line.StartsWith("Resolving package dependencies", StringComparison.Ordinal))
            return new ProgressUpdate(TransactionPhase.Initialize, Percent(line));

        return null;
    }

    /// <summary>The <c>(n/m)</c> in a line as a percentage, or <c>null</c> if there is not one.</summary>
    private static double? Percent(string line)
    {
        if (Counter.Match(line) is not { Success: true } match)
            return null;

        if (!double.TryParse(match.Groups["total"].Value, out var total) || total <= 0)
            return null;

        if (!double.TryParse(match.Groups["done"].Value, out var done))
            return null;

        return Math.Clamp(done / total * 100, 0, 100);
    }
}
