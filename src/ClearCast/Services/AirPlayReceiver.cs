using System.Buffers.Text;
using System.ComponentModel;
using System.IO;
using System.Text;

namespace ClearCast;

internal enum DecoderMode { Automatic, Software, Hardware }
internal enum MediaCommand { PlayPause = 1, Previous, Next, Seek, Skip = 5 }

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
    PairingCompleted,
    ConnectionRejected,
    PairingAuthenticationRequired = 12
}

internal enum PairingRejectionReason
{
    InvalidSetupData = 1,
    ProofRejected,
    PinUnavailableOrExpired,
    RateLimited,
    VerifyRejected
}

// Pairing events use Height for the challenge generation; zero is unassociated.
// PairingRequired carries the PIN in Width, and PairingRejected carries its reason in ErrorCode.
internal readonly record struct ReceiverEvent(ReceiverEventKind Kind, int Width, int Height, int Codec, int ErrorCode);

internal readonly record struct PlaybackStats(ulong Rendered, ulong Dropped, int Width, int Height);

internal sealed record SavedPairing(string Id, string? Name);

// Commands are not replaceable snapshots. Reject excess work before scheduling
// a native/network call rather than building a thread-pool queue behind it.
internal sealed class ReceiverCommandGate
{
    private int _pending;
    private int _generation;
    private int _suspended;

    internal void Suspend()
    {
        Volatile.Write(ref _suspended, 1);
        Interlocked.Increment(ref _generation);
    }

    internal void Resume() => Volatile.Write(ref _suspended, 0);

    internal Task RunAsync(Action command)
    {
        int generation = Volatile.Read(ref _generation);
        if (Volatile.Read(ref _suspended) != 0)
            return Task.FromException(new Win32Exception(1223)); // ERROR_CANCELLED
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
            return Task.FromException(new Win32Exception(170)); // ERROR_BUSY
        try
        {
            return Task.Run(() =>
            {
                try
                {
                    if (Volatile.Read(ref _suspended) != 0 || generation != Volatile.Read(ref _generation))
                        throw new Win32Exception(1223);
                    command();
                }
                finally { Volatile.Write(ref _pending, 0); }
            });
        }
        catch
        {
            Volatile.Write(ref _pending, 0);
            throw;
        }
    }
}

internal sealed class AirPlayReceiver
{
    private const int PairingKeyLength = 44;
    private const int SharingViolation = 32;
    private const int MaximumPairingFileBytes = 16 * 1024 * 1024;
    private const int MaximumPairings = 4096;
    private const int MaximumPairingRowBytes = 4096;

    private readonly FileStream _stateLock;
    private readonly byte[] _mediaBuffer = new byte[8192];
    private readonly ReceiverCommandGate _mediaCommands = new();
    private readonly object _stopLock = new();
    private Task? _stopTask;

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
        var pairings = new Dictionary<(string Id, string? Name), SavedPairing>();
        var namedKeys = new HashSet<string>(StringComparer.Ordinal);
        // Receiver records are below 1 KiB. Keep a bounded prefix even if a damaged
        // local file contains an oversized metadata line, while still retaining its key.
        foreach (var row in ReadPairingRows(PairingPath(root)))
        {
            if (GetPairingKey(row.Span) is not { } id)
                continue;
            string? name = GetPairingName(row.Span);
            if (!string.IsNullOrEmpty(name))
            {
                // A trust key can be reused by different named senders. Keep their
                // names instead of replacing every earlier name with the last one.
                namedKeys.Add(id);
                pairings.Remove((id, null));
            }
            else
            {
                if (namedKeys.Contains(id)) continue;
                name = null;
            }
            var identity = (id, name);
            if (pairings.Count == MaximumPairings && !pairings.ContainsKey(identity))
                throw new IOException("The saved pairing registry contains too many devices.");
            pairings.TryAdd(identity, new(id, name));
        }
        return [.. pairings.Values];
    }

    // The receiver must be stopped before replacing the file it appends to and caches in memory.
    public static void RemovePairing(string root, string id) => RemovePairing(root, id, null, false);

    public static void RemovePairing(string root, string id, string? storedName) =>
        RemovePairing(root, id, NormalizePairingName(storedName), true);

    private static void RemovePairing(string root, string id, string? storedName, bool matchName)
    {
        using var stateLock = AcquirePairingLock(root);
        if (stateLock is null)
            return;

        string path = PairingPath(root);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            bool removed;
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            {
                removed = CopyRetainedPairings(path, stream, id, storedName, matchName);
            }
            if (removed)
                // Preserve the receiver identity so unrelated saved devices can
                // continue verifying it after this record has been revoked.
                File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string? NormalizePairingName(string? name)
    {
        name = name?.Trim().Replace('\r', ' ');
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string? GetPairingName(ReadOnlySpan<byte> row)
    {
        // UxPlay writes key, timestamp, device ID, and name. Names may contain commas.
        string[] fields = Encoding.UTF8.GetString(row).Split(',', 4);
        return fields.Length == 4 ? NormalizePairingName(fields[3]) : null;
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

    private static bool CopyRetainedPairings(string path, Stream output, string id, string? storedName, bool matchName)
    {
        FileStream input;
        try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
        using (input)
        {
            byte[] buffer = new byte[8192], prefix = new byte[matchName ? MaximumPairingRowBytes + 1 : PairingKeyLength + 2];
            int prefixLength = 0, count;
            long remaining = input.Length;
            bool decided = false, discard = false, removed = false;
            bool Discard(ReadOnlySpan<byte> row, bool complete)
            {
                if (GetPairingKey(row) != id) return false;
                if (!matchName) { removed = true; return true; }
                // Preserve an oversized, ambiguous metadata row rather than
                // matching its truncated name to a different complete name.
                if (!complete) return false;
                string? rowName = GetPairingName(row);
                bool selected = rowName == storedName;
                removed |= selected;
                // PIN placeholders must not survive as a wildcard grant after
                // deleting a named record. They are committed only if found.
                return selected || rowName is null;
            }
            while (remaining > 0 && (count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) != 0)
            {
                remaining -= count;
                for (int offset = 0; offset < count;)
                {
                    int end = Array.IndexOf(buffer, (byte)'\n', offset, count - offset);
                    int next = end == -1 ? count : end + 1;
                    if (!decided)
                    {
                        int retained = Math.Min(next - offset, prefix.Length - prefixLength);
                        buffer.AsSpan(offset, retained).CopyTo(prefix.AsSpan(prefixLength));
                        prefixLength += retained;
                        offset += retained;
                        if (prefixLength == prefix.Length || end != -1)
                        {
                            discard = Discard(prefix.AsSpan(0, prefixLength), end != -1 && offset == next);
                            decided = true;
                            if (!discard) output.Write(prefix.AsSpan(0, prefixLength));
                        }
                    }
                    if (decided && !discard) output.Write(buffer.AsSpan(offset, next - offset));
                    offset = next;
                    if (end != -1) { prefixLength = 0; decided = discard = false; }
                }
            }
            if (remaining != 0) throw new IOException("The saved pairing registry changed while being read.");
            if (!decided && prefixLength > 0)
            {
                if (!Discard(prefix.AsSpan(0, prefixLength), true)) output.Write(prefix.AsSpan(0, prefixLength));
            }
            return removed;
        }
    }

    private static IEnumerable<ReadOnlyMemory<byte>> ReadPairingRows(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            yield break;
        }
        using (stream)
        using (var row = new MemoryStream())
        {
            long remaining = stream.Length;
            if (remaining > MaximumPairingFileBytes)
                throw new IOException("The saved pairing registry is too large.");
            byte[] buffer = new byte[8192];
            int count;
            while (remaining > 0 && (count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) != 0)
            {
                remaining -= count;
                for (int offset = 0; offset < count;)
                {
                    int end = Array.IndexOf(buffer, (byte)'\n', offset, count - offset);
                    int next = end == -1 ? count : end + 1;
                    int length = next - offset;
                    int retained = Math.Min(length, Math.Max(0, MaximumPairingRowBytes - (int)row.Length));
                    row.Write(buffer, offset, retained);
                    offset = next;
                    if (end != -1)
                    {
                        yield return row.ToArray();
                        row.SetLength(0);
                    }
                }
            }
            if (remaining != 0) throw new IOException("The saved pairing registry changed while being read.");
            if (row.Length != 0)
                yield return row.ToArray();
        }
    }

    private static string PairingPath(string root) => Path.Combine(StateDirectory(root), "clients.txt");

    private static string StateDirectory(string root) => Path.Combine(root, ".clearcast-state");

    public static string ReadDiagnostics(string root)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(StateDirectory(root), "receiver.log"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Keep copied reports small even after a long session.
            if (stream.Length > 16 * 1024)
                stream.Seek(-16 * 1024, SeekOrigin.End);
            bool partialLine = stream.Position > 0;
            byte[] bytes = new byte[16 * 1024];
            int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            ReadOnlySpan<byte> tail = bytes.AsSpan(0, count);
            if (partialLine)
            {
                int end = tail.IndexOf((byte)'\n');
                tail = end < 0 ? [] : tail[(end + 1)..];
            }
            return Encoding.UTF8.GetString(tail).TrimStart('\uFEFF');
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

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

    public bool TryReadMedia(out ReceiverMediaState state)
    {
        int length = NativeReceiver.ReadMedia(_mediaBuffer, _mediaBuffer.Length);
        state = ReceiverMediaState.Empty;
        return length is > 1 and <= 8192 && _mediaBuffer[length - 1] == 0 &&
            ReceiverMediaState.TryParse(_mediaBuffer.AsSpan(0, length - 1), out state);
    }

    public byte[]? ReadArtwork(ulong revision, int length)
    {
        if (revision == 0 || length is <= 0 or > 2 * 1024 * 1024)
            return null;
        var buffer = new byte[length];
        return NativeReceiver.ReadArtwork(revision, buffer, length) == length ? buffer : null;
    }

    public ReceiverVideoFrame? ReadVideoFrame()
    {
        var pixels = new byte[ReceiverVideoFrame.ByteCount];
        return NativeReceiver.ReadVideoFrame(pixels, pixels.Length, out ulong generation) == pixels.Length && generation != 0
            ? new ReceiverVideoFrame(generation, pixels) : null;
    }

    public Task SendMediaCommandAsync(MediaCommand command) => _mediaCommands.RunAsync(() =>
    {
        int result = NativeReceiver.MediaCommand((int)command);
        if (result != 0)
            throw new Win32Exception(result, "无法发送播放指令，请在设备上操作。");
    });

    public Task ControlMediaAsync(ReceiverMediaState state, MediaCommand command, long value = 0) => _mediaCommands.RunAsync(() =>
    {
        int result = NativeReceiver.MediaControl(state.ControlSession, state.ContentRevision, (int)command, value);
        if (result != 0) throw new Win32Exception(result);
    });

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
            throw new Win32Exception(result, "无法旋转屏幕镜像画面。");
    }

    // Shutdown can wait for GStreamer and a Win32 message pump. Keep the UI thread available.
    public Task StopAsync()
    {
        lock (_stopLock)
        {
            if (_stopTask is not null) return _stopTask;
            _mediaCommands.Suspend();
            return _stopTask = Task.Run(() =>
            {
                try
                {
                    int result = NativeReceiver.Stop();
                    if (result != 0) throw new Win32Exception(result, "无法停止接收，请重试。");
                    _stateLock.Dispose();
                }
                catch
                {
                    lock (_stopLock)
                    {
                        _mediaCommands.Resume();
                        _stopTask = null;
                    }
                    throw;
                }
            });
        }
    }
}
