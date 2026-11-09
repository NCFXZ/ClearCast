using System.Numerics;

namespace ClearCast;

internal static class BeatImpulseTests
{
    internal static void Run(Action<bool, string> check)
    {
        var beat = new BeatImpulse();
        bool Start(ulong epoch, ulong sequence, long now, bool fresh = true, bool enabled = true, float strength = 1) =>
            beat.TryStart(epoch, sequence, strength, fresh, enabled, now, out _, out _);
        check(Start(1, 1, 0) && beat.IsActive(0) && beat.IsActive(BeatImpulse.DurationMs - 1) && !beat.IsActive(BeatImpulse.DurationMs),
            "a new beat schedules exactly one bounded impulse");
        check(!Start(1, 1, 50) && !Start(1, 0, 60) && beat.IsActive(100),
            "duplicate and empty polls do not restart or cut short an active impulse");
        check(!Start(1, 2, 100) && beat.Sequence == 2 && !Start(1, 2, 250),
            "a refractory event is consumed and cannot be replayed later");
        check(Start(1, 3, 250), "a distinct beat can trigger after the refractory interval");
        check(!Start(1, 4, 300, fresh: false) && beat.Sequence == 4 && beat.IsActive(350) && !Start(1, 4, 500),
            "same-epoch stale events are consumed without stopping the current rebound");
        check(!Start(1, 5, 510, enabled: false) && beat.Sequence == 5 && !Start(1, 5, 750),
            "disabled motion consumes onsets without replay on reactivation");
        beat.Reset();
        check(!beat.IsActive(300) && beat.Epoch == 1 && beat.Sequence == 5 && !Start(1, 5, 800),
            "explicit reset stops the impulse but preserves deduplication state");
        check(Start(1, 6, 850), "a new onset remains usable after reset");
        check(!Start(2, 0, 900, fresh: false) && beat.Epoch == 2 && beat.Sequence == 0 && !beat.IsActive(900),
            "a newer empty epoch clears the old impulse at a FLUSH boundary");
        check(!Start(1, 99, 950) && !Start(0, 99, 950) && beat.Epoch == 2 && beat.Sequence == 0,
            "old and invalid epochs cannot move deduplication state backwards");
        check(Start(2, 1, 1000) && !Start(2, 0, 1050, fresh: false) && beat.IsActive(1100),
            "an empty poll in the same epoch does not stop an active pulse");
        check(!Start(2, 10, 1300, fresh: false) && !Start(2, 9, 1400) && Start(2, 11, 1400),
            "only the newest sequence is consumed and skipped events never form a catch-up burst");
        check(beat.TryStart(3, 1, 4, true, true, 1500, out float clamped, out bool changed) && clamped == 1 && changed,
            "new epochs can restart numbering while finite oversized strength is bounded");
        foreach (float invalid in new[] { 0, -1, float.NaN, float.PositiveInfinity })
        {
            ulong next = beat.Sequence + 1;
            check(!Start(3, next, 2000, strength: invalid) && beat.Sequence == next,
                "invalid or silent impulse strength is consumed without motion " + invalid);
        }
        ulong weakSequence = beat.Sequence + 1;
        check(!Start(3, weakSequence, 2100, strength: 0.02f) && beat.Sequence == weakSequence &&
            !Start(3, weakSequence, 2400, strength: 1),
            "a near-invisible onset is consumed and cannot be replayed later with stronger data");
        var cadence = new BeatImpulse();
        for (int index = 0; index < 10; index++)
            check(cadence.TryStart(1, (ulong)index + 1, 1, true, true, index * 333, out _, out _),
                "180BPM strong beats are not lost to the visual refractory interval " + index);
        var dense = new BeatImpulse();
        int accepted = 0;
        for (int index = 0; index < 20; index++)
            if (dense.TryStart(1, (ulong)index + 1, 1, true, true, index * 50, out _, out _)) accepted++;
        check(accepted == 5 && dense.Sequence == 20,
            "dense 50ms events are bounded to five pulses per second without a replay queue");

        var keyFrames = BeatImpulse.KeyFrames.ToArray();
        check(keyFrames[0] == (0, 0) && keyFrames[^1] == (1, 0) && Math.Abs(keyFrames[^2].Value) < 0.002f,
            "the damped impulse converges gently to a zero-value finite endpoint");
        var peak = keyFrames.MaxBy(frame => frame.Value);
        var trough = keyFrames.MinBy(frame => frame.Value);
        check(peak.Progress * BeatImpulse.DurationMs is >= 20 and <= 65 && peak.Value == 1 &&
            trough.Progress > peak.Progress && trough.Value is > -0.20f and < -0.05f &&
            keyFrames.Where(frame => frame.Progress > 0.75f).All(frame => Math.Abs(frame.Value) < 0.025f),
            "a quick strong attack leads into a smaller rebound and a quiet settling tail");
        var strengths = new BeatImpulse();
        check(strengths.TryStart(1, 1, 0.2f, true, true, 0, out float soft, out _) &&
            strengths.TryStart(2, 1, 0.8f, true, true, 1, out float strong, out _) && soft < 0.2f && strong > 0.8f,
            "perceptual strength mapping keeps soft beats restrained and makes strong beats more pronounced");
        foreach (Vector2 size in new[] { new Vector2(1), new Vector2(274, 180), new Vector2(640, 480), new Vector2(3840, 2160) })
        {
            Vector2 limits = BeatImpulse.OffsetLimit(size.X, size.Y);
            foreach (var (_, value) in BeatImpulse.KeyFrames)
                foreach (float strength in new[] { 0f, 0.25f, 1f })
                {
                    float impulse = value * strength;
                    float scale = BeatImpulse.BaselineScale + BeatImpulse.ZoomAmount * impulse;
                    Vector2 margin = size * ((scale - 1) * 0.5f);
                    Vector2 offset = limits * Math.Abs(impulse);
                    check(scale > 1 && scale <= 1.171f && offset.X <= margin.X && offset.Y <= margin.Y,
                        "zoom and rebound overscan cover every translated edge " + size + " " + impulse);
                }
        }
        check(BeatImpulse.OffsetLimit(float.NaN, float.PositiveInfinity) == Vector2.Zero,
            "invalid geometry cannot produce an unbounded compositor offset");
    }
}
