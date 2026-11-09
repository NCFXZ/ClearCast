using System.Text;
using System.Text.Json;

namespace ClearCast;

internal enum ReceiverMediaKind { None, Audio, Video }

[Flags]
internal enum MediaControls { None = 0, PlayPause = 1, Previous = 2, Next = 4 }

internal readonly record struct ReceiverAudioBeat(ulong Sequence, float Strength, int AgeMs)
{
    internal const int MaximumAgeMs = 250;

    internal bool IsFresh(TimeSpan elapsed) => Sequence > 0 && float.IsFinite(Strength) && Strength is >= 0 and <= 1 &&
        AgeMs is >= 0 and < MaximumAgeMs &&
        Math.Max(0, elapsed.Ticks) < (long)(MaximumAgeMs - AgeMs) * TimeSpan.TicksPerMillisecond;

    internal float StrengthAt(TimeSpan elapsed) => IsFresh(elapsed)
        ? Strength * MathF.Exp(-(AgeMs + (float)Math.Max(0, elapsed.TotalMilliseconds)) / 180f) : 0;
}

internal readonly record struct ReceiverAudioActivity(bool Valid, float Energy, float Density, int AgeMs)
{
    internal const int MaximumAgeMs = 900;
    internal ulong BeatEpoch { get; init; }
    internal ReceiverAudioBeat[]? Beats { get; init; }

    internal bool IsFresh(TimeSpan elapsed) => Valid &&
        float.IsFinite(Energy) && Energy is >= 0 and <= 1 &&
        float.IsFinite(Density) && Density is >= 0 and <= 1 &&
        AgeMs is >= 0 and < MaximumAgeMs &&
        Math.Max(0, elapsed.Ticks) < (long)(MaximumAgeMs - AgeMs) * TimeSpan.TicksPerMillisecond;
}

internal sealed record ReceiverMediaState(
    ulong Revision,
    ReceiverMediaKind Kind,
    bool Playing,
    string Title,
    string Artist,
    string Album,
    long PositionMs,
    long DurationMs,
    ulong ArtworkRevision,
    int ArtworkBytes,
    MediaControls Controls)
{
    private const int MaximumSnapshotBytes = 8192;
    private const int MaximumTextLength = 1024;
    private const int MaximumArtworkBytes = 2 * 1024 * 1024;
    private const long MaximumDurationMs = 7L * 24 * 60 * 60 * 1000;
    private const MediaControls KnownControls = MediaControls.PlayPause | MediaControls.Previous | MediaControls.Next;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ulong Session { get; init; }
    public bool Connected { get; init; }
    public ReceiverAudioActivity AudioActivity { get; init; }

    internal static ReceiverMediaState Empty { get; } = new(0, ReceiverMediaKind.None, false, "", "", "", 0, 0, 0, 0, MediaControls.None);

    internal static bool TryParse(ReadOnlySpan<byte> snapshot, out ReceiverMediaState state)
    {
        state = Empty;
        if (snapshot.IsEmpty || snapshot.Length > MaximumSnapshotBytes)
            return false;
        if (snapshot[^1] == 0)
            snapshot = snapshot[..^1];
        else if (snapshot.Length == MaximumSnapshotBytes)
            return false; // The native ABI reserves one byte for the NUL terminator.

        try
        {
            StrictUtf8.GetCharCount(snapshot);
            var reader = new Utf8JsonReader(snapshot, new JsonReaderOptions { MaxDepth = 4 });
            using var document = JsonDocument.ParseValue(ref reader);
            if (reader.Read() || document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            var root = document.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name))
                    return false;

            if (!TryUInt64(root, "revision", out ulong revision) ||
                !TryText(root, "kind", out string kindText) ||
                !root.TryGetProperty("playing", out var playingValue) ||
                playingValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !TryText(root, "title", out string title) ||
                !TryText(root, "artist", out string artist) ||
                !TryText(root, "album", out string album) ||
                !TryInt64(root, "positionMs", out long position) ||
                !TryInt64(root, "durationMs", out long duration) ||
                duration is < 0 or > MaximumDurationMs ||
                !TryUInt64(root, "artworkRevision", out ulong artworkRevision) ||
                !TryInt64(root, "artworkBytes", out long artworkBytes) ||
                artworkBytes is < 0 or > MaximumArtworkBytes ||
                !TryUInt64(root, "controls", out ulong controls) || controls > uint.MaxValue)
                return false;
            ulong session = 0;
            if (root.TryGetProperty("session", out _) && !TryUInt64(root, "session", out session))
                return false;
            if (!TryAudioActivity(root, out var activity))
                return false;

            ReceiverMediaKind kind;
            switch (kindText)
            {
                case "none": kind = ReceiverMediaKind.None; break;
                case "audio": kind = ReceiverMediaKind.Audio; break;
                case "video": kind = ReceiverMediaKind.Video; break;
                default: return false;
            }
            bool connected = kind != ReceiverMediaKind.None;
            if (root.TryGetProperty("connected", out var connectedValue))
            {
                if (connectedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return false;
                connected = connectedValue.GetBoolean();
            }

            position = Math.Clamp(position, 0, duration > 0 ? duration : MaximumDurationMs);
            state = new(revision, kind, kind != ReceiverMediaKind.None && playingValue.GetBoolean(),
                title, artist, album, position, duration, artworkRevision, (int)artworkBytes,
                kind == ReceiverMediaKind.Audio ? (MediaControls)(controls & (ulong)KnownControls) : MediaControls.None)
            {
                Session = session,
                Connected = connected,
                AudioActivity = kind == ReceiverMediaKind.Audio && playingValue.GetBoolean() ? activity : default
            };
            return true;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryAudioActivity(JsonElement root, out ReceiverAudioActivity activity)
    {
        activity = default;
        if (!root.TryGetProperty("audioActivity", out var value))
            return true;
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name))
                return false;
        if (!value.TryGetProperty("valid", out var valid) ||
            valid.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !TryUnitInterval(value, "energy", out float energy) ||
            !TryUnitInterval(value, "density", out float density) ||
            !value.TryGetProperty("ageMs", out var ageValue) || ageValue.ValueKind != JsonValueKind.Number ||
            !ageValue.TryGetInt32(out int age) || age < 0)
            return false;
        if (!TryBeatHistory(value, out ulong epoch, out var beats))
            return false;
        if (valid.GetBoolean())
            activity = new(true, energy, density, age) { BeatEpoch = epoch, Beats = beats };
        else if (epoch != 0)
            // A playing source can flush before it has another decoded sample.
            // Keep only its validated generation to cancel old visual impulses.
            activity = new(false, 0, 0, age) { BeatEpoch = epoch };
        return true;
    }

    private static bool TryBeatHistory(JsonElement activity, out ulong epoch, out ReceiverAudioBeat[]? beats)
    {
        epoch = 0;
        beats = null;
        bool haveEpoch = activity.TryGetProperty("beatEpoch", out _);
        bool haveBeats = activity.TryGetProperty("beats", out var history);
        if (!haveEpoch && !haveBeats) return true;
        if (!haveEpoch || !haveBeats || !TryUInt64(activity, "beatEpoch", out epoch) ||
            history.ValueKind != JsonValueKind.Array || history.GetArrayLength() > 16 ||
            (epoch == 0 && history.GetArrayLength() != 0))
            return false;
        beats = new ReceiverAudioBeat[history.GetArrayLength()];
        ulong previous = 0;
        int index = 0;
        foreach (var item in history.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
                if (!names.Add(property.Name)) return false;
            if (!TryUInt64(item, "sequence", out ulong sequence) || sequence <= previous ||
                !TryUnitInterval(item, "strength", out float strength) ||
                !item.TryGetProperty("ageMs", out var ageValue) || ageValue.ValueKind != JsonValueKind.Number ||
                !ageValue.TryGetInt32(out int age) || age < 0)
                return false;
            beats[index++] = new(sequence, strength, age);
            previous = sequence;
        }
        return true;
    }

    private static bool TryUnitInterval(JsonElement root, string name, out float value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number ||
            !element.TryGetDouble(out double number) || !double.IsFinite(number) || number is < 0 or > 1)
            return false;
        value = (float)number;
        return true;
    }

    internal long PositionAt(TimeSpan elapsed)
    {
        long limit = DurationMs > 0 ? Math.Min(DurationMs, MaximumDurationMs) : MaximumDurationMs;
        long position = Math.Clamp(PositionMs, 0, limit);
        if (Kind != ReceiverMediaKind.Audio || !Playing || elapsed <= TimeSpan.Zero)
            return position;
        long advance = elapsed.Ticks / TimeSpan.TicksPerMillisecond;
        return position + Math.Min(advance, limit - position);
    }

    private static bool TryText(JsonElement root, string name, out string text)
    {
        text = "";
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        text = value.GetString()!;
        return text.Length <= MaximumTextLength;
    }

    private static bool TryUInt64(JsonElement root, string name, out ulong number)
    {
        number = 0;
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out number);
    }

    private static bool TryInt64(JsonElement root, string name, out long number)
    {
        number = 0;
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number);
    }
}
