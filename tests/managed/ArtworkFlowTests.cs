namespace ClearCast;

internal static class ArtworkFlowTests
{
    internal static void Run(Action<bool, string> check)
    {
        double Linear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        double PrimaryContrast(byte[] frame, int index)
        {
            double luminance = 0.2126 * Linear(frame[index + 2] / 255.0) +
                0.7152 * Linear(frame[index + 1] / 255.0) + 0.0722 * Linear(frame[index] / 255.0);
            return (luminance + 0.05) / (Linear(26 / 255.0) + 0.05);
        }
        const int side = 32;
        var artwork = new byte[side * side * 4];
        var rearranged = new byte[artwork.Length];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int index = (y * side + x) * 4;
                artwork[index] = (byte)(x < side / 2 ? 240 : 20);
                artwork[index + 1] = (byte)(y < side / 2 ? 30 : 210);
                artwork[index + 2] = (byte)(x < side / 2 ? 30 : 220);
                artwork[index + 3] = 255;
            }
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                artwork.AsSpan((y * side + x) * 4, 4).CopyTo(rearranged.AsSpan(((x * side + y) * 4), 4));
        var first = ArtworkFlow.GenerateFrame(artwork, side, side, 0, false);
        check(first.SequenceEqual(ArtworkFlow.GenerateFrame(artwork, side, side, 1, false)),
            "artwork warp returns to the same pixels at the loop seam");
        check(!first.SequenceEqual(ArtworkFlow.GenerateFrame(rearranged, side, side, 0, false)),
            "equal-palette artwork with different spatial structure produces different flow textures");
        var moved = ArtworkFlow.GenerateFrame(artwork, side, side, 0.25, false);
        check(first.Where((_, index) => index % 4 != 3).Zip(moved.Where((_, index) => index % 4 != 3),
                (left, right) => Math.Abs(left - right)).Average() > 1,
            "local coordinate twists change the actual cover texture across phases");
        check(Enumerable.Range(0, ArtworkFlow.Side * ArtworkFlow.Side).All(pixel => first[pixel * 4 + 3] == 255 && moved[pixel * 4 + 3] == 255),
            "artwork flow frames stay fully opaque for source-over interpolation");

        // The same layer windows initialize the actual visuals and generate the
        // GPU expressions. Exercise every boundary and midpoint, including wrap.
        for (int sample = 0; sample <= ArtworkFlow.FrameCount * 4; sample++)
        {
            double phase = (double)sample / (ArtworkFlow.FrameCount * 4);
            int visible = 0;
            double alpha = 0;
            for (int layer = 0; layer <= ArtworkFlow.FrameCount; layer++)
            {
                double opacity = ArtworkFlow.LayerOpacity(layer, phase);
                if (opacity > 0) visible++;
                alpha = opacity + alpha * (1 - opacity);
            }
            check(visible is >= 1 and <= 2 && alpha == 1,
                "phase blend keeps at most two visible layers and never exposes the base at sample " + sample);
        }
        check(ArtworkFlow.LayerOpacity(0, 0) == 1 && ArtworkFlow.LayerOpacity(ArtworkFlow.FrameCount, 1) == 1,
            "loop endpoints display the same first texture through the bottom and alias layers");
        check(ArtworkFlow.LayerOpacity(7, 7.5 / ArtworkFlow.FrameCount) == 1 &&
            ArtworkFlow.LayerOpacity(8, 7.5 / ArtworkFlow.FrameCount) == 0.5f,
            "the source-over midpoint uses an opaque lower frame and half-opacity incoming frame");
        var dark = ArtworkFlow.GenerateFrame(artwork, side, side, 0.25, true);
        check(Enumerable.Range(0, ArtworkFlow.Side * ArtworkFlow.Side).All(pixel => PrimaryContrast(first, pixel * 4) >= 7) &&
            Enumerable.Range(0, ArtworkFlow.Side * ArtworkFlow.Side).All(pixel =>
                dark[pixel * 4 + 2] * 0.2126 + dark[pixel * 4 + 1] * 0.7152 + dark[pixel * 4] * 0.0722 <= 99),
            "light flow keeps strong dark-text contrast and dark flow preserves readable brightness without removing cover chroma");
        var transparent = ArtworkFlow.GenerateFrame(new byte[4], 1, 1, 0, true);
        check(Enumerable.Range(0, ArtworkFlow.Side * ArtworkFlow.Side).All(pixel =>
            Math.Abs(transparent[pixel * 4] - transparent[pixel * 4 + 2]) <= 6),
            "transparent artwork becomes a neutral opaque wash");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool cancellationObserved = false;
        try { ArtworkFlow.Generate(artwork, side, side, false, canceled.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        check(cancellationObserved, "superseded cover generation cancels before building a frame bank");
        bool invalidRejected = false;
        try { ArtworkFlow.GenerateFrame([], 129, 129, 0, false); }
        catch (ArgumentException) { invalidRejected = true; }
        check(invalidRejected, "artwork warp rejects invalid dimensions and pixel buffers");
        var frames = ArtworkFlow.Generate(artwork, side, side, false);
        check(frames.Length == 32 && frames.All(frame => frame.Length == 96 * 96 * 4) &&
            frames.Sum(frame => frame.Length / 4) <= 500_000,
            "artwork phase bank stays within its fixed frame count and pixel budget");
        check(frames.Select(frame => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(frame))).Distinct().Count() > 24,
            "the phase bank contains distinct deformed cover textures rather than a repeated static palette");

        // A muted cover must retain visible color/motion structure after blur,
        // while deep source shadows cannot turn the light UI into a dark page.
        var mutedArtwork = new byte[96 * 96 * 4];
        for (int y = 0; y < 96; y++)
            for (int x = 0; x < 96; x++)
            {
                uint color = x < 16 || x > 83 - y / 4 ? 0xFF382D32u : 0xFFCCA9A4u;
                if (x is >= 68 and < 85 && y is >= 18 and < 82) color = 0xFF20ADB9u;
                if (x < 30 && y > 68) color = 0xFF9896A0u;
                int index = (y * 96 + x) * 4;
                mutedArtwork[index] = (byte)color;
                mutedArtwork[index + 1] = (byte)(color >> 8);
                mutedArtwork[index + 2] = (byte)(color >> 16);
                mutedArtwork[index + 3] = 255;
            }
        var mutedFrames = ArtworkFlow.Generate(mutedArtwork, 96, 96, false);
        double colorSpan = 0, temporalDelta = 0, minimumContrast = double.MaxValue;
        for (int frame = 0; frame < mutedFrames.Length; frame++)
        {
            byte[] current = mutedFrames[frame], next = mutedFrames[(frame + 1) % mutedFrames.Length];
            for (int index = 0; index < current.Length; index += 4)
            {
                colorSpan += Math.Max(current[index], Math.Max(current[index + 1], current[index + 2])) -
                    Math.Min(current[index], Math.Min(current[index + 1], current[index + 2]));
                temporalDelta += Math.Abs(current[index] - next[index]) + Math.Abs(current[index + 1] - next[index + 1]) +
                    Math.Abs(current[index + 2] - next[index + 2]);
                minimumContrast = Math.Min(minimumContrast, PrimaryContrast(current, index));
            }
        }
        double sampleCount = (double)mutedFrames.Length * ArtworkFlow.Side * ArtworkFlow.Side;
        check(colorSpan / sampleCount > 12 && temporalDelta / (sampleCount * 3) > 4,
            "muted pink brown and teal artwork retains visible light-theme color and flow rather than a nearly uniform wash");
        check(minimumContrast >= 7,
            "every light-theme phase preserves dark-label contrast even over source shadow regions");
        var blackCover = ArtworkFlow.GenerateFrame([0, 0, 0, 255], 1, 1, 0, false);
        check(PrimaryContrast(blackCover, 0) >= 7 && Math.Abs(blackCover[0] - blackCover[2]) <= 3,
            "a black cover stays a readable neutral light surface without invented saturated color");
    }
}
