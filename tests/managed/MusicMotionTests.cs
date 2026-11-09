namespace ClearCast;

internal static class MusicMotionTests
{
    internal static void Run(Action<bool, string> check)
    {
        var motion = new MusicMotion();
        check(motion.Rate == MusicMotion.BaseRate, "music motion starts at a slow baseline");
        float first = motion.Update(1, 1, true, TimeSpan.FromMilliseconds(150));
        check(first > MusicMotion.BaseRate && first < MusicMotion.MaximumRate,
            "a dense audio sample accelerates gradually instead of jumping to maximum pace");
        float afterQuiet = motion.Update(0, 0, true, TimeSpan.FromMilliseconds(150));
        check(afterQuiet > MusicMotion.BaseRate && afterQuiet < first,
            "quiet audio releases the motion pace gradually");

        float Settled(float energy, float density)
        {
            var candidate = new MusicMotion();
            for (int index = 0; index < 40; index++) candidate.Update(energy, density, true, TimeSpan.FromMilliseconds(150));
            return candidate.Rate;
        }
        float sustainedLoud = Settled(1, 0), denseQuiet = Settled(0.1f, 1);
        check(denseQuiet > sustainedLoud + 0.8f,
            "onset-dense quiet audio moves faster than a loud sustained tone");
        check(Settled(0.05f, 0.6f) > Settled(0.05f, 0.1f),
            "equal-energy audio responds to onset density");
        check(Math.Abs(Settled(1, 0.6f) - Settled(0.01f, 0.6f)) < 0.11f,
            "changing gain has only a small effect when onset density is unchanged");
        check(Settled(0, 0) == MusicMotion.BaseRate,
            "silence retains slow flow rather than inventing a rhythmic pulse");

        var frequent = new MusicMotion();
        for (int index = 0; index < 30; index++) frequent.Update(0.4f, 0.8f, true, TimeSpan.FromMilliseconds(50));
        var regular = new MusicMotion();
        for (int index = 0; index < 10; index++) regular.Update(0.4f, 0.8f, true, TimeSpan.FromMilliseconds(150));
        var irregular = new MusicMotion();
        foreach (int milliseconds in new[] { 20, 230, 410, 840 })
            irregular.Update(0.4f, 0.8f, true, TimeSpan.FromMilliseconds(milliseconds));
        check(Math.Abs(frequent.Rate - regular.Rate) < 0.00002f && Math.Abs(irregular.Rate - regular.Rate) < 0.00002f,
            "time-based smoothing behaves consistently across snapshot frequencies");
        float before = regular.Rate;
        check(regular.Update(0, 0, false, TimeSpan.Zero) == before &&
            regular.Update(0, 0, false, TimeSpan.FromSeconds(-1)) == before,
            "zero and negative elapsed time do not change the motion state");
        float missing = regular.Update(0, 0, false, TimeSpan.FromMilliseconds(150));
        check(missing < before && missing > MusicMotion.BaseRate,
            "missing or stale audio samples ease toward the baseline");
        for (int index = 0; index < 120; index++) regular.Update(0, 0, false, TimeSpan.FromMilliseconds(150));
        check(regular.Rate == MusicMotion.BaseRate, "missing samples settle fully at the baseline");

        foreach (var (energy, density) in new[] { (float.NaN, 1f), (1f, float.PositiveInfinity), (float.NegativeInfinity, 1f) })
        {
            motion.Reset();
            check(motion.Update(energy, density, true, TimeSpan.MaxValue) == MusicMotion.BaseRate,
                "non-finite audio activity cannot accelerate or poison the motion state");
        }
        check(float.IsFinite(motion.Update(float.MaxValue, float.MaxValue, true, TimeSpan.MaxValue)) &&
            motion.Rate >= MusicMotion.BaseRate && motion.Rate <= MusicMotion.MaximumRate,
            "oversized activity and elapsed time remain bounded and finite");
        motion.Reset();
        check(motion.Rate == MusicMotion.BaseRate, "pause or session reset restores the baseline rate");
    }
}
