namespace ClearCast;

// Only auxiliary visual work uses this policy. Playback clocks and encoded
// audio/video admission are deliberately independent of its cadence.
internal sealed class AdaptiveWorkCadence
{
    private const int SlowSamples = 3, RecoverySamples = 16;
    private readonly int _minimum, _maximum;
    private int _slow, _healthy;

    internal AdaptiveWorkCadence(int minimumMilliseconds, int maximumMilliseconds)
    {
        if (minimumMilliseconds <= 0 || maximumMilliseconds < minimumMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(minimumMilliseconds));
        _minimum = minimumMilliseconds;
        _maximum = maximumMilliseconds;
        IntervalMilliseconds = _minimum;
    }

    internal int IntervalMilliseconds { get; private set; }
    internal int Multiplier => Math.Max(1, IntervalMilliseconds / _minimum);
    internal bool IsThrottled => IntervalMilliseconds > _minimum;

    internal void Observe(TimeSpan work)
    {
        if (work < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(work));
        if (work.TotalMilliseconds >= IntervalMilliseconds * 0.75)
        {
            _healthy = 0;
            if (++_slow < SlowSamples) return;
            IntervalMilliseconds = (int)Math.Min(_maximum, (long)IntervalMilliseconds * 2);
            _slow = 0;
        }
        else
        {
            _slow = 0;
            if (work.TotalMilliseconds > IntervalMilliseconds * 0.25) { _healthy = 0; return; }
            if (++_healthy < RecoverySamples) return;
            IntervalMilliseconds = Math.Max(_minimum, IntervalMilliseconds / 2);
            _healthy = 0;
        }
    }

    internal void Reset()
    {
        IntervalMilliseconds = _minimum;
        _slow = _healthy = 0;
    }
}
