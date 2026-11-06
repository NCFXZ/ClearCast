using System.Buffers.Text;
using System.ComponentModel;
using System.IO;
using System.Text;

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
    private const int SharingViolation = 32;

    private readonly FileStream _stateLock;

    private AirPlayReceiver(FileStream stateLock) => _stateLock = stateLock;

    public static AirPlayReceiver Start(string root, DecoderMode decoder, nint videoHost, ReceiverAdapter? adapter)
    {
        var (size, hevc) = SelectPictureQuality(adapter, decoder);

        string state = StateDirectory(root);
        Directory.CreateDirectory(state);
        FileStream stateLock;
        try
        {
            stateLock = AcquireStateLock(state);
        }
        catch (IOException error) when (error.HResult == unchecked((int)0x80070000) + SharingViolation)
        {
            throw new Win32Exception(SharingViolation, "另一个 ClearCast 窗口正在使用接收器。请关闭它后重试。");
        }

        try
        {
            var receiver = new AirPlayReceiver(stateLock);
            // The receiver generates a fresh PIN per challenge. The start ABI still accepts a PIN argument.
            int result = NativeReceiver.Start(state, size, 30, (int)decoder, hevc ? 1 : 0, "0001", videoHost,
                adapter?.NativeAddress ?? 0);
            if (result != 0)
                throw new Win32Exception(result, ReceiverNetwork.GetFailureMessage(result, decoder));
            return receiver;
        }
        catch
        {
            stateLock.Dispose();
            throw;
        }
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
        using var stateLock = AcquirePairingLock(root);
        if (stateLock is not null)
            File.Delete(PairingPath(root));
    }

    public static IReadOnlyList<SavedPairing> GetSavedPairings(string root)
    {
        var pairings = new Dictionary<string, SavedPairing>(StringComparer.Ordinal);
        foreach (var row in ReadPairingRows(PairingPath(root)))
        {
            if (GetPairingKey(row.Span) is not { } id)
                continue;

            // UxPlay writes key, timestamp, device ID, and name. Older records can have only the key.
            string[] fields = Encoding.UTF8.GetString(row.Span).Split(',', 4);
            string? name = fields.Length == 4 ? fields[3].Trim().Replace('\r', ' ') : null;
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
        using var stateLock = AcquirePairingLock(root);
        if (stateLock is null)
            return;

        string path = PairingPath(root);
        var rows = ReadPairingRows(path).ToList();
        if (rows.RemoveAll(row => GetPairingKey(row.Span) == id) == 0)
            return;

        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            {
                foreach (var row in rows)
                    stream.Write(row.Span);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string? GetPairingKey(ReadOnlySpan<byte> row)
    {
        // Keep the same LF-delimited record and key boundary as the native receiver.
        if (row.EndsWith("\r\n"u8))
            row = row[..^2];
        else if (row.EndsWith("\n"u8))
            row = row[..^1];

        if (row.Length < PairingKeyLength ||
            (row.Length > PairingKeyLength && row[PairingKeyLength] != (byte)',') ||
            row[PairingKeyLength - 1] != (byte)'=' ||
            !Base64.IsValid(row[..PairingKeyLength], out int decodedLength) || decodedLength != 32)
            return null;

        return Encoding.ASCII.GetString(row[..PairingKeyLength]);
    }

    private static IEnumerable<ReadOnlyMemory<byte>> ReadPairingRows(string path)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var snapshot = new MemoryStream();
            stream.CopyTo(snapshot);
            bytes = snapshot.ToArray();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            yield break;
        }
        for (int offset = 0; offset < bytes.Length;)
        {
            int end = Array.IndexOf(bytes, (byte)'\n', offset);
            int next = end == -1 ? bytes.Length : end + 1;
            yield return bytes.AsMemory(offset, next - offset);
            offset = next;
        }
    }

    private static string PairingPath(string root) => Path.Combine(StateDirectory(root), "clients.txt");

    private static string StateDirectory(string root) => Path.Combine(root, ".clearcast-state");

    // A second ClearCast process can browse saved devices, but cannot change keys cached by an active receiver.
    private static FileStream AcquireStateLock(string state) => new(Path.Combine(state, "receiver.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

    private static FileStream? AcquirePairingLock(string root)
    {
        try
        {
            return AcquireStateLock(StateDirectory(root));
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

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
        _stateLock.Dispose();
    });
}
