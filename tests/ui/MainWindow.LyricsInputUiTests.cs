#if CLEARCAST_UI_TESTS
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ClearCast;

public sealed partial class MainWindow
{
    private sealed record LyricsInputSnapshot(string Phase, int Step, int Probe, float Y, float TargetY,
        bool Animating, bool Browsing, double ManualOffset, int ActiveLine, double ElapsedMs);

    private async Task UiTestLyricsInputAsync(string output)
    {
        UiCheck(_receiver is null, "lyrics input fixture never starts a receiver or occupies its ports");
        var preferences = _preferences;
        var snapshots = new List<LyricsInputSnapshot>();
        var clock = Stopwatch.StartNew();
        bool passed = false;
        void Phase(string name) => File.AppendAllText(Path.Combine(_root, "ui-check-progress.log"),
            $"lyrics-input {name} {DateTimeOffset.UtcNow:O}" + Environment.NewLine);
        LyricsInputSnapshot Snapshot(string phase, int step, int probe)
        {
            var motion = MusicLyrics.UiLineMotion(probe);
            var frame = new LyricsInputSnapshot(phase, step, probe, motion.Y, motion.TargetY, motion.Animating,
                MusicLyrics.UiManualBrowsing, MusicLyrics.UiManualOffset, MusicLyrics.ActiveLineIndex,
                clock.Elapsed.TotalMilliseconds);
            snapshots.Add(frame);
            File.AppendAllText(Path.Combine(output, "lyrics-input.jsonl"),
                JsonSerializer.Serialize(frame) + Environment.NewLine);
            return frame;
        }
        static bool Close(double first, double second) => Math.Abs(first - second) <= 0.15;

        async Task CheckDeltas(string phase, int probe, IReadOnlyList<double> deltas)
        {
            Phase(phase);
            for (int index = 0; index < deltas.Count; ++index)
            {
                var before = Snapshot(phase + "-before", index, probe);
                MusicLyrics.UiScrollBy(deltas[index]);
                var after = Snapshot(phase + "-submitted", index, probe);
                UiCheck(after.Browsing && !after.Animating && Close(after.Y, after.TargetY),
                    $"{phase} publishes the direct visual position without a trailing offset animation {index}");
                UiCheck(Close(after.TargetY - before.TargetY, deltas[index]),
                    $"{phase} preserves precise input distance and reverses immediately {index}");
                await Task.Delay(8);
            }
            var settled = Snapshot(phase + "-settled", deltas.Count, probe);
            await Task.Delay(80);
            var later = Snapshot(phase + "-later", deltas.Count, probe);
            UiCheck(!later.Animating && Close(later.Y, settled.Y) && Close(later.TargetY, settled.TargetY),
                phase + " does not continue chasing the target after input stops");
        }

        try
        {
            _preferences = _preferences with { ShowLyrics = true, AutoFullScreen = false };
            ApplyMediaState(ReceiverMediaState.Empty);
            var audio = new ReceiverMediaState(991, ReceiverMediaKind.Audio, false,
                "Input follow test", "Synthetic artist", "Synthetic album", 120_000, 240_000,
                0, 0, MediaControls.None) { Session = 98, Source = "audio" };
            ApplyMediaState(audio);
            await UiWaitUntil(() => !_lyricsPending, "input fixture offline lookup settles");
            var lines = Enumerable.Range(0, 120)
                .Select(index => new LyricLine(index * 2000, "Input row " + index)).ToArray();
            _lyrics = new Lyrics(LyricsKind.Synced, lines);
            UpdateLyricsPresentation(false);
            MusicLyrics.AnimationsEnabled = true;
            await UiWaitForStableLayout(MusicPanel, MusicContent, MusicLyrics);
            await Task.Delay(1000);
            UiCheck(_lyricsOpen && MusicLyrics.ActiveLineIndex == 60,
                "input fixture opens with synced lyrics at a stable midpoint");

            double[] wheelDeltas = [-17.5, -11.25, 6.75, 22, -5.5, 5.5];
            double[] fineDeltas = [-2.25, -1.5, 0.75, 3, -4.125, 4.125];
            await CheckDeltas("synced-wheel", 60, wheelDeltas);
            await CheckDeltas("synced-fine-input", 60, fineDeltas);
            UiCheck(Math.Abs(MusicLyrics.UiManualOffset) <= 0.15 && MusicLyrics.UiManualBrowsing,
                "a zero-net manual gesture still keeps the explicit browsing hold");

            Phase("playback-during-hold");
            var held = Snapshot("hold-before", 0, 60);
            MusicLyrics.SetPosition(124_000);
            var advanced = Snapshot("hold-playback-advanced", 0, 60);
            UiCheck(advanced.ActiveLine == 62 && advanced.Browsing && !advanced.Animating &&
                Close(advanced.Y, held.Y) && Close(advanced.TargetY, held.TargetY),
                "playback can advance emphasis without moving lyrics underneath a manual gesture");
            await Task.Delay(100);
            var stillHeld = Snapshot("hold-later", 0, 60);
            UiCheck(Close(stillHeld.Y, held.Y) && Close(stillHeld.TargetY, held.TargetY),
                "the manual viewport remains fixed while the new playback line is emphasized");

            MusicLyrics.UiBeginManualGesture();
            await Task.Delay(3100);
            MusicLyrics.SetPosition(126_000);
            var gestureHeld = Snapshot("gesture-still-held", 0, 60);
            UiCheck(gestureHeld.Browsing && !gestureHeld.Animating && gestureHeld.ActiveLine == 63 &&
                Close(gestureHeld.Y, held.Y) && Close(gestureHeld.TargetY, held.TargetY),
                "an ongoing gesture holds its viewport even when the finger pauses for more than three seconds");
            MusicLyrics.UiEndManualGesture();
            MusicLyrics.SetPosition(128_000);
            var gestureReleased = Snapshot("gesture-released-hold", 0, 60);
            UiCheck(gestureReleased.Browsing && !gestureReleased.Animating &&
                Close(gestureReleased.Y, held.Y) && Close(gestureReleased.TargetY, held.TargetY),
                "releasing a gesture starts the manual hold without immediately returning to playback");
            await Task.Delay(3100);
            MusicLyrics.SetPosition(128_000);
            var resumed = Snapshot("hold-expired", 0, 60);
            UiCheck(!resumed.Browsing && resumed.Animating && Math.Abs(resumed.TargetY - held.TargetY) > 1 &&
                MusicLyrics.LineBlur(64) == 0 && MusicLyrics.LineBlur(63) > 0,
                "automatic cascade and blur resume after the manual hold expires even on the same active line");
            await Task.Delay(1000);

            Phase("interrupt-automatic-motion");
            MusicLyrics.SetPosition(140_000);
            await Task.Delay(60);
            Snapshot("automatic-before-input", 0, 70);
            MusicLyrics.UiScrollBy(-9.5);
            var interrupted = Snapshot("automatic-interrupted", 0, 70);
            UiCheck(interrupted.Browsing && !interrupted.Animating && Close(interrupted.Y, interrupted.TargetY),
                "manual input takes over an in-flight automatic animation without a residual offset tween");
            await Task.Delay(120);
            var afterInterrupt = Snapshot("automatic-interrupted-later", 0, 70);
            UiCheck(!afterInterrupt.Animating && Close(afterInterrupt.Y, interrupted.Y) &&
                Close(afterInterrupt.TargetY, interrupted.TargetY),
                "the interrupted automatic animation cannot keep moving the manual viewport");
            await UiCaptureAsync(output, "synced-direct-input");

            Phase("plain-source");
            _lyrics = new Lyrics(LyricsKind.Plain, lines.Select(line => line with { TimeMs = 0 }).ToArray());
            UpdateLyricsPresentation(false);
            await UiWaitForStableLayout(MusicLyrics);
            await Task.Delay(600);
            UiCheck(!MusicLyrics.UiManualBrowsing && MusicLyrics.ActiveLineIndex == -1,
                "replacing synced lyrics with plain text clears the former browsing state");
            await CheckDeltas("plain-wheel", 3, wheelDeltas);
            await CheckDeltas("plain-fine-input", 3, fineDeltas);
            var plain = Snapshot("plain-before-playback", 0, 3);
            MusicLyrics.SetPosition(180_000);
            var plainAfterPlayback = Snapshot("plain-after-playback", 0, 3);
            UiCheck(plainAfterPlayback.ActiveLine == -1 && plainAfterPlayback.Browsing &&
                Close(plainAfterPlayback.Y, plain.Y) && Close(plainAfterPlayback.TargetY, plain.TargetY),
                "plain lyrics retain their manually chosen viewport when playback position changes");

            Phase("clamped-input");
            MusicLyrics.UiScrollBy(-1_000_000);
            var bottom = Snapshot("bottom-clamped", 0, 119);
            MusicLyrics.UiScrollBy(-999);
            var bottomAgain = Snapshot("bottom-clamped-again", 0, 119);
            UiCheck(!bottomAgain.Animating && Close(bottomAgain.Y, bottom.Y) && Close(bottomAgain.TargetY, bottom.TargetY),
                "input beyond the final lyric does not create overscroll or a trailing animation");
            MusicLyrics.UiScrollBy(1_000_000);
            var top = Snapshot("top-clamped", 0, 0);
            MusicLyrics.UiScrollBy(999);
            var topAgain = Snapshot("top-clamped-again", 0, 0);
            UiCheck(!topAgain.Animating && Close(topAgain.Y, top.Y) && Close(topAgain.TargetY, top.TargetY),
                "reversing from the bottom reaches the first lyric without overscroll");
            await UiCaptureAsync(output, "plain-direct-input");
            passed = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(output, "lyrics-input-results.json"), JsonSerializer.Serialize(new
            {
                passed, snapshots,
                scope = "Actual scrolling method and published Composition properties, including no active offset tween. Synthetic wheel-sized/fine deltas; no physical hardware or GPU presentation latency claim."
            }, new JsonSerializerOptions { WriteIndented = true }));
            _preferences = preferences;
            ApplyMediaState(ReceiverMediaState.Empty);
            ResetLyrics();
            UpdateLyricsPresentation(false);
        }
    }
}
#endif
