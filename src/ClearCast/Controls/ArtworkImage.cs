using Windows.Graphics.Imaging;

namespace ClearCast;

internal static class ArtworkImage
{
    internal static bool IsSupported(BitmapDecoder decoder)
    {
        Guid format = decoder.DecoderInformation.CodecId;
        return (format == BitmapDecoder.PngDecoderId || format == BitmapDecoder.JpegDecoderId) &&
            decoder.PixelWidth is > 0 and <= 4096 && decoder.PixelHeight is > 0 and <= 4096 &&
            (ulong)decoder.PixelWidth * decoder.PixelHeight <= 16_000_000;
    }
}
