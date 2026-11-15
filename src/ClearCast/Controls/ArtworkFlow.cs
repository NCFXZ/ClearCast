using System.Numerics;

namespace ClearCast;

internal static class ArtworkFlow
{
    internal const int Side = 96;
    internal const int FrameCount = 32;
    internal const int TwistCount = 8;

    internal static (float Begin, float End) LayerWindow(int layer)
    {
        if (layer is < 0 or > FrameCount) throw new ArgumentOutOfRangeException(nameof(layer));
        return (Math.Max(0, layer - 1f) / FrameCount, Math.Min(1, (layer + 1f) / FrameCount));
    }

    internal static float LayerOpacity(int layer, double phase)
    {
        if (!double.IsFinite(phase) || phase is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(phase));
        var window = LayerWindow(layer);
        if (layer == 0) return phase < window.End ? 1 : 0;
        if (layer < FrameCount && phase >= window.End) return 0;
        return Math.Clamp(((float)phase - window.Begin) * FrameCount, 0, 1);
    }

    internal static byte[][] Generate(byte[] source, int width, int height, bool darkTheme, CancellationToken cancellation = default)
    {
        Validate(source, width, height);
        var frames = new byte[FrameCount][];
        for (int frame = 0; frame < FrameCount; frame++)
        {
            cancellation.ThrowIfCancellationRequested();
            frames[frame] = GenerateFrame(source, width, height, (double)frame / FrameCount, darkTheme, cancellation);
        }
        return frames;
    }

    internal static byte[] GenerateFrame(byte[] source, int width, int height, double phase, bool darkTheme,
        CancellationToken cancellation = default)
    {
        Validate(source, width, height);
        if (!double.IsFinite(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        phase -= Math.Floor(phase);
        float time = (float)(phase * Math.Tau);
        var centers = new Vector2[TwistCount];
        var strengths = new float[TwistCount];
        var radii = new float[TwistCount];
        for (int twist = 0; twist < TwistCount; twist++)
        {
            float angle = twist * MathF.Tau / TwistCount;
            centers[twist] = new Vector2(MathF.Cos(angle) * 0.62f + 0.20f * MathF.Sin(time + angle * 2),
                MathF.Sin(angle) * 0.58f + 0.22f * MathF.Cos(time * 2 - angle));
            strengths[twist] = 2.8f * MathF.Sin(time + angle * 1.5f);
            radii[twist] = 0.82f + 0.25f * MathF.Sin(time * 2 + angle);
        }
        var copyAngles = new float[3];
        var copyOffsets = new Vector2[3];
        for (int copy = 0; copy < 3; copy++)
        {
            float offset = copy * MathF.Tau / 3;
            copyAngles[copy] = 0.55f * MathF.Sin(time + offset);
            copyOffsets[copy] = new Vector2(0.38f * MathF.Cos(time + offset), 0.34f * MathF.Sin(time * 2 + offset));
        }
        var pixels = new float[Side * Side * 3];
        for (int y = 0; y < Side; y++)
        {
            if (y % 8 == 0) cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < Side; x++)
            {
                var point = new Vector2((x + 0.5f) * 2 / Side - 1, (y + 0.5f) * 2 / Side - 1);
                for (int twist = 0; twist < TwistCount; twist++)
                {
                    Vector2 delta = point - centers[twist];
                    float distance = delta.LengthSquared() / (radii[twist] * radii[twist]);
                    if (distance < 1)
                    {
                        float amount = 1 - distance;
                        point = centers[twist] + Rotate(delta, strengths[twist] * amount * amount);
                    }
                }
                Vector3 color = Vector3.Zero;
                for (int copy = 0; copy < 3; copy++)
                {
                    Vector2 sample = Rotate(point, copyAngles[copy]) * (0.70f + copy * 0.10f) + copyOffsets[copy];
                    color += Sample(source, width, height, sample * 0.5f + new Vector2(0.5f)) *
                        (copy == 0 ? 0.45f : copy == 1 ? 0.35f : 0.20f);
                }
                int index = (y * Side + x) * 3;
                pixels[index] = color.X;
                pixels[index + 1] = color.Y;
                pixels[index + 2] = color.Z;
            }
        }
        Blur(pixels, cancellation);
        var result = new byte[Side * Side * 4];
        for (int pixel = 0; pixel < Side * Side; pixel++)
        {
            var raw = new Vector3(pixels[pixel * 3], pixels[pixel * 3 + 1], pixels[pixel * 3 + 2]);
            float sourceBrightness = 0.2126f * raw.X + 0.7152f * raw.Y + 0.0722f * raw.Z;
            var gray = new Vector3(sourceBrightness);
            raw = Vector3.Clamp(gray + (raw - gray) * 1.12f, Vector3.Zero, new Vector3(255));
            var neutral = new Vector3(18, 20, 24);
            var toned = Vector3.Lerp(neutral, raw, 0.58f);
            float brightness = 0.2126f * toned.X + 0.7152f * toned.Y + 0.0722f * toned.Z;
            if (brightness > 98)
            {
                toned *= 98 / brightness;
                brightness = 98;
            }
            if (!darkTheme)
            {
                // Derive light mode from the finished dark texture, preserving its
                // color and tonal structure while making dark foreground text readable.
                const float darkFloor = (18 * 0.2126f + 20 * 0.7152f + 24 * 0.0722f) * (1 - 0.58f);
                float lightBrightness = 165 + MathF.Pow(Math.Clamp((brightness - darkFloor) / (98 - darkFloor), 0, 1), 1.2f) * 82;
                Vector3 chroma = toned - new Vector3(brightness);
                float peak = Math.Max(chroma.X, Math.Max(chroma.Y, chroma.Z));
                float trough = Math.Min(chroma.X, Math.Min(chroma.Y, chroma.Z));
                float strength = Math.Min(1, Math.Min(peak > 0 ? (255 - lightBrightness) / peak : 1,
                    trough < 0 ? lightBrightness / -trough : 1));
                toned = new Vector3(lightBrightness) + chroma * strength;
            }
            result[pixel * 4] = (byte)Math.Clamp(MathF.Round(toned.Z), 0, 255);
            result[pixel * 4 + 1] = (byte)Math.Clamp(MathF.Round(toned.Y), 0, 255);
            result[pixel * 4 + 2] = (byte)Math.Clamp(MathF.Round(toned.X), 0, 255);
            result[pixel * 4 + 3] = 255;
        }
        return result;
    }

    private static Vector2 Rotate(Vector2 point, float angle)
    {
        float sine = MathF.Sin(angle), cosine = MathF.Cos(angle);
        return new(point.X * cosine - point.Y * sine, point.X * sine + point.Y * cosine);
    }

    private static Vector3 Sample(byte[] source, int width, int height, Vector2 point)
    {
        // Mirrored edges keep moving copies seamless without importing pixels
        // from outside the bounded decoded cover.
        float Mirror(float value) { value -= 2 * MathF.Floor(value / 2); return value <= 1 ? value : 2 - value; }
        float x = Mirror(point.X) * (width - 1), y = Mirror(point.Y) * (height - 1);
        int left = (int)x, top = (int)y, right = Math.Min(left + 1, width - 1), bottom = Math.Min(top + 1, height - 1);
        Vector3 Pixel(int column, int row)
        {
            int index = (row * width + column) * 4;
            float alpha = source[index + 3] / 255f;
            return Vector3.Lerp(new Vector3(128), new Vector3(source[index + 2], source[index + 1], source[index]), alpha);
        }
        return Vector3.Lerp(Vector3.Lerp(Pixel(left, top), Pixel(right, top), x - left),
            Vector3.Lerp(Pixel(left, bottom), Pixel(right, bottom), x - left), y - top);
    }

    private static void Blur(float[] pixels, CancellationToken cancellation)
    {
        const int radius = 12;
        Span<float> weights = stackalloc float[radius * 2 + 1];
        float total = 0;
        for (int index = -radius; index <= radius; index++)
        {
            float weight = MathF.Exp(-(index * index) / 50f);
            weights[index + radius] = weight;
            total += weight;
        }
        for (int index = 0; index < weights.Length; index++) weights[index] /= total;
        var intermediate = new float[pixels.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int y = 0; y < Side; y++)
            {
                if (y % 8 == 0) cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < Side; x++)
                    for (int channel = 0; channel < 3; channel++)
                    {
                        float value = 0;
                        for (int offset = -radius; offset <= radius; offset++)
                        {
                            int sampleX = pass == 0 ? Math.Clamp(x + offset, 0, Side - 1) : x;
                            int sampleY = pass == 0 ? y : Math.Clamp(y + offset, 0, Side - 1);
                            value += pixels[(sampleY * Side + sampleX) * 3 + channel] * weights[offset + radius];
                        }
                        intermediate[(y * Side + x) * 3 + channel] = value;
                    }
            }
            (pixels, intermediate) = (intermediate, pixels);
        }
    }

    private static void Validate(byte[] source, int width, int height)
    {
        if (width is < 1 or > 128 || height is < 1 or > 128 || source.Length != width * height * 4)
            throw new ArgumentException("Artwork flow requires a bounded BGRA sample.");
    }
}
