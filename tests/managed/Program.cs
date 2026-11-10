using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClearCast;

using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These tests require Windows file-sharing semantics.");
string? output = args.Length == 2 && args[0] == "--output" ? Path.GetFullPath(args[1]) : null;
string disposable = Directory.CreateTempSubdirectory("ClearCast-managed-state-").FullName;
try
{
    var result = RunTests(disposable);
    string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (output is not null) { Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllText(output, json); }
}
catch (Exception error)
{
    string json = JsonSerializer.Serialize(new { status = "fail", failure = error.ToString() }, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (output is not null) { Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllText(output, json); }
    Environment.ExitCode = 1;
}
finally { Directory.Delete(disposable, recursive: true); }

static object RunTests(string root)
{
    Directory.CreateDirectory(Path.Combine(root, ".clearcast-state"));
    string state = Path.Combine(root, ".clearcast-state");
    string clients = Path.Combine(state, "clients.txt");
    int checks = 0;
    void Check(bool test, string name) { ++checks; if (!test) throw new InvalidOperationException(name); }
    string Key(int shift) => Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + shift)).ToArray());
    var watch = Stopwatch.StartNew();
    string key1 = Key(0), key2 = Key(1), key3 = Key(2), key4 = Key(3);

    File.WriteAllText(clients, key1 + "\r\n" + key2 + ",2026-11-07 00:00:00,AA:BB,设备,名称\n" + key2 + ",date,id,\n" + "invalid-row\n", new UTF8Encoding(false));
    var pairings = AirPlayReceiver.GetSavedPairings(root);
    Check(pairings.Count == 2, "valid unique keys only");
    Check(pairings.Single(p => p.Id == key2).Name == "设备,名称", "non-empty UTF-8 metadata and commas retained");
    Check(pairings.Single(p => p.Id == key1).Name is null, "legacy CRLF key-only row");
    AirPlayReceiver.RemovePairing(root, key2);
    Check(File.ReadAllText(clients) == key1 + "\r\ninvalid-row\n", "remove duplicate key while preserving unrelated bytes");
    byte[] beforeMissing = File.ReadAllBytes(clients);
    AirPlayReceiver.RemovePairing(root, key4);
    Check(File.ReadAllBytes(clients).SequenceEqual(beforeMissing), "missing key leaves file unchanged");
    Check(Directory.GetFiles(state, "*.tmp").Length == 0, "temporary files removed");

    string identity = Path.Combine(state, "receiver.pem");
    string retiredIdentity = Path.Combine(state, "receiver.unpaired.pem");
    byte[] liveKey = Encoding.UTF8.GetBytes("fixture-live-identity");
    byte[] oldBackup = Encoding.UTF8.GetBytes("fixture-previous-retired-identity");
    string rowA = key1 + ",date,id-A,设备 A\n";
    string rowB = key2 + ",date,id-B,设备 B,名称\r\n";
    void PrepareRemoval()
    {
        File.WriteAllText(clients, rowA + rowB + "unrelated legacy row\n", new UTF8Encoding(false));
        File.WriteAllBytes(identity, liveKey);
        File.WriteAllBytes(retiredIdentity, oldBackup);
    }
    void CheckRollback(string name)
    {
        Check(File.ReadAllText(clients) == rowA + rowB + "unrelated legacy row\n", name + " clients unchanged");
        Check(File.ReadAllBytes(identity).SequenceEqual(liveKey), name + " live identity restored");
        Check(File.ReadAllBytes(retiredIdentity).SequenceEqual(oldBackup), name + " old backup restored");
        Check(Directory.GetFiles(state, "*.tmp").Length == 0, name + " no staging files remain");
    }
    PrepareRemoval();
    AirPlayReceiver.RemovePairing(root, key1);
    Check(File.ReadAllText(clients) == rowB + "unrelated legacy row\n", "A removed, B bytes preserved");
    Check(AirPlayReceiver.GetSavedPairings(root).Single().Name == "设备 B,名称", "B name preserved");
    Check(!File.Exists(identity) && File.ReadAllBytes(retiredIdentity).SequenceEqual(liveKey), "successful removal retires server identity");
    Check(Directory.GetFiles(state, "*.tmp").Length == 0, "committed removal cleans unique backup staging");
    PrepareRemoval();
    AirPlayReceiver.RemovePairing(root, key4);
    CheckRollback("missing ID");
    PrepareRemoval();
    using (var heldIdentity = new FileStream(identity, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        bool refused = false;
        try { AirPlayReceiver.RemovePairing(root, key1); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refused = true; }
        Check(refused, "locked live identity aborts removal");
    }
    CheckRollback("live identity move failure");
    PrepareRemoval();
    using (var heldBackup = new FileStream(retiredIdentity, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        bool refused = false;
        try { AirPlayReceiver.RemovePairing(root, key1); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refused = true; }
        Check(refused, "locked retired backup aborts removal");
    }
    CheckRollback("retired backup move failure");
    PrepareRemoval();
    using (var heldClients = new FileStream(clients, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        bool refused = false;
        try { AirPlayReceiver.RemovePairing(root, key1); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refused = true; }
        Check(refused, "locked clients replacement aborts removal");
    }
    CheckRollback("clients atomic replacement failure");
    PrepareRemoval();
    File.Delete(identity);
    AirPlayReceiver.RemovePairing(root, key1);
    Check(File.ReadAllText(clients) == rowB + "unrelated legacy row\n", "removal without existing server key still revokes A");
    Check(File.ReadAllBytes(retiredIdentity).SequenceEqual(oldBackup), "missing live identity preserves retired backup");

    File.WriteAllText(clients, key1 + "\n" + key2, new UTF8Encoding(false));
    Check(AirPlayReceiver.GetSavedPairings(root).Count == 2, "last key without LF");
    File.WriteAllText(clients, key1 + "!\n" + "!" + key2[1..] + "\n" + key2 + ",date,id,Valid\r\n", new UTF8Encoding(false));
    Check(AirPlayReceiver.GetSavedPairings(root).Count == 1, "reject invalid Base64 and key delimiters");

    string oversized = key3 + ",date,id," + new string('x', 2 * 1024 * 1024) + "\n";
    File.WriteAllText(clients, oversized + key4 + ",date,id,Following\n", new UTF8Encoding(false));
    long oversizedReadBytes = new FileInfo(clients).Length;
    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    pairings = AirPlayReceiver.GetSavedPairings(root);
    long oversizedReadAllocations = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    Check(pairings.Count == 2 && pairings.Single(p => p.Id == key4).Name == "Following", "oversized row prefix does not split into extra records");
    Check(pairings.Single(p => p.Id == key3).Name!.Length <= 4096, "oversized metadata bounded");
    Check(oversizedReadAllocations < 128 * 1024, "large damaged file read allocations bounded");
    AirPlayReceiver.RemovePairing(root, key4);
    Check(File.ReadAllText(clients) == oversized, "removal preserves oversized unrelated row byte-for-byte");

    using (var held = new FileStream(Path.Combine(state, "receiver.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    {
        bool refused = false;
        try { AirPlayReceiver.ClearPairings(root); } catch (IOException) { refused = true; }
        Check(refused && File.Exists(clients), "active receiver lock prevents pairing mutation");
    }
    AirPlayReceiver.ClearPairings(root);
    Check(!File.Exists(clients) && AirPlayReceiver.GetSavedPairings(root).Count == 0, "clear pairings and missing file");

    string preferences = Path.Combine(state, "preferences.json");
    Check(!new ReceiverPreferences().AutoFullScreen, "automatic full screen defaults off");
    File.WriteAllText(preferences, "{\"Decoder\":1,\"Theme\":2,\"Language\":1,\"OriginalSize\":true}");
    var settings = ReceiverPreferences.Load(root);
    Check(settings == new ReceiverPreferences(DecoderMode.Software, true, AppTheme.Dark, AppLanguage.English),
        "legacy preferences preserve existing values and leave automatic full screen off");
    File.WriteAllText(preferences, "{\"Decoder\":999,\"Theme\":999,\"Language\":999,\"OriginalSize\":true,\"AutoFullScreen\":true}");
    settings = ReceiverPreferences.Load(root);
    Check(settings.Decoder == DecoderMode.Automatic && settings.Theme == AppTheme.System && settings.Language == AppLanguage.SimplifiedChinese && settings.OriginalSize && settings.AutoFullScreen, "invalid saved enums replaced with defaults while boolean preferences survive");
    File.WriteAllText(preferences, "{broken json");
    Check(ReceiverPreferences.Load(root) == new ReceiverPreferences(), "malformed settings recover");
    settings = new ReceiverPreferences(DecoderMode.Software, true, AppTheme.Dark, AppLanguage.English, AutoFullScreen: true);
    settings.Save(root);
    Check(ReceiverPreferences.Load(root) == settings, "settings round trip");
    settings = settings with { Theme = AppTheme.Light, AutoFullScreen = false };
    settings.Save(root);
    Check(ReceiverPreferences.Load(root) == settings && Directory.GetFiles(state, "*.tmp").Length == 0, "settings atomic overwrite and cleanup");

    var mediaSnapshot = new
    {
        revision = 7UL, kind = "audio", playing = true, title = "曲目🎵", artist = "演奏者", album = "专辑",
        positionMs = 7_000L, durationMs = 120_000L, artworkRevision = 3UL, artworkBytes = 512, controls = 7UL
    };
    byte[] MediaJson<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);
    byte[] validMedia = MediaJson(mediaSnapshot);
    string validMediaJson = Encoding.UTF8.GetString(validMedia);
    Check(ReceiverMediaState.Empty.Kind == ReceiverMediaKind.None && !ReceiverMediaState.Empty.Playing &&
        ReceiverMediaState.Empty.Controls == MediaControls.None && ReceiverMediaState.Empty.PositionAt(TimeSpan.MaxValue) == 0,
        "empty media has no playback or controls");
    Check(ReceiverMediaState.TryParse(validMedia, out var media) && media.Revision == 7 &&
        media.Kind == ReceiverMediaKind.Audio && media.Playing && media.Title == "曲目🎵" && media.Artist == "演奏者" &&
        media.Album == "专辑" && media.PositionMs == 7_000 && media.DurationMs == 120_000 && media.ArtworkRevision == 3 &&
        media.ArtworkBytes == 512 && media.Controls == (MediaControls.PlayPause | MediaControls.Previous | MediaControls.Next),
        "complete UTF-8 media snapshot parses without losing metadata");
    Check(media.Session == 0, "media snapshots without the optional session remain compatible");
    byte[] ControlJson(string fields) => Encoding.UTF8.GetBytes(validMediaJson[..^1] + fields + "}");
    Check(ReceiverMediaState.TryParse(ControlJson(",\"source\":\"audio\",\"controlSession\":12,\"contentRevision\":7,\"seekStartMs\":0,\"seekEndMs\":120000"), out var readOnly) &&
        !readOnly.CanSeek, "a duration alone cannot authorize music seeking");
    var seekJson = Encoding.UTF8.GetString(ControlJson(",\"source\":\"audio\",\"controlSession\":12,\"contentRevision\":7,\"seekStartMs\":0,\"seekEndMs\":120000")).Replace("\"controls\":7", "\"controls\":15");
    Check(ReceiverMediaState.TryParse(Encoding.UTF8.GetBytes(seekJson), out var seekMusic) && seekMusic.CanSeek &&
        seekMusic.ControlSession == 12 && seekMusic.ContentRevision == 7, "music seeking requires explicit capability range and session");
    Check(seekMusic.SeekSliderValue(60_000) == 500 && seekMusic.SeekPosition(500) == 60_000 &&
        seekMusic.SeekPosition(double.NaN) == seekMusic.PositionMs && seekMusic.SeekPosition(-1) == 0 &&
        seekMusic.SeekPosition(1001) == 120_000, "normalized seek mapping rejects non-finite input and clamps boundaries");
    var rollingSeek = seekMusic with { SeekStartMs = 60_000, SeekEndMs = 604_800_000 };
    Check(rollingSeek.SeekPosition(0) == 60_000 && rollingSeek.SeekPosition(1000) == 604_800_000 &&
        rollingSeek.SeekSliderValue(604_800_000) == 1000, "long and nonzero live ranges preserve bounded UI coordinates");
    var directJson = seekJson.Replace("\"kind\":\"audio\"", "\"kind\":\"video\"").Replace("\"source\":\"audio\"", "\"source\":\"direct\"");
    Check(ReceiverMediaState.TryParse(Encoding.UTF8.GetBytes(directJson), out var seekVideo) && seekVideo.IsDirectVideo &&
        seekVideo.CanSeek && seekVideo.Controls == (MediaControls.PlayPause | MediaControls.Seek), "direct video exposes only supported video controls");
    Check(seekVideo.CanControlVideo && !(seekVideo with { DurationMs = 0 }).CanControlVideo,
        "desktop video transport requires a known on-demand source");
    Check((seekVideo with { Controls = MediaControls.PlayPause }).CanControlVideo &&
        !(seekVideo with { Controls = MediaControls.PlayPause }).CanSeek,
        "finite video can pause even when seeking is unsupported");
    Check(ReceiverMediaState.TryParse(Encoding.UTF8.GetBytes(directJson[..^1] + ",\"isLive\":true}"), out var liveVideo) &&
        liveVideo.IsLive && liveVideo.CanSeek && !liveVideo.CanControlVideo, "live DVR capability does not expose on-demand playback controls");
    Check(!ReceiverMediaState.TryParse(Encoding.UTF8.GetBytes(directJson[..^1] + ",\"isLive\":\"true\"}"), out _),
        "live status must be a boolean");
    Check(ReceiverMediaState.TryParse(Encoding.UTF8.GetBytes(directJson.Replace("\"direct\"", "\"mirror\"")), out var mirrorControl) &&
        mirrorControl.Controls == MediaControls.None && !mirrorControl.CanSeek, "screen mirroring cannot acquire transport controls");
    foreach (string invalid in new[] {seekJson.Replace("\"seekEndMs\":120000", "\"seekEndMs\":-1"),
        seekJson.Replace("\"seekStartMs\":0", "\"seekStartMs\":120001"),
        seekJson.Replace("\"controlSession\":12", "\"controlSession\":true"),
        seekJson.Replace("\"source\":\"audio\"", "\"source\":\"direct\"")})
        Check(!ReceiverMediaState.TryParse(Encoding.UTF8.GetBytes(invalid), out _), "malformed or mismatched playback capabilities rejected");
    Check(media.Connected && !ReceiverMediaState.Empty.Connected &&
        ReceiverMediaState.TryParse(MediaJson(mediaSnapshot with { kind = "video" }), out var legacyVideoConnection) && legacyVideoConnection.Connected &&
        ReceiverMediaState.TryParse(MediaJson(mediaSnapshot with { kind = "none" }), out var legacyIdleConnection) && !legacyIdleConnection.Connected,
        "legacy snapshots infer connection from media while the empty state remains disconnected");
    byte[] ConnectionJson(string connected, string? source = null) => Encoding.UTF8.GetBytes(
        (source ?? validMediaJson)[..^1] + ",\"connected\":" + connected + "}");
    string idleMediaJson = Encoding.UTF8.GetString(MediaJson(mediaSnapshot with { kind = "none" }));
    Check(ReceiverMediaState.TryParse(ConnectionJson("true", idleMediaJson), out var idleConnected) &&
        idleConnected.Connected && idleConnected.Kind == ReceiverMediaKind.None && !idleConnected.Playing && idleConnected.Controls == MediaControls.None,
        "authenticated idle connection is independent of media playback and capabilities");
    Check(ReceiverMediaState.TryParse(ConnectionJson("false", idleMediaJson), out var idleDisconnected) && !idleDisconnected.Connected &&
        idleDisconnected.Revision == idleConnected.Revision,
        "connection can change without a metadata revision");
    Check(ReceiverMediaState.TryParse(ConnectionJson("false"), out var explicitDisconnected) && !explicitDisconnected.Connected &&
        ReceiverMediaState.TryParse(ConnectionJson("true", validMediaJson.Replace("\"playing\":true", "\"playing\":false")), out var pausedConnection) &&
        pausedConnection.Connected && !pausedConnection.Playing,
        "explicit connection state remains authoritative during playback or pause");
    foreach (string invalidConnection in new[] { "null", "0", "1", "\"true\"", "{}", "[]", "true,\"connected\":false" })
        Check(!ReceiverMediaState.TryParse(ConnectionJson(invalidConnection), out var rejectedConnection) &&
            rejectedConnection == ReceiverMediaState.Empty,
            "nonboolean or duplicate connection state is rejected without partial state");
    byte[] NamedConnection(string name, bool connected = true) => Encoding.UTF8.GetBytes(
        Encoding.UTF8.GetString(ConnectionJson(connected ? "true" : "false", idleMediaJson))[..^1] + ",\"deviceName\":" + name + "}");
    Check(ReceiverMediaState.TryParse(NamedConnection("\" Test iPhone \""), out var namedConnection) && namedConnection.DeviceName == "Test iPhone",
        "an authenticated sender name is parsed and trimmed without changing playback capabilities");
    Check(ReceiverMediaState.TryParse(NamedConnection("\"Test iPhone\"", false), out var noDevice) && noDevice.DeviceName == "",
        "a disconnected snapshot clears the old sender label");
    foreach (string invalidName in new[] { "42", "null", "\"line\\nbreak\"", JsonSerializer.Serialize(new string('x', 513)) })
        Check(!ReceiverMediaState.TryParse(NamedConnection(invalidName), out _), "invalid sender names cannot enter the connected label");
    Check(media.AudioActivity == default && !media.AudioActivity.IsFresh(TimeSpan.Zero),
        "legacy media without audio analysis uses non-reactive background motion");
    byte[] ActivityJson(string activity, string? source = null) => Encoding.UTF8.GetBytes(
        (source ?? validMediaJson)[..^1] + ",\"audioActivity\":" + activity + "}");
    const string validActivity = """{"valid":true,"energy":0.125,"density":0.75,"ageMs":25}""";
    Check(ReceiverMediaState.TryParse(ActivityJson(validActivity), out var rhythmicMedia) &&
        rhythmicMedia.AudioActivity == new ReceiverAudioActivity(true, 0.125f, 0.75f, 25),
        "normalized decoded-audio activity parses independently of metadata and session revision");
    Check(rhythmicMedia.AudioActivity.IsFresh(TimeSpan.Zero) &&
        rhythmicMedia.AudioActivity.IsFresh(TimeSpan.FromMilliseconds(874)) &&
        !rhythmicMedia.AudioActivity.IsFresh(TimeSpan.FromMilliseconds(875)),
        "native sample age and local elapsed time expire activity at the shared 900ms deadline");
    Check(rhythmicMedia.AudioActivity.IsFresh(TimeSpan.FromSeconds(-1)) &&
        !rhythmicMedia.AudioActivity.IsFresh(TimeSpan.MaxValue),
        "activity freshness handles negative and extreme elapsed time without overflow");
    Check(ReceiverMediaState.TryParse(ActivityJson("""{"valid":true,"energy":0,"density":1,"ageMs":900}"""), out var staleActivity) &&
        !staleActivity.AudioActivity.IsFresh(TimeSpan.Zero), "stale decoded-audio windows cannot drive motion");
    Check(ReceiverMediaState.TryParse(ActivityJson("""{"valid":false,"energy":1,"density":1,"ageMs":2147483647}"""), out var invalidActivity) &&
        invalidActivity.AudioActivity == default, "invalid analysis is normalized to an empty activity");
    foreach (string suppressedSource in new[] { validMediaJson.Replace("\"playing\":true", "\"playing\":false"),
        validMediaJson.Replace("\"kind\":\"audio\"", "\"kind\":\"video\""),
        validMediaJson.Replace("\"kind\":\"audio\"", "\"kind\":\"none\"") })
        Check(ReceiverMediaState.TryParse(ActivityJson(validActivity, suppressedSource), out var suppressedActivity) &&
            suppressedActivity.AudioActivity == default, "paused or non-audio media suppresses retained activity");
    foreach (string invalidAnalysis in new[]
    {
        "null", "[]", "{}",
        """{"valid":1,"energy":0,"density":0,"ageMs":0}""",
        """{"valid":true,"energy":-0.01,"density":0,"ageMs":0}""",
        """{"valid":true,"energy":1.00000001,"density":0,"ageMs":0}""",
        """{"valid":true,"energy":1e400,"density":0,"ageMs":0}""",
        """{"valid":true,"energy":"0.5","density":0,"ageMs":0}""",
        """{"valid":true,"energy":0,"density":1.0001,"ageMs":0}""",
        """{"valid":true,"energy":0,"density":0,"density":1,"ageMs":0}""",
        """{"valid":true,"energy":0,"density":0,"ageMs":-1}""",
        """{"valid":true,"energy":0,"density":0,"ageMs":0.5}""",
        """{"valid":true,"energy":0,"density":0,"ageMs":2147483648}"""
    })
        Check(!ReceiverMediaState.TryParse(ActivityJson(invalidAnalysis), out _),
            "malformed, non-finite, duplicate, or out-of-bounds audio analysis is rejected");
    Check(!new ReceiverAudioActivity(true, float.NaN, 0, 0).IsFresh(TimeSpan.Zero) &&
        !new ReceiverAudioActivity(true, 0, float.PositiveInfinity, 0).IsFresh(TimeSpan.Zero),
        "invalid in-process activity values cannot enable reactive motion");
    MusicMotionTests.Run(Check);
    BeatImpulseTests.Run(Check);
    MediaBeatTests.Run(Check);
    ArtworkFlowTests.Run(Check);
    Check(ReceiverMediaState.TryParse(Encoding.UTF8.GetBytes(validMediaJson[..^1] + ",\"session\":18446744073709551615}"), out var sessionMedia) &&
        sessionMedia.Session == ulong.MaxValue && sessionMedia.Title == media.Title,
        "audio session identity accepts the full unsigned 64-bit range");
    Check(ReceiverMediaState.TryParse(validMedia.Concat(new byte[] { 0 }).ToArray(), out var terminatedMedia) && terminatedMedia == media,
        "native NUL-terminated media snapshot parses");
    Check(media.PositionAt(TimeSpan.FromMilliseconds(1_500)) == 8_500 && media.PositionAt(TimeSpan.FromTicks(9_999)) == 7_000,
        "playing audio extrapolates whole milliseconds");
    Check(media.PositionAt(TimeSpan.MaxValue) == 120_000 && media.PositionAt(TimeSpan.FromSeconds(-1)) == 7_000,
        "audio progress clamps at duration and never rewinds for negative elapsed time");
    Check((media with { Playing = false }).PositionAt(TimeSpan.FromSeconds(5)) == 7_000,
        "paused audio progress remains fixed");
    Check(ReceiverMediaState.TryParse(MediaJson(mediaSnapshot with { kind = "video" }), out var videoMedia) &&
        videoMedia.Playing && videoMedia.Controls == MediaControls.None && videoMedia.PositionAt(TimeSpan.FromSeconds(5)) == 7_000,
        "video preserves playback state without audio controls or extrapolation");
    Check(ReceiverMediaState.TryParse(MediaJson(mediaSnapshot with { kind = "none" }), out var noMedia) &&
        !noMedia.Playing && noMedia.Controls == MediaControls.None,
        "no-media snapshots cannot enable playback or transport controls");
    Check(ReceiverMediaState.TryParse(MediaJson(mediaSnapshot with { controls = 0xFFFFUL }), out var maskedMedia) &&
        maskedMedia.Controls == (MediaControls.PlayPause | MediaControls.Previous | MediaControls.Next),
        "unknown transport capability bits are masked");
    Check(ReceiverMediaState.TryParse(MediaJson(mediaSnapshot with { positionMs = long.MaxValue }), out var endedMedia) && endedMedia.PositionMs == 120_000,
        "overshooting snapshot position clamps to track duration");
    Check(ReceiverMediaState.TryParse(MediaJson(mediaSnapshot with { positionMs = -1L }), out var negativeMedia) && negativeMedia.PositionMs == 0,
        "negative snapshot position clamps to zero");
    const long sevenDaysMs = 7L * 24 * 60 * 60 * 1000;
    Check(ReceiverMediaState.TryParse(MediaJson(mediaSnapshot with { durationMs = 0L, positionMs = long.MaxValue }), out var unboundedMedia) &&
        unboundedMedia.PositionMs == sevenDaysMs && unboundedMedia.PositionAt(TimeSpan.MaxValue) == sevenDaysMs,
        "unknown duration remains bounded without arithmetic overflow");
    Check(ReceiverMediaState.TryParse(MediaJson(mediaSnapshot with { durationMs = sevenDaysMs, artworkBytes = 2 * 1024 * 1024,
        revision = ulong.MaxValue, artworkRevision = ulong.MaxValue, title = new string('x', 1024) }), out var limitsMedia) &&
        limitsMedia.Revision == ulong.MaxValue && limitsMedia.ArtworkRevision == ulong.MaxValue,
        "media duration artwork text and revision boundary values are accepted");

    var invalidMediaCases = new (string Name, byte[] Json)[]
    {
        ("empty", Array.Empty<byte>()),
        ("malformed", Encoding.UTF8.GetBytes("{broken")),
        ("array root", Encoding.UTF8.GetBytes("[]")),
        ("null root", Encoding.UTF8.GetBytes("null")),
        ("missing field", Encoding.UTF8.GetBytes(validMediaJson.Replace("\"durationMs\":120000,", ""))),
        ("unknown kind", MediaJson(mediaSnapshot with { kind = "other" })),
        ("negative revision", Encoding.UTF8.GetBytes(validMediaJson.Replace("\"revision\":7", "\"revision\":-1"))),
        ("overflow revision", Encoding.UTF8.GetBytes(validMediaJson.Replace("\"revision\":7", "\"revision\":18446744073709551616"))),
        ("nonboolean playing", Encoding.UTF8.GetBytes(validMediaJson.Replace("\"playing\":true", "\"playing\":1"))),
        ("fractional position", Encoding.UTF8.GetBytes(validMediaJson.Replace("\"positionMs\":7000", "\"positionMs\":7000.5"))),
        ("negative duration", MediaJson(mediaSnapshot with { durationMs = -1L })),
        ("oversized duration", MediaJson(mediaSnapshot with { durationMs = sevenDaysMs + 1 })),
        ("negative artwork", MediaJson(mediaSnapshot with { artworkBytes = -1 })),
        ("oversized artwork", MediaJson(mediaSnapshot with { artworkBytes = 2 * 1024 * 1024 + 1 })),
        ("oversized text", MediaJson(mediaSnapshot with { title = new string('x', 1025) })),
        ("null text", Encoding.UTF8.GetBytes(validMediaJson.Replace("\"title\":" + JsonSerializer.Serialize(mediaSnapshot.title), "\"title\":null"))),
        ("overflow controls", MediaJson(mediaSnapshot with { controls = (ulong)uint.MaxValue + 1 })),
        ("negative session", Encoding.UTF8.GetBytes(validMediaJson[..^1] + ",\"session\":-1}")),
        ("nonnumeric session", Encoding.UTF8.GetBytes(validMediaJson[..^1] + ",\"session\":\"1\"}")),
        ("duplicate field", Encoding.UTF8.GetBytes(validMediaJson[..^1] + ",\"kind\":\"audio\"}")),
        ("excessive depth", Encoding.UTF8.GetBytes(validMediaJson[..^1] + ",\"extra\":{\"one\":{\"two\":{\"three\":{}}}}}")),
        ("trailing JSON", Encoding.UTF8.GetBytes(validMediaJson + " {}")),
        ("double NUL", validMedia.Concat(new byte[] { 0, 0 }).ToArray()),
        ("oversized payload", new byte[8193])
    };
    foreach (var invalid in invalidMediaCases)
        Check(!ReceiverMediaState.TryParse(invalid.Json, out var rejectedMedia) && ReferenceEquals(rejectedMedia, ReceiverMediaState.Empty),
            "invalid media snapshot rejected: " + invalid.Name);
    byte[] invalidUtf8 = validMedia.Concat(new byte[] { 0xFF }).ToArray();
    Check(!ReceiverMediaState.TryParse(invalidUtf8, out _), "invalid UTF-8 media bytes rejected");
    byte[] boundaryMedia = new byte[8192];
    Array.Fill(boundaryMedia, (byte)' ');
    validMedia.CopyTo(boundaryMedia, 0);
    boundaryMedia[^1] = 0;
    Check(ReceiverMediaState.TryParse(boundaryMedia, out _), "8192-byte snapshot accepts its terminating NUL");
    boundaryMedia[^1] = (byte)' ';
    Check(!ReceiverMediaState.TryParse(boundaryMedia, out _) && ReceiverMediaState.TryParse(boundaryMedia.AsSpan(0, 8191), out _),
        "raw JSON reserves one byte of the native buffer for NUL");

    uint[] PaletteColors(MusicPalette palette) => [palette.Background, palette.First, palette.Second, palette.Third];
    byte[] blueArtwork = Enumerable.Repeat(new byte[] { 230, 80, 20, 255 }, 32 * 32).SelectMany(pixel => pixel).ToArray();
    byte[] whiteArtwork = Enumerable.Repeat((byte)255, 32 * 32 * 4).ToArray();
    byte[] whiteCoverWithAccent = whiteArtwork.ToArray();
    blueArtwork.AsSpan(0, 128 * 4).CopyTo(whiteCoverWithAccent);
    foreach (bool dark in new[] { false, true })
    {
        var bluePalette = MusicPalette.FromBgra(blueArtwork, dark);
        Check((bluePalette.First & 255) > ((bluePalette.First >> 16) & 255), "artwork palette retains the cover hue " + dark);
        var accentPalette = MusicPalette.FromBgra(whiteCoverWithAccent, dark);
        Check((accentPalette.First & 255) > ((accentPalette.First >> 16) & 255) + 5,
            "chromatic cover accents remain visible when most artwork pixels are white " + dark);
        Check(PaletteColors(bluePalette).All(color => (color >> 24) == 255), "artwork palette colors have a stable opaque base " + dark);
        var whitePalette = MusicPalette.FromBgra(whiteArtwork, dark);
        Check(PaletteColors(whitePalette).All(color => Math.Abs((int)((color >> 16) & 255) - (int)(color & 255)) <= 6),
            "white artwork produces a neutral wash without invented saturated colors " + dark);
        Check(MusicPalette.FromBgra(new byte[4096], dark) == MusicPalette.FromBgra([], dark),
            "fully transparent artwork uses the theme-safe neutral fallback " + dark);
        Check(PaletteColors(bluePalette).Concat(PaletteColors(whitePalette)).All(color =>
            new[] { (color >> 16) & 255, (color >> 8) & 255, color & 255 }.All(channel => dark ? channel <= 122 : channel >= 180)),
            "artwork palette tones stay within readable theme bounds " + dark);
        Check(MusicPalette.FromBgra(blueArtwork.Concat(whiteArtwork).ToArray(), dark) == bluePalette,
            "artwork palette computation is bounded to its 32 by 32 sample " + dark);
    }

    var texts = new UiText();
    texts.SetReceiverName("Test-PC");
    texts.SetEnglish(true);
    foreach (var (kind, abi) in new[]
    {
        (ReceiverEventKind.Ready, 1), (ReceiverEventKind.Connecting, 2), (ReceiverEventKind.Video, 3),
        (ReceiverEventKind.MirrorEnded, 4), (ReceiverEventKind.Failed, 5),
        (ReceiverEventKind.PairingRequired, 6), (ReceiverEventKind.PairingRejected, 7),
        (ReceiverEventKind.DecoderChanged, 8), (ReceiverEventKind.DecoderChangeFailed, 9),
        (ReceiverEventKind.PairingCompleted, 10), (ReceiverEventKind.ConnectionRejected, 11),
        (ReceiverEventKind.PairingAuthenticationRequired, 12)
    })
        Check((int)kind == abi, "native receiver event ABI " + kind);
    foreach (var (reason, abi) in new[]
    {
        (PairingRejectionReason.InvalidSetupData, 1), (PairingRejectionReason.ProofRejected, 2),
        (PairingRejectionReason.PinUnavailableOrExpired, 3), (PairingRejectionReason.RateLimited, 4),
        (PairingRejectionReason.VerifyRejected, 5)
    })
        Check((int)reason == abi, "native pairing reason ABI " + reason);
    foreach (DecoderMode decoder in Enum.GetValues<DecoderMode>())
        foreach (int code in new[] { 50, 10048, 10049, 10051, 1231, 1062, 10013, 9999 })
        {
            string message = texts.Translate(ReceiverNetwork.GetFailureMessage(code, decoder));
            Check(!string.IsNullOrWhiteSpace(message) && !Regex.IsMatch(message, "[\\p{IsCJKUnifiedIdeographs}]"),
                "produced receiver failure translates to English " + decoder + " " + code);
        }
    var properties = typeof(UiText).GetProperties().Where(p => p.PropertyType == typeof(string)).ToArray();
    foreach (var property in properties)
    {
        string value = (string)property.GetValue(texts)!;
        Check(!string.IsNullOrWhiteSpace(value), "nonempty UI string " + property.Name);
        if (property.Name != nameof(UiText.ChineseLanguage))
            Check(!Regex.IsMatch(value, "[\\p{IsCJKUnifiedIdeographs}]"), "English UI string " + property.Name);
    }
    var dynamicEnglish = (Dictionary<string, string>)typeof(UiText).GetField("DynamicEnglish", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    foreach (var entry in dynamicEnglish)
    {
        Check(texts.Translate(entry.Key) == entry.Value && !Regex.IsMatch(entry.Value, "[\\p{IsCJKUnifiedIdeographs}]"), "dynamic English " + entry.Key);
    }
    texts.SetEnglish(false);
    foreach (string message in dynamicEnglish.Keys) Check(texts.Translate(message) == message, "Chinese dynamic " + message);

    File.WriteAllText(Path.Combine(state, "receiver.log"), string.Join('\n', Enumerable.Range(0, 500).Select(i => $"entry-{i:D4} " + new string('x', 80))) + "\n", new UTF8Encoding(false));
    string diagnostics = AirPlayReceiver.ReadDiagnostics(root);
    Check(diagnostics.Length <= 16384 && diagnostics.EndsWith("\n") && diagnostics.Contains("entry-0499") && !diagnostics.Contains("entry-0000"), "bounded latest diagnostic log tail");

    var report = new { status = "pass", checks, elapsedMilliseconds = watch.ElapsedMilliseconds, oversizedReadBytes, oversizedReadAllocations, localizationProperties = properties.Length, localizedDynamicStates = dynamicEnglish.Count, stateDirectory = root };
    return report;
}
