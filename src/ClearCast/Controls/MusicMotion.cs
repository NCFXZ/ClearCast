namespace ClearCast;

internal sealed class MusicMotion
{
    internal const float BaseRate = 0.55f;
    internal const float MaximumRate = 1.90f;
    internal float Rate { get; private set; } = BaseRate;

    internal float Update(float energy, float density, bool hasSample, TimeSpan elapsed)
    {
        float target = BaseRate;
        if (hasSample && float.IsFinite(energy) && float.IsFinite(density))
        {
            // Onset density controls the pace. Loud sustained audio alone has
            // little influence, and silence still retains a gentle base flow.
            float activity = 0.92f * Math.Clamp(density, 0, 1) +
                0.08f * MathF.Sqrt(Math.Clamp(energy, 0, 1));
            target += (MaximumRate - BaseRate) * activity;
        }
        double seconds = Math.Clamp(elapsed.TotalSeconds, 0, 5);
        double responseSeconds = target > Rate ? 0.35 : 1.4;
        Rate += (target - Rate) * (float)(1 - Math.Exp(-seconds / responseSeconds));
        Rate = Math.Clamp(Rate, BaseRate, MaximumRate);
        if (Math.Abs(Rate - target) < 0.0001f) Rate = target;
        return Rate;
    }

    internal void Reset() => Rate = BaseRate;
}
