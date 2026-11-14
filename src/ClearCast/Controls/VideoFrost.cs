using System.Drawing;

namespace ClearCast;

/// <summary>A bounded, locally sampled video wash for the full-screen transport's native-window cutout.</summary>
internal sealed class VideoFrost
{
    internal const int Width = 256, Height = 48;
    private const int Passes = 3, MaximumRadius = 16;
    private const double BlurRadiusDips = 16;
    private const int MaximumWidth = Width + 2 * Passes * MaximumRadius;
    private const int MaximumHeight = Height + 2 * Passes * MaximumRadius;
    private readonly byte[] _sample = new byte[MaximumWidth * MaximumHeight * 4];
    private readonly byte[] _scratch = new byte[MaximumWidth * MaximumHeight * 4];
    private readonly byte[] _output = new byte[Width * Height * 4];

    /// <summary>
    /// Maps the unrotated receiver sample through the displayed image rectangle, including black
    /// letterboxing. The returned BGRA buffer is reused and is valid until the next call.
    /// </summary>
    internal byte[] Render(ReadOnlySpan<byte> source, RectangleF picture, RectangleF controls, int quarterTurns)
    {
        if (source.Length != ReceiverVideoFrame.ByteCount || !Valid(picture) || !Valid(controls))
        {
            for (int offset = 0; offset < _output.Length; offset += 4)
            {
                _output[offset] = _output[offset + 1] = _output[offset + 2] = Tint(0);
                _output[offset + 3] = 255;
            }
            return _output;
        }

        int radiusX = (int)Math.Ceiling(Math.Clamp(BlurRadiusDips * Width / controls.Width, 1, MaximumRadius));
        int radiusY = (int)Math.Ceiling(Math.Clamp(BlurRadiusDips * Height / controls.Height, 1, MaximumRadius));
        int padX = Passes * radiusX, padY = Passes * radiusY;
        int width = Width + 2 * padX, height = Height + 2 * padY;
        int rotation = (quarterTurns % 4 + 4) % 4;
        // Sampling the convolution halo prevents the card edges from clamping to just the
        // pixels inside the card. Native-size cropping uses the complete image rectangle.
        for (int y = 0; y < height; y++)
        {
            double displayY = controls.Y + (y - padY + 0.5) * controls.Height / Height;
            for (int x = 0; x < width; x++)
            {
                double displayX = controls.X + (x - padX + 0.5) * controls.Width / Width;
                double p = (displayX - picture.X) / picture.Width, q = (displayY - picture.Y) / picture.Height;
                int offset = (y * width + x) * 4;
                if (p < 0 || p > 1 || q < 0 || q > 1)
                {
                    _sample[offset] = _sample[offset + 1] = _sample[offset + 2] = 0;
                    continue;
                }
                (double u, double v) = rotation switch
                {
                    1 => (q, 1 - p), 2 => (1 - p, 1 - q), 3 => (1 - q, p), _ => (p, q)
                };
                double sx = Math.Clamp(u * ReceiverVideoFrame.Side - 0.5, 0, ReceiverVideoFrame.Side - 1);
                double sy = Math.Clamp(v * ReceiverVideoFrame.Side - 0.5, 0, ReceiverVideoFrame.Side - 1);
                int x0 = (int)sx, y0 = (int)sy;
                int x1 = Math.Min(x0 + 1, ReceiverVideoFrame.Side - 1), y1 = Math.Min(y0 + 1, ReceiverVideoFrame.Side - 1);
                double fx = sx - x0, fy = sy - y0;
                for (int channel = 0; channel < 3; channel++)
                {
                    double top = source[(y0 * ReceiverVideoFrame.Side + x0) * 4 + channel] * (1 - fx) +
                        source[(y0 * ReceiverVideoFrame.Side + x1) * 4 + channel] * fx;
                    double bottom = source[(y1 * ReceiverVideoFrame.Side + x0) * 4 + channel] * (1 - fx) +
                        source[(y1 * ReceiverVideoFrame.Side + x1) * 4 + channel] * fx;
                    _sample[offset + channel] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
                }
            }
        }
        // Three small separable boxes approximate a Gaussian; sliding sums keep work
        // proportional to the fixed texture area rather than the blur radius.
        for (int pass = 0; pass < Passes; pass++)
        {
            Blur(_sample, _scratch, width, height, radiusX, horizontal: true);
            Blur(_scratch, _sample, width, height, radiusY, horizontal: false);
        }
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int input = ((y + padY) * width + x + padX) * 4, output = (y * Width + x) * 4;
                for (int channel = 0; channel < 3; channel++) _output[output + channel] = Tint(_sample[input + channel]);
                _output[output + 3] = 255;
            }
        return _output;
    }

    private static bool Valid(RectangleF bounds) => float.IsFinite(bounds.X) && float.IsFinite(bounds.Y) &&
        float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height) && bounds.Width > 0 && bounds.Height > 0;

    // A translucent dark tint, baked against the sampled video, keeps white transport labels legible.
    private static byte Tint(byte value) => (byte)((value * 40 + 24 * 60 + 50) / 100);

    private static void Blur(byte[] source, byte[] target, int width, int height, int radius, bool horizontal)
    {
        int length = horizontal ? width : height, lines = horizontal ? height : width;
        int step = horizontal ? 4 : width * 4, divisor = radius * 2 + 1;
        for (int line = 0; line < lines; line++)
        {
            int start = horizontal ? line * width * 4 : line * 4;
            for (int channel = 0; channel < 3; channel++)
            {
                int sum = 0;
                for (int index = -radius; index <= radius; index++)
                    sum += source[start + Math.Clamp(index, 0, length - 1) * step + channel];
                for (int index = 0; index < length; index++)
                {
                    target[start + index * step + channel] = (byte)((sum + divisor / 2) / divisor);
                    sum -= source[start + Math.Max(0, index - radius) * step + channel];
                    sum += source[start + Math.Min(length - 1, index + radius + 1) * step + channel];
                }
            }
        }
    }
}
