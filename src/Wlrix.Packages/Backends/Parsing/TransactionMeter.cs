using System.Text.RegularExpressions;

namespace Wlrix.Packages.Backends.Parsing;

/// <summary>
/// Turns one transaction's output into a single figure for the overall progress bar.
///
/// The phases do not take equal time, so each gets a share of the bar: a little to work out what
/// to do, the most to replacing files, a little for hooks. pacman has no <c>(n/m)</c> without a
/// terminal, but it does say how many packages it is about to handle, so its downloads and package
/// operations are counted against that. apt prints no count, so it moves from phase to phase. The
/// figure never goes backward: a line out of order leaves the bar where it was.
/// </summary>
public sealed partial class TransactionMeter(string backend)
{
    private const double InitializeEnd = 10;
    private const double DownloadEnd = 40;
    private const double InstallEnd = 90;

    /// <summary><c>Packages (70) foo-1-1 bar-2-1</c>, the plan pacman prints before acting on it.</summary>
    [GeneratedRegex(@"^Packages \((?<count>\d+)\)", RegexOptions.ExplicitCapture)]
    private static partial Regex PacmanPlan { get; }

    /// <summary>Without a terminal, pacman names each file as it starts to fetch it.</summary>
    [GeneratedRegex(@"^\s\S+ downloading\.\.\.$")]
    private static partial Regex PacmanDownload { get; }

    private int _packages;
    private int _downloads;
    private int _operations;
    private bool _inPostInstall;

    /// <summary>How far along the transaction is: 0 to 100.</summary>
    public double Percent { get; private set; }

    /// <summary>Takes in one line of output.</summary>
    public void Read(string line)
    {
        if (backend == "pacman")
            ReadPacmanCounts(line);

        if (TransactionProgress.Read(backend, line) is { } progress)
        {
            _inPostInstall |= progress.Phase == TransactionPhase.PostInstall;
            Advance(progress.Phase switch
            {
                TransactionPhase.Initialize => Share(0, InitializeEnd, progress.Percent ?? 0),
                TransactionPhase.Install => Share(InitializeEnd, InstallEnd, progress.Percent ?? 0),
                _ => Share(InstallEnd, 100, progress.Percent ?? 0),
            });
        }
        else if (_inPostInstall && TransactionProgress.CountedPercent(line) is { } percent)
        {
            // pacman's hooks are counted, on lines that do not say they are hooks.
            Advance(Share(InstallEnd, 100, percent));
        }
    }

    private void ReadPacmanCounts(string line)
    {
        if (PacmanPlan.Match(line) is { Success: true } plan)
        {
            _packages = int.Parse(plan.Groups["count"].Value);
        }
        else if (_packages > 0 && PacmanDownload.IsMatch(line))
        {
            // Only after the plan: the same line is printed for each database when refreshing.
            _downloads++;
            Advance(Share(InitializeEnd, DownloadEnd, Math.Min(100.0 * _downloads / _packages, 100)));
        }
        else if (_packages > 0 && PackageOperations.Read(backend, line) is not null)
        {
            _operations++;
            Advance(Share(DownloadEnd, InstallEnd, Math.Min(100.0 * _operations / _packages, 100)));
        }
    }

    private void Advance(double percent) => Percent = Math.Max(Percent, percent);

    private static double Share(double start, double end, double percent) => start + (end - start) * percent / 100;
}
