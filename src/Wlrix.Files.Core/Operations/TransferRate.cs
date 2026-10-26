namespace Wlrix.Files.Core.Operations;

/// <summary>How fast bytes are moving, over the last few seconds.</summary>
/// <remarks>
/// Over a window rather than since the start, so a share that stops answering shows its rate
/// falling to nothing within seconds. An average since the start would take minutes to sag on
/// a long copy, which is exactly the "has it died?" question the rate is there to answer.
///
/// <para>
/// The caller passes the time in, so this holds no clock of its own and the tests need none.
/// Samples come from progress reports, which stop when the bytes do, so the caller also samples
/// on a timer: those samples repeat the last count, and the rate falls as they fill the window.
/// </para>
/// </remarks>
public sealed class TransferRate(TimeSpan window)
{
    /// <summary>How long the samples have to span before a rate is worth showing.</summary>
    /// <remarks>
    /// The first report after a fast start can be thirty megabytes in a few milliseconds, which
    /// divides out to gigabytes a second. A second of history is enough to be believable.
    /// </remarks>
    private static readonly TimeSpan MinimumSpan = TimeSpan.FromSeconds(1);

    private readonly Queue<(TimeSpan At, long Bytes)> _samples = new();

    /// <summary>Records <paramref name="bytes"/> done so far, at <paramref name="at"/>.</summary>
    public void Add(TimeSpan at, long bytes)
    {
        // A count that goes backwards is a retry starting its file over. What came before says
        // nothing about the new attempt, so it starts from here.
        if (_samples.Count > 0 && bytes < _samples.Last().Bytes)
            _samples.Clear();

        _samples.Enqueue((at, bytes));

        // Dropped once the next sample is also out of the window, so the oldest one kept is the
        // last before the window opened, and the rate spans the whole window rather than less.
        while (_samples.Count > 2 && at - _samples.ElementAt(1).At >= window)
            _samples.Dequeue();
    }

    /// <summary>Bytes a second across the window, or null until there is a second to go on.</summary>
    public double? BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2)
                return null;
            var first = _samples.Peek();
            var last = _samples.Last();
            var span = last.At - first.At;
            if (span < MinimumSpan)
                return null;
            return (last.Bytes - first.Bytes) / span.TotalSeconds;
        }
    }

    public void Reset() => _samples.Clear();
}
