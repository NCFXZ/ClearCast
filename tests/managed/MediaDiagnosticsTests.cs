using System.Globalization;
using System.Text;
using System.Text.Json;
using ClearCast;

internal static class MediaDiagnosticsTests
{
    internal static void Run(Action<bool, string> check)
    {
        const string source = """{"revision":1,"session":7,"kind":"video","connected":true,"playing":true,"title":"","artist":"","album":"","positionMs":0,"durationMs":10000,"artworkRevision":0,"artworkBytes":0,"controls":0""";
        bool Parse(string? diagnostics, out ReceiverMediaState state, string? snapshot = null) => ReceiverMediaState.TryParse(
            Encoding.UTF8.GetBytes((snapshot ?? source) + (diagnostics is null ? "" : ",\"diagnostics\":" + diagnostics) + "}"), out state);
        const string complete = """{"videoCodec":"H.264","width":1920,"height":1080,"frameRate":29.97002997002997,"videoBitrate":5300000,"audioCodec":"AAC","sampleRate":44100,"channels":2,"audioBitrate":256000}""";
        check(Parse(complete, out var video) && video.Diagnostics == new ReceiverMediaDiagnostics
        {
            VideoCodec = "H.264", Width = 1920, Height = 1080, FrameRate = 29.97002997002997,
            VideoBitrate = 5_300_000, AudioCodec = "AAC", SampleRate = 44100, Channels = 2, AudioBitrate = 256000
        }, "negotiated media diagnostics preserve source precision and stream bitrates");
        bool legacyParsed = Parse(null, out var legacy), unknownParsed = Parse("{}", out var unknown);
        check(legacyParsed && legacy.Diagnostics == default && unknownParsed && unknown.Diagnostics == default,
            "missing and empty diagnostics remain backwards compatible and contain no invented metrics");
        check(Parse("""{"audioCodec":"ALAC","sampleRate":48000,"channels":2}""", out var audio,
            source.Replace("\"video\"", "\"audio\"")) && audio.Diagnostics.AudioCodec == "ALAC" &&
            audio.Diagnostics.Width == 0 && audio.Diagnostics.VideoBitrate == 0,
            "a replacement music snapshot cannot retain a preceding video's metrics");
        foreach (string noMedia in new[] { source.Replace("\"connected\":true", "\"connected\":false"),
            source.Replace("\"video\"", "\"none\"") })
            check(Parse(complete, out var cleared, noMedia) && cleared.Diagnostics == default,
                "disconnect and idle snapshots clear diagnostics rather than preserving stream details");
        foreach (string invalid in new[]
        {
            "null", "[]", "true", "42", "{\"width\":1,\"width\":2}", "{\"other\":0,\"other\":1}",
            "{\"width\":-1}", "{\"width\":32769}", "{\"height\":32769}", "{\"width\":1.5}",
            "{\"height\":\"1080\"}", "{\"frameRate\":-0.1}", "{\"frameRate\":1000.01}",
            "{\"frameRate\":1e400}", "{\"frameRate\":\"30\"}", "{\"frameRate\":null}",
            "{\"videoBitrate\":-1}", "{\"videoBitrate\":1000000000001}", "{\"audioBitrate\":-1}",
            "{\"audioBitrate\":1000000000001}", "{\"sampleRate\":-1}", "{\"sampleRate\":768001}",
            "{\"channels\":65}", "{\"channels\":true}", "{\"videoCodec\":null}", "{\"audioCodec\":2}",
            "{\"videoCodec\":\"line\\nbreak\"}", "{\"audioCodec\":" + JsonSerializer.Serialize(new string('x', 65)) + "}"
        })
            check(!Parse(invalid, out var rejected) && rejected == ReceiverMediaState.Empty,
                "malformed, duplicate, non-finite or excessive stream diagnostics reject the whole snapshot");
        check(Parse("""{"videoCodec":"","width":32768,"height":32768,"frameRate":1000,"videoBitrate":1000000000000,"sampleRate":768000,"channels":64,"audioBitrate":1000000000000}""", out var maximum) &&
            maximum.Diagnostics.VideoCodec is null && maximum.Diagnostics.Width == 32768 && maximum.Diagnostics.AudioBitrate == 1_000_000_000_000,
            "diagnostic bounds and explicit unknown codec values are handled without overflow");
        var texts = new UiText();
        check(texts.Disconnect == "断开连接", "disconnect button uses the requested Chinese wording");
        string chinese = texts.MediaDiagnosticsText(video);
        check(chinese == "分辨率: 1920 × 1080\n源帧率: 29.97 fps\n视频编码: H.264\n视频码率: 5.3 Mbps\n音频编码: AAC\n采样率: 44.1 kHz\n声道数: 2\n音频码率: 256 kbps",
            "Chinese overlay reports actual source video and audio information with compact units");
        check(texts.MediaDiagnosticsText(audio) == "音频编码: ALAC\n采样率: 48 kHz\n声道数: 2",
            "music diagnostics omit unknown bitrate and unrelated video metrics");
        check(texts.MediaDiagnosticsText(unknown) == "等待媒体信息…" && texts.MediaDiagnosticsText(ReceiverMediaState.Empty) == "",
            "unknown media displays a localized waiting state and idle displays no details");
        texts.SetEnglish(true);
        check(texts.MediaDiagnosticsText(video) == "Resolution: 1920 × 1080\nSource frame rate: 29.97 fps\nVideo codec: H.264\nVideo bitrate: 5.3 Mbps\nAudio codec: AAC\nSample rate: 44.1 kHz\nChannels: 2\nAudio bitrate: 256 kbps",
            "English overlay distinguishes negotiated source frame rate and video/audio bitrates");
        check(texts.MediaDiagnosticsText(unknown) == "Waiting for media information…", "unknown media waiting text translates");
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            check(texts.MediaDiagnosticsText(video).Contains("29.97 fps") && texts.MediaDiagnosticsText(video).Contains("44.1 kHz"),
                "diagnostics numeric formatting remains stable under different host cultures");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
