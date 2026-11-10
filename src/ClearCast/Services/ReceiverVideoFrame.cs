namespace ClearCast;

internal sealed record ReceiverVideoFrame(ulong Generation, byte[] Pixels)
{
    internal const int Side = 32;
    internal const int ByteCount = Side * Side * 4;
}
