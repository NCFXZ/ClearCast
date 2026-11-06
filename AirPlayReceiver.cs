using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace ClearCast;

internal enum DecoderMode { Automatic, Software, Hardware }

internal enum ReceiverEventKind
{
    Ready = 1,
    Connecting,
    Video,
    MirrorEnded,
    Failed,
    PairingRequired,
    PairingRejected,
    DecoderChanged,
    DecoderChangeFailed,
    PairingCompleted
}

internal readonly record struct ReceiverEvent(ReceiverEventKind Kind, int Width, int Height, int Codec, int ErrorCode);

internal readonly record struct PlaybackStats(ulong Rendered, ulong Dropped, int Width, int Height);

internal sealed record SavedPairing(string Id, string? Name);

internal sealed class AirPlayReceiver
{
    private const int PairingKeyLength = 44;

    public string Pin { get; }

    private AirPlayReceiver(string pin) => Pin = pin;

    public static AirPlayReceiver Start(string root, DecoderMode decoder, nint videoHost, ReceiverAdapter? adapter)
    {
        var (size, hevc) = SelectPictureQuality(adapter, decoder);

        string state = StateDirectory(root);
        Directory.CreateDirectory(state);
        string pin = RandomNumberGenerator.GetInt32(1, 10_000).ToString("0000", CultureInfo.InvariantCulture);
        int result = NativeReceiver.Start(state, size, 30, (int)decoder, hevc ? 1 : 0, pin, videoHost,
            adapter?.NativeAddress ?? 0);
        if (result != 0)
            throw new Win32Exception(result, ReceiverNetwork.GetFailureMessage(result, decoder));
        return new AirPlayReceiver(pin);
    }

    private static (string Size, bool Hevc) SelectPictureQuality(ReceiverAdapter? adapter, DecoderMode decoder)
    {
        const long megabit = 1_000_000;
        if (adapter is null || adapter.LinkSpeed <= 0)
            return ("1920x1080", false);

        // A negotiated Wi-Fi link rate includes overhead and is typically above usable throughput.
        bool fast = adapter.IsWireless ? adapter.LinkSpeed >= 400 * megabit
            : adapter.IsEthernet && adapter.LinkSpeed >= 100 * megabit;
        if (fast && decoder != DecoderMode.Software)
            return ("3840x2160", true);
        return adapter.LinkSpeed < 50 * megabit ? ("1280x720", false) : ("1920x1080", false);
    }

    public static void ClearPairings(string root)
    {
        try
        {
            File.Delete(PairingPath(root));
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    public static IReadOnlyList<SavedPairing> GetSavedPairings(string root)
    {
        var pairings = new Dictionary<string, SavedPairing>(StringComparer.Ordinal);
        Span<byte> decodedKey = stackalloc byte[32];
        foreach (string line in ReadPairingLines(PairingPath(root)))
        {
            if (line.Length < PairingKeyLength ||
                !Convert.TryFromBase64Chars(line.AsSpan(0, PairingKeyLength), decodedKey, out int bytes) ||
                bytes != decodedKey.Length)
                continue;

            // UxPlay writes key, timestamp, device ID, and name. Older records can have only the key.
            string id = line[..PairingKeyLength];
            string[] fields = line.Split(',', 4);
            string? name = fields.Length == 4 && fields[0].Length == PairingKeyLength ? fields[3].Trim() : null;
            if (!string.IsNullOrEmpty(name))
                pairings[id] = new(id, name);
            else
                pairings.TryAdd(id, new(id, null));
        }
        return [.. pairings.Values];
    }

    // The receiver must be stopped before replacing the file it appends to and caches in memory.
    public static void RemovePairing(string root, string id)
    {
        string path = PairingPath(root);
        List<string> lines = ReadPairingLines(path);
        if (lines.RemoveAll(line => line.Length >= PairingKeyLength &&
            line.AsSpan(0, PairingKeyLength).SequenceEqual(id)) == 0)
            return;

        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static List<string> ReadPairingLines(string path)
    {
        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is string line)
                lines.Add(line);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
        }
        return lines;
    }

    private static string PairingPath(string root) => Path.Combine(StateDirectory(root), "clients.txt");

    private static string StateDirectory(string root) => Path.Combine(root, ".clearcast-state");

    public bool TryReadEvent(out ReceiverEvent receiverEvent)
    {
        int result = NativeReceiver.ReadEvent(out int type, out int width, out int height, out int codec, out int error);
        receiverEvent = new ReceiverEvent((ReceiverEventKind)type, width, height, codec, error);
        return result == 1;
    }

    public bool TryReadStats(out PlaybackStats stats)
    {
        int result = NativeReceiver.ReadStats(out ulong rendered, out ulong dropped, out int width, out int height);
        stats = new PlaybackStats(rendered, dropped, width, height);
        return result == 1;
    }

    public void RequestDecoder(DecoderMode decoder)
    {
        int result = NativeReceiver.SetDecoder((int)decoder);
        if (result != 0)
            throw new Win32Exception(result, "无法切换解码方式。");
    }

    public void SetVideoRotation(int quarterTurnsClockwise)
    {
        int result = NativeReceiver.SetVideoRotation(quarterTurnsClockwise);
        if (result != 0)
            throw new Win32Exception(result, "无法旋转投屏画面。");
    }

    // Shutdown can wait for GStreamer and a Win32 message pump. Keep the UI thread available.
    public Task StopAsync() => Task.Run(() =>
    {
        int result = NativeReceiver.Stop();
        if (result != 0)
            throw new Win32Exception(result, "无法停止接收，请重试。");
    });
}
