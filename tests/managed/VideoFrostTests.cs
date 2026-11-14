using System.Drawing;

namespace ClearCast;

internal static class VideoFrostTests
{
    internal static void Run(Action<bool, string> check)
    {
        var renderer = new VideoFrost();
        var frame = new byte[ReceiverVideoFrame.ByteCount];
        for (int y = 0; y < ReceiverVideoFrame.Side; y++)
            for (int x = 0; x < ReceiverVideoFrame.Side; x++)
            {
                int offset = (y * ReceiverVideoFrame.Side + x) * 4;
                frame[offset] = (byte)(y >= 16 ? 255 : 0);
                frame[offset + 1] = (byte)(x >= 16 ? 255 : 0);
                frame[offset + 2] = (byte)((x < 16 && y < 16) || (x >= 16 && y >= 16) ? 255 : 0);
                frame[offset + 3] = 255;
            }
        var picture = new RectangleF(0, 0, 1000, 1000);
        var local = new RectangleF(100, 100, 96, 64);
        int center = (VideoFrost.Height / 2 * VideoFrost.Width + VideoFrost.Width / 2) * 4;
        bool Dominant(byte[] pixels, int channel) => pixels[center + channel] > 100 &&
            Enumerable.Range(0, 3).Where(value => value != channel).All(value => pixels[center + value] < 20);
        byte[] output = renderer.Render(frame, picture, local, 0);
        check(output.Length == VideoFrost.Width * VideoFrost.Height * 4 && Dominant(output, 2),
            "video frost samples the local image position instead of averaging the whole frame");
        check(Dominant(renderer.Render(frame, picture, local, 1), 0) &&
            renderer.Render(frame, picture, local, 2).AsSpan(center, 3).ToArray().All(value => value > 100) &&
            Dominant(renderer.Render(frame, picture, local, 3), 1),
            "video frost maps all four sink rotations back to the unrotated receiver sample");
        byte[] wrappedRotation = renderer.Render(frame, picture, local, 5).ToArray();
        check(wrappedRotation.SequenceEqual(renderer.Render(frame, picture, local, 1)),
            "video frost normalizes quarter-turn values without changing their mapping");
        output = renderer.Render(frame, new RectangleF(200, 0, 600, 1000), new RectangleF(0, 100, 96, 64), 0);
        check(Enumerable.Range(0, output.Length / 4).All(index =>
            output[index * 4] == 14 && output[index * 4 + 1] == 14 && output[index * 4 + 2] == 14 && output[index * 4 + 3] == 255),
            "video frost keeps letterboxing black beneath its dark tint and emits opaque BGRA");
        check(Dominant(renderer.Render(frame, new RectangleF(-500, 0, 1000, 1000), local, 0), 1),
            "video frost uses the complete native-size image rectangle when its left side is cropped");

        // The strip begins to the right of the red/green boundary. Its first pixels must
        // still receive blurred red from outside the card, while the interior stays green.
        output = renderer.Render(frame, picture, new RectangleF(520, 200, 400, 64), 0);
        int row = VideoFrost.Height / 2 * VideoFrost.Width * 4;
        check(output[row + 2] > output[row + 128 * 4 + 2] && output[row + 128 * 4 + 1] > 100,
            "video frost samples a surrounding halo so blur crosses the card edge without smearing the whole strip");
        Array.Clear(frame);
        for (int offset = 0; offset < frame.Length; offset += 4) { frame[offset] = 255; frame[offset + 3] = 255; }
        check(ReferenceEquals(output, renderer.Render(frame, picture, local, 0)) && Dominant(output, 0),
            "video frost updates the same bounded output buffer when video colors change");
        renderer.Render(frame, picture, local, 0);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int repeat = 0; repeat < 8; repeat++) renderer.Render(frame, picture, local, repeat);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        check(allocated == 0, "video frost rendering reuses its fixed working buffers without per-frame allocations");

        Array.Fill(frame, (byte)255);
        output = renderer.Render(frame, picture, local, 0);
        check(Enumerable.Range(0, output.Length / 4).All(index =>
            output[index * 4] <= 117 && output[index * 4 + 1] <= 117 && output[index * 4 + 2] <= 117 &&
            output[index * 4 + 3] == 255),
            "video frost keeps pure-white video dark enough for readable white transport labels");

        foreach (var invalid in new[] { RectangleF.Empty, new RectangleF(float.NaN, 0, 100, 100),
            new RectangleF(0, 0, float.PositiveInfinity, 100), new RectangleF(0, 0, -1, 100) })
        {
            output = renderer.Render(frame, invalid, local, 0);
            check(output[center] == 14 && output[center + 1] == 14 && output[center + 2] == 14 && output[center + 3] == 255,
                "video frost safely falls back for invalid image geometry " + invalid);
            output = renderer.Render(frame, picture, invalid, 0);
            check(output[center] == 14 && output[center + 3] == 255,
                "video frost safely falls back for invalid control geometry " + invalid);
        }
        output = renderer.Render([], picture, local, 0);
        check(output[center] == 14 && output[center + 3] == 255,
            "video frost safely falls back for a missing receiver sample");
        output = renderer.Render(frame, new RectangleF(float.MaxValue, float.MaxValue, float.Epsilon, float.Epsilon),
            new RectangleF(-float.MaxValue, -float.MaxValue, float.Epsilon, float.Epsilon), int.MinValue);
        check(output[center] == 14 && output[center + 3] == 255,
            "video frost bounds extreme finite coordinates and tiny sizes without allocating from geometry");

        // Alternate both convolution radii and the picture placement. A persistent UI renderer
        // must not retain the preceding frame when its active portion of the fixed buffers changes.
        var layouts = new[]
        {
            (Picture: new RectangleF(0, 0, 1920, 1080), Controls: new RectangleF(500, 900, 1000, 64)),
            (Picture: new RectangleF(-500, -100, 3840, 2160), Controls: new RectangleF(250, 900, 1000, 90)),
            (Picture: new RectangleF(600, 0, 720, 1280), Controls: new RectangleF(300, 1100, 1400, 64))
        };
        bool updatesValid = true;
        allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int update = 0; update < 96; update++)
        {
            byte color = (byte)(update % 2 == 0 ? 0 : 255);
            Array.Fill(frame, color);
            var layout = layouts[update % layouts.Length];
            output = renderer.Render(frame, layout.Picture, layout.Controls, update);
            updatesValid &= output[center] == (color == 0 ? 14 : 116) && output[center + 3] == 255;
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        check(updatesValid && allocated == 0,
            "video frost keeps fresh bounded output across repeated frame, rotation, size and clipped-layout changes");
    }
}
