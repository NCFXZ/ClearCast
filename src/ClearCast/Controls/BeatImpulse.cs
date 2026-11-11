using System.Numerics;

namespace ClearCast;

internal sealed class BeatImpulse
{
    internal const int DurationMs = 360;
    internal const int MinimumIntervalMs = 200;
    internal const float MinimumStrength = 0.08f;
    internal const float BaselineScale = 1.05f;
    internal const float ZoomAmount = 0.12f;
    private static readonly (float Progress, float Value)[] Envelope = CreateEnvelope();

    private static (float Progress, float Value)[] CreateEnvelope()
    {
        // Sample one damped impulse once, then let Composition interpolate it.
        // A shared curve avoids stopping at every hand-authored rebound point.
        var frames = new (float Progress, float Value)[33];
        float peak = 0;
        for (int index = 0; index < frames.Length; index++)
        {
            float progress = (float)index / (frames.Length - 1);
            float tail = Math.Clamp((progress - 0.8f) / 0.2f, 0, 1);
            float fade = 1 - tail * tail * (3 - 2 * tail);
            float value = MathF.Exp(-6 * progress) * MathF.Sin(3 * MathF.PI * progress) * fade;
            frames[index] = (progress, index == 0 || index == frames.Length - 1 ? 0 : value);
            peak = Math.Max(peak, value);
        }
        for (int index = 0; index < frames.Length; index++)
            frames[index].Value /= peak;
        return frames;
    }

    private long _startedAt;
    private bool _hasStarted;
    internal ulong Epoch { get; private set; }
    internal ulong Sequence { get; private set; }
    internal static ReadOnlySpan<(float Progress, float Value)> KeyFrames => Envelope;

    internal bool TryStart(ulong epoch, ulong sequence, float strength, bool fresh, bool enabled,
        long nowMs, out float amplitude, out bool epochChanged)
    {
        amplitude = 0;
        epochChanged = false;
        if (epoch == 0 || epoch < Epoch) return false;
        if (epoch > Epoch)
        {
            Epoch = epoch;
            Sequence = 0;
            Reset();
            epochChanged = true;
        }
        if (sequence == 0 || sequence <= Sequence) return false;
        // Consume even stale, disabled and refractory events. Nothing is queued
        // to replay when another polling pass or a resumed window arrives.
        Sequence = sequence;
        if (!fresh || !enabled || !float.IsFinite(strength) || strength < MinimumStrength || nowMs < 0)
            return false;
        if (_hasStarted && (nowMs < _startedAt || nowMs - _startedAt < MinimumIntervalMs))
            return false;
        float bounded = Math.Clamp(strength, 0, 1);
        amplitude = bounded * bounded * (3 - 2 * bounded);
        _startedAt = nowMs;
        _hasStarted = true;
        return true;
    }

    internal bool IsActive(long nowMs) => _hasStarted && nowMs >= _startedAt && nowMs - _startedAt < DurationMs;

    internal void Reset()
    {
        _hasStarted = false;
        _startedAt = 0;
        // Retain Epoch/Sequence so reset, pause and artwork changes cannot replay
        // an onset that was already observed before the boundary.
    }

    internal static Vector2 OffsetLimit(float width, float height)
    {
        float Limit(float size) => float.IsFinite(size) ? Math.Clamp(size * 0.008f, 0, 10) : 0;
        return new(Limit(width), Limit(height));
    }
}
