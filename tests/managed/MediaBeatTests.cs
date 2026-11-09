using System.Text;
using ClearCast;

internal static class MediaBeatTests
{
    internal static void Run(Action<bool, string> check)
    {
        const string source = """{"revision":1,"kind":"audio","playing":true,"title":"Track","artist":"Artist","album":"Album","positionMs":0,"durationMs":10000,"artworkRevision":0,"artworkBytes":0,"controls":0,"audioActivity":{"valid":true,"energy":0.5,"density":0.2,"ageMs":0""";
        bool Parse(string fields, out ReceiverMediaState state) => ReceiverMediaState.TryParse(
            Encoding.UTF8.GetBytes(source + fields + "}}"), out state);
        string Event(ulong sequence, float strength = 0.8f, int age = 25) =>
            FormattableString.Invariant($"{{\"sequence\":{sequence},\"strength\":{strength},\"ageMs\":{age}}}");
        check(Parse(",\"beatEpoch\":8,\"beats\":[" + Event(1, 0.3f, 175) + "," + Event(2) + "]", out var state) &&
            state.AudioActivity.BeatEpoch == 8 && state.AudioActivity.Beats is { Length: 2 } &&
            state.AudioActivity.Beats[1] == new ReceiverAudioBeat(2, 0.8f, 25),
            "bounded beat history parses at the existing JSON depth limit");
        check(Parse("", out var legacy) && legacy.AudioActivity.BeatEpoch == 0 && legacy.AudioActivity.Beats is null,
            "old activity snapshots without beat history remain compatible");
        check(Parse(",\"beatEpoch\":0,\"beats\":[]", out var empty) && empty.AudioActivity.Beats is { Length: 0 },
            "empty inactive beat history is accepted");
        string flushedJson = source.Replace("\"valid\":true", "\"valid\":false") + ",\"beatEpoch\":9,\"beats\":[]}}";
        check(ReceiverMediaState.TryParse(Encoding.UTF8.GetBytes(flushedJson), out var flushed) &&
            !flushed.AudioActivity.IsFresh(TimeSpan.Zero) && flushed.AudioActivity.BeatEpoch == 9 &&
            flushed.AudioActivity.Energy == 0 && flushed.AudioActivity.Beats is null,
            "a valid flush generation survives an invalid sample without retaining any energy or event");
        check(Parse(",\"beatEpoch\":18446744073709551615,\"beats\":[" + Event(ulong.MaxValue) + "]", out var maximum) &&
            maximum.AudioActivity.BeatEpoch == ulong.MaxValue && maximum.AudioActivity.Beats![0].Sequence == ulong.MaxValue,
            "beat identities retain full unsigned precision");
        string sixteen = string.Join(',', Enumerable.Range(1, 16).Select(i => Event((ulong)i)));
        check(Parse(",\"beatEpoch\":1,\"beats\":[" + sixteen + "]", out var bounded) && bounded.AudioActivity.Beats!.Length == 16,
            "the native maximum of sixteen beat events fits the snapshot parser");
        foreach (string fields in new[]
        {
            ",\"beatEpoch\":1", ",\"beats\":[]", ",\"beatEpoch\":-1,\"beats\":[]",
            ",\"beatEpoch\":18446744073709551616,\"beats\":[]", ",\"beatEpoch\":1,\"beats\":null",
            ",\"beatEpoch\":0,\"beats\":[" + Event(1) + "]",
            ",\"beatEpoch\":1,\"beats\":[" + sixteen + "," + Event(17) + "]",
            ",\"beatEpoch\":1,\"beats\":[" + Event(0) + "]",
            ",\"beatEpoch\":1,\"beats\":[" + Event(2) + "," + Event(1) + "]",
            ",\"beatEpoch\":1,\"beats\":[" + Event(2) + "," + Event(2) + "]",
            ",\"beatEpoch\":1,\"beats\":[null]", ",\"beatEpoch\":1,\"beats\":[{}]",
            ",\"beatEpoch\":1,\"beats\":[" + Event(1, -0.1f) + "]",
            ",\"beatEpoch\":1,\"beats\":[" + Event(1, 1.1f) + "]",
            ",\"beatEpoch\":1,\"beats\":[" + Event(1, 1, -1) + "]",
            """, "beatEpoch":1,"beats":[{"sequence":1,"strength":1e400,"ageMs":0}]""",
            """, "beatEpoch":1,"beats":[{"sequence":1,"strength":1.00000001,"ageMs":0}]""",
            """, "beatEpoch":1,"beats":[{"sequence":1,"strength":0.5,"strength":1,"ageMs":0}]""",
            """, "beatEpoch":1,"beats":[{"sequence":1,"strength":0.5,"ageMs":0.1}]""",
            """, "beatEpoch":1,"beats":[{"sequence":1,"strength":0.5,"ageMs":2147483648}]"""
        })
            check(!Parse(fields, out _), "malformed, duplicated, unordered or oversized beat histories are rejected");
        var beat = new ReceiverAudioBeat(1, 0.9f, 50);
        check(beat.IsFresh(TimeSpan.FromMilliseconds(199)) && !beat.IsFresh(TimeSpan.FromMilliseconds(200)),
            "native beat age and local elapsed time share an exact 250ms freshness deadline");
        check(beat.IsFresh(TimeSpan.FromMilliseconds(-1)) && !beat.IsFresh(TimeSpan.MaxValue),
            "beat freshness handles negative and extreme local time without overflow");
        check(beat.StrengthAt(TimeSpan.FromMilliseconds(100)) < beat.StrengthAt(TimeSpan.Zero) &&
            beat.StrengthAt(TimeSpan.Zero) < beat.Strength && beat.StrengthAt(TimeSpan.MaxValue) == 0,
            "late beat events lose strength and expired events cannot pulse");
        check(!new ReceiverAudioBeat(0, 1, 0).IsFresh(TimeSpan.Zero) &&
            !new ReceiverAudioBeat(1, float.NaN, 0).IsFresh(TimeSpan.Zero) &&
            !new ReceiverAudioBeat(1, float.PositiveInfinity, 0).IsFresh(TimeSpan.Zero),
            "invalid in-process beat identities and amplitudes cannot pulse");
        foreach (string tag in new[] { "\"playing\":false", "\"kind\":\"video\"", "\"kind\":\"none\"" })
        {
            string json = source + ",\"beatEpoch\":8,\"beats\":[" + Event(1) + "]}}";
            json = tag.StartsWith("\"playing") ? json.Replace("\"playing\":true", tag) : json.Replace("\"kind\":\"audio\"", tag);
            check(ReceiverMediaState.TryParse(Encoding.UTF8.GetBytes(json), out var inactive) && inactive.AudioActivity == default,
                "paused or non-audio states clear beat history as well as slow activity");
        }
    }
}
