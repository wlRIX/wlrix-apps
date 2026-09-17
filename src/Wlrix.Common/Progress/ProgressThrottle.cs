using System.Diagnostics;

namespace Wlrix.Common.Progress;

/// <summary>Rate-limits progress reports to something a UI can keep up with.</summary>
/// <remarks>
/// The inner loops that drive these run tens of thousands of times — once per 128 KB of a
/// multi-gigabyte decompression, once per file of a large copy — and each report crosses to
/// the UI thread. Unthrottled that is a flood of dispatcher work competing with the redraw it
/// is supposed to be driving, and the window ends up *less* responsive than with no progress
/// at all. Ten a second is more than the eye resolves on a progress bar.
///
/// <para>
/// Generic over the report type because the archiver and the file manager's operations engine
/// report different shapes and there is no reason for two copies of this.
/// </para>
/// </remarks>
public sealed class ProgressThrottle<T>(IProgress<T>? progress)
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    private readonly TimeSpan _interval = DefaultInterval;
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private bool _reported;

    /// <summary>Creates a throttle with a custom interval.</summary>
    public ProgressThrottle(IProgress<T>? progress, TimeSpan interval) : this(progress) => _interval = interval;

    /// <summary>Forwards <paramref name="value"/> if enough time has passed since the last one.</summary>
    public void Report(T value)
    {
        if (progress is null)
            return;

        // The first report goes straight through: the point of it is to replace "nothing is
        // happening" as fast as possible, and waiting out the interval to do that defeats it.
        if (_reported && _since.Elapsed < _interval)
            return;

        _reported = true;
        _since.Restart();
        progress.Report(value);
    }

    /// <summary>
    /// Forwards <paramref name="value"/> regardless of the interval.
    /// </summary>
    /// <remarks>
    /// For the last report of an operation, which must not be the one the throttle
    /// swallows — a progress bar left at 98% is worse than none.
    /// </remarks>
    public void ReportNow(T value)
    {
        if (progress is null)
            return;
        _reported = true;
        _since.Restart();
        progress.Report(value);
    }
}
