#if CLEARCAST_UI_TESTS
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ClearCast;

public sealed partial class MainWindow
{
    private sealed record LyricsPerformanceSample(int Lines, string Phase, int Inputs, long PlaceCalls,
        long VisitedLines, long CreatedAnimations, long StartedAnimations, long GeometryWrites, int VisibleLines,
        double CallbackMedianMs, double CallbackP95Ms, double CallbackMaximumMs,
        double WallMs, double ProcessCpuMs, long UiAllocatedBytes);

    private async Task UiTestLyricsPerformanceAsync(string output)
    {
        UiCheck(_receiver is null, "lyrics performance fixture never starts a receiver or occupies its ports");
        bool baseline = Environment.GetEnvironmentVariable("CLEARCAST_UI_LYRICS_PERF_BASELINE") == "1";
        var originalPreferences = _preferences;
        var samples = new List<LyricsPerformanceSample>();
        using var process = Process.GetCurrentProcess();
        void Phase(int lines, string name) => File.AppendAllText(Path.Combine(_root, "ui-check-progress.log"),
            $"lyrics-performance {lines} {name} {DateTimeOffset.UtcNow:O}" + Environment.NewLine);

        async Task Measure(int lines, string phase, int inputs, Action<int> action)
        {
            Phase(lines, phase + "-start");
            MusicLyrics.UiResetPerformanceCounters();
            var times = new double[inputs];
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            TimeSpan cpu = process.TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            for (int index = 0; index < inputs; ++index)
            {
                long begin = Stopwatch.GetTimestamp();
                action(index);
                times[index] = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                // Let actual Composition changes and any coalesced input drain between events.
                await Task.Delay(16);
            }
            await Task.Delay(40);
            var work = MusicLyrics.UiPerformance;
            double processCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            long allocations = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Array.Sort(times);
            var sample = new LyricsPerformanceSample(lines, phase, inputs, work.PlaceCalls,
                work.VisitedLines, work.CreatedAnimations, work.StartedAnimations, work.GeometryWrites, MusicLyrics.UiVisibleLineCount,
                times[times.Length / 2], times[(int)Math.Ceiling(times.Length * .95) - 1], times[^1],
                wall.Elapsed.TotalMilliseconds, processCpu, allocations);
            samples.Add(sample);
            File.WriteAllText(Path.Combine(output, "lyrics-performance.json"), JsonSerializer.Serialize(new
            {
                baseline, samples,
                timingScope = "Callback CPU plus process CPU across per-event 16ms yields; not a GPU presentation/FPS measurement."
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!baseline && lines >= 400)
            {
                UiCheck(work.PlaceCalls > 0 && work.VisitedLines <= work.PlaceCalls * 256,
                    $"lyrics {phase} visits a bounded viewport band rather than all {lines} lines");
                UiCheck(work.CreatedAnimations == 0 && work.StartedAnimations <= work.PlaceCalls * 256 * 3,
                    $"lyrics {phase} reuses animation templates and bounds submissions independently of the {lines}-line song");
                UiCheck(work.GeometryWrites == 0,
                    $"settled lyrics {phase} does not rewrite glyph surface geometry for {lines} lines");
            }
            UiCheck(MusicLyrics.UiVisibleLineCount > 0 && MusicLyrics.UiVisibleLineCount <= Math.Min(lines, 256),
                $"lyrics {phase} keeps a visible bounded scene for {lines} lines");
            Phase(lines, phase + "-complete");
        }

        try
        {
            _preferences = _preferences with { ShowLyrics = true, AutoFullScreen = false };
            ApplyMediaState(ReceiverMediaState.Empty);
            foreach (int count in new[] { 80, 400, 2000 })
            {
                Phase(count, "source-start");
                int middle = count / 2;
                var audio = new ReceiverMediaState((ulong)(count + 1), ReceiverMediaKind.Audio, false,
                    "Performance " + count, "Synthetic artist", "Synthetic album", middle * 2000, count * 2000,
                    0, 0, MediaControls.None) { Session = 97, Source = "audio" };
                ApplyMediaState(audio);
                await UiWaitUntil(() => !_lyricsPending, "performance offline lookup settles " + count);
                var lines = Enumerable.Range(0, count).Select(index => new LyricLine(index * 2000,
                    index % 17 == 16 ? "" : index % 9 == 0
                        ? $"Synthetic line {index}: a longer phrase measures wrapping while scrolling through this bounded test song"
                        : $"Synthetic line {index}: keep the same scrolling and emphasis")).ToArray();
                _lyrics = new Lyrics(LyricsKind.Synced, lines);
                UpdateLyricsPresentation(false);
                MusicLyrics.AnimationsEnabled = true;
                await UiWaitForStableLayout(MusicPanel, MusicContent, MusicLyrics);
                await Task.Delay(900);
                UiCheck(_lyricsOpen && MusicLyrics.ActiveLineIndex == middle,
                    "performance fixture opens at the synthetic song midpoint " + count);
                await Measure(count, "automatic", 8, index => MusicLyrics.SetPosition((middle + index + 1) * 2000));
                int active = middle + 8;
                UiCheck(MusicLyrics.ActiveLineIndex == active && MusicLyrics.LineBlur(active) == 0,
                    "automatic performance scrolling preserves current-line emphasis " + count);
                await Measure(count, "wheel", 24, index => MusicLyrics.UiScrollBy(index % 2 == 0 ? -96 : 96));
                UiCheck(MusicLyrics.ActiveLineIndex == active && MusicLyrics.LineBlur(active + 1) == 0,
                    "wheel browsing preserves playback position and clears neighbouring blur " + count);
                await Measure(count, "touch", 24, index => MusicLyrics.UiScrollBy(index % 2 == 0 ? -12 : 12));
                UiCheck(MusicLyrics.ActiveLineIndex == active && MusicLyrics.LineBlur(active + 1) == 0,
                    "touch browsing preserves playback position and clears neighbouring blur " + count);
                MusicLyrics.SetPosition((active + 1) * 2000);
                await Task.Delay(3100);
                MusicLyrics.SetPosition((active + 1) * 2000);
                await Task.Delay(900);
                UiCheck(MusicLyrics.ActiveLineIndex == active + 1 && MusicLyrics.LineBlur(active + 1) == 0 &&
                    MusicLyrics.LineBlur(active) > 0,
                    "automatic playback restores emphasis and blur after manual browsing " + count);
                MusicLyrics.EffectsEnabled = false;
                MusicLyrics.UiResetPerformanceCounters();
                MusicLyrics.SetPosition(6000);
                UiCheck(MusicLyrics.UiBlurAnimationStarts == 0 && Enumerable.Range(0, 8).All(index => MusicLyrics.LineBlur(index) == 0),
                    "high contrast makes previously hidden lyrics sharp immediately after a long seek " + count);
                await Task.Delay(500);
                MusicLyrics.EffectsEnabled = true;
                MusicLyrics.SetPosition((active + 1) * 2000);
                MusicLyrics.UiScrollBy(1_000_000);
                UiCheck(Math.Abs(MusicLyrics.UiLineOpacity(3) - 0.3f) < 0.001f,
                    "a hidden former active row re-enters without stale emphasis " + count);
                MusicLyrics.UiScrollBy(-MusicLyrics.UiManualOffset);
                UiCheck(Math.Abs(MusicLyrics.UiManualOffset) < 0.001,
                    "manual input accumulation can return exactly to the playback anchor " + count);
                MusicLyrics.SetPosition((active + 2) * 2000);
                await Task.Delay(900);
                if (count is 80 or 2000) await UiCaptureAsync(output, "lyrics-performance-" + count);
                Phase(count, "source-complete");
            }
        }
        finally
        {
            _preferences = originalPreferences;
            ApplyMediaState(ReceiverMediaState.Empty);
            ResetLyrics();
            UpdateLyricsPresentation(false);
        }
    }
}
#endif
