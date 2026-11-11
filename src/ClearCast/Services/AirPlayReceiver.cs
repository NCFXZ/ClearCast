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
        var pairings = new Dictionary<string, SavedPairing>(StringComparer.Ordinal);
        // Receiver records are below 1 KiB. Keep a bounded prefix even if a damaged
        // local file contains an oversized metadata line, while still retaining its key.
        foreach (var row in ReadPairingRows(PairingPath(root), maximumRowBytes: 4096))
        {
            if (GetPairingKey(row.Span) is not { } id)
                continue;
            if (pairings.Count == MaximumPairings && !pairings.ContainsKey(id))
                throw new IOException("The saved pairing registry contains too many devices.");

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
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            bool removed;
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            {
                removed = CopyRetainedPairings(path, stream, id);
            }
            if (removed)
                CommitPairingRemoval(root, temporary, path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void CommitPairingRemoval(string root, string temporary, string path)
    {
        // Apple caches the receiver public key. Retire that identity as well as
        // revoking the client key, so a removed device can request a new PIN.
        // Keep unique staging files until clients.txt is committed, so any I/O
        // failure can restore both the live identity and its previous backup.
        string state = StateDirectory(root);
        string identity = Path.Combine(state, "receiver.pem");
        string retired = Path.Combine(state, "receiver.unpaired.pem");
        string transaction = Guid.NewGuid().ToString("N");
        string stagedIdentity = identity + "." + transaction + ".tmp";
        string stagedBackup = retired + "." + transaction + ".tmp";
        bool identityStaged = false, backupStaged = false, identityRetired = false, committed = false;
        try
        {
            identityStaged = MoveIfPresent(identity, stagedIdentity);
            if (identityStaged)
            {
                backupStaged = MoveIfPresent(retired, stagedBackup);
                File.Move(stagedIdentity, retired);
                identityRetired = true;
            }
            File.Move(temporary, path, overwrite: true);
            committed = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var rollbackErrors = new List<Exception>();
            bool identityRestored = !identityStaged;
            if (identityStaged)
            {
                try
                {
                    File.Move(identityRetired ? retired : stagedIdentity, identity, overwrite: true);
                    identityRestored = true;
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(rollback);
                }
            }
            // Do not overwrite the only surviving identity if restoring it failed.
            if (backupStaged && identityRestored)
            {
                try { File.Move(stagedBackup, retired, overwrite: true); }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(rollback);
                }
            }
            if (rollbackErrors.Count != 0)
                throw new IOException("Could not restore the pairing identity after a failed removal.",
                    new AggregateException(new[] { error }.Concat(rollbackErrors)));
            throw;
        }
        finally
        {
            if (committed)
            {
                // A delayed antivirus scan may hold a retired backup briefly. A
                // cleanup failure must not turn a completed revocation into an error.
                try { File.Delete(stagedBackup); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static bool MoveIfPresent(string source, string destination)
    {
        try
        {
            File.Move(source, destination);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
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

    private static bool CopyRetainedPairings(string path, Stream output, string id)
    {
        FileStream input;
        try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
        using (input)
        {
            byte[] buffer = new byte[8192], prefix = new byte[PairingKeyLength + 2];
            int prefixLength = 0, count;
            long remaining = input.Length;
            bool decided = false, discard = false, removed = false;
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
                            discard = GetPairingKey(prefix.AsSpan(0, prefixLength)) == id;
                            removed |= discard;
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
                if (GetPairingKey(prefix.AsSpan(0, prefixLength)) == id) removed = true;
                else output.Write(prefix.AsSpan(0, prefixLength));
            }
            return removed;
        }
    }

    private static IEnumerable<ReadOnlyMemory<byte>> ReadPairingRows(string path, int? maximumRowBytes = null)
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
                    int retained = maximumRowBytes is int limit
                        ? Math.Min(length, Math.Max(0, limit - (int)row.Length)) : length;
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
