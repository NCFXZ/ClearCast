namespace ClearCast;

internal readonly record struct MusicPalette(uint Background, uint First, uint Second, uint Third)
{
    internal static MusicPalette FromBgra(ReadOnlySpan<byte> pixels, bool darkTheme)
    {
        pixels = pixels[..Math.Min(pixels.Length, 32 * 32 * 4)];
        Span<double> weights = stackalloc double[512];
        Span<double> reds = stackalloc double[512];
        Span<double> greens = stackalloc double[512];
        Span<double> blues = stackalloc double[512];
        weights.Clear(); reds.Clear(); greens.Clear(); blues.Clear();
        for (int index = 0; index + 3 < pixels.Length; index += 4)
        {
            int alpha = pixels[index + 3];
            if (alpha < 32)
                continue;
            int red = pixels[index + 2], green = pixels[index + 1], blue = pixels[index];
            int maximum = Math.Max(red, Math.Max(green, blue)), minimum = Math.Min(red, Math.Min(green, blue));
            // White-heavy covers still have identifiable ink/accent colors.
            // Keep neutral-only artwork neutral without letting its paper drown those colors.
            double chroma = (maximum - minimum) / 255.0;
            double weight = alpha / 255.0 * (0.04 + 2.5 * chroma);
            int bucket = (red >> 5) * 64 + (green >> 5) * 8 + (blue >> 5);
            weights[bucket] += weight;
            reds[bucket] += red * weight;
            greens[bucket] += green * weight;
            blues[bucket] += blue * weight;
        }

        Span<(double Red, double Green, double Blue)> colors = stackalloc (double, double, double)[3];
        colors.Fill((128, 128, 128));
        double dominantWeight = 0;
        for (int color = 0; color < colors.Length; color++)
        {
            int best = -1;
            double bestScore = 0;
            for (int bucket = 0; bucket < weights.Length; bucket++)
            {
                double weight = weights[bucket];
                if (weight <= 0 || (color > 0 && weight < dominantWeight * 0.08))
                    continue;
                double red = reds[bucket] / weight, green = greens[bucket] / weight, blue = blues[bucket] / weight;
                double distance = 1;
                for (int previous = 0; previous < color; previous++)
                {
                    var chosen = colors[previous];
                    distance = Math.Min(distance, (Math.Abs(red - chosen.Red) + Math.Abs(green - chosen.Green) + Math.Abs(blue - chosen.Blue)) / 765.0);
                }
                double score = weight * (color == 0 ? 1 : distance);
                if (score > bestScore)
                {
                    best = bucket;
                    bestScore = score;
                }
            }
            if (best < 0)
            {
                if (color > 0) colors[color] = colors[0];
                continue;
            }
            colors[color] = (reds[best] / weights[best], greens[best] / weights[best], blues[best] / weights[best]);
            if (color == 0) dominantWeight = weights[best];
        }

        uint Tone((double Red, double Green, double Blue) source, double amount)
        {
            var neutral = darkTheme ? (Red: 18.0, Green: 20.0, Blue: 24.0) : (Red: 247.0, Green: 247.0, Blue: 249.0);
            byte Channel(double value, double background) => (byte)Math.Clamp(Math.Round(background + (value - background) * amount), 0, 255);
            double red = Channel(source.Red, neutral.Red), green = Channel(source.Green, neutral.Green), blue = Channel(source.Blue, neutral.Blue);
            double brightness = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
            if (darkTheme && brightness > 98)
            {
                double scale = 98 / brightness;
                red *= scale; green *= scale; blue *= scale;
            }
            return 0xFF000000u | ((uint)Math.Round(red) << 16) | ((uint)Math.Round(green) << 8) | (uint)Math.Round(blue);
        }
        return new(Tone(colors[0], darkTheme ? 0.16 : 0.12), Tone(colors[0], darkTheme ? 0.42 : 0.26),
            Tone(colors[1], darkTheme ? 0.42 : 0.26), Tone(colors[2], darkTheme ? 0.42 : 0.26));
    }
}
