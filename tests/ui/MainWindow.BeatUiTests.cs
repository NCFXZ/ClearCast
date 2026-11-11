#if CLEARCAST_UI_TESTS
using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace ClearCast;

public sealed partial class MainWindow
{
    private async Task UiTestMusicFastPollingAsync()
    {
        UiCheck(_receiver is not null && _receiverReady, "fast music polling test uses the real disposable receiver");
        _receiverTimer.Stop();
        var original = _mediaState;
        try
        {
            var audio = new ReceiverMediaState(1, ReceiverMediaKind.Audio, true, "Synthetic polling track", "Test artist", "Test album",
                0, 10000, 1, 1, MediaControls.None) { Session = 42 };
            ApplyMediaState(audio);
            byte[] artwork = await UiCreateRhythmArtworkAsync();
            await ApplyMusicArtworkAsync(artwork, _artworkGeneration, _receiver);
            if (MusicBackground.IsAnimating)
            {
                long reads = _uiMusicActivityReads;
                UiCheck(_musicActivityTimer?.IsRunning == true,
                    "asynchronous artwork completion enables the fast music-only timer");
                await UiWaitUntil(() => _uiMusicActivityReads >= reads + 3, "real native media reads through the fast music timer");
                UiCheck(_hasMusic && _mediaState == audio && MusicTitle.Text == audio.Title,
                    "fast native audio-only polling does not apply idle native metadata or rebuild the synthetic music page");
                MusicBackground.SetActive(true, false);
                await UiWaitUntil(() => _musicActivityTimer?.IsRunning != true, "fast polling stops when background animation is unavailable");
                UpdateMusicBackgroundState();
                UiCheck(_musicActivityTimer?.IsRunning == true,
                    "restoring the current motion policy resumes fast music-only reads");
            }
            ApplyMediaState(ReceiverMediaState.Empty);
            UiCheck(_musicActivityTimer?.IsRunning != true,
                "ending music immediately stops the native fast activity timer");
        }
        finally
        {
            ApplyMediaState(original);
            _receiverTimer.Start();
        }
    }

    private async Task UiTestBeatMotionAsync(string output)
    {
        if (!MusicBackground.IsAnimating || !MusicBackground.HasArtworkFlow)
        {
            _uiTestLayouts.Add(new { phase = "strong-beat background", status = "unsupported",
                reason = "The current motion/accessibility policy or artwork does not enable background animation." });
            return;
        }
        var source = _mediaState;
        ApplyMediaState(source with { Controls = MediaControls.PlayPause | MediaControls.Previous | MediaControls.Next });
        await UiWaitForStableLayout(MusicContent, MusicArtworkFrame, MusicPlayPauseButton);
        FrameworkElement[] foreground = [MusicArtworkFrame, MusicTitle, MusicArtist, MusicAlbum,
            MusicProgress, MusicPreviousButton, MusicPlayPauseButton, MusicNextButton, MusicDisconnectButton];
        var originalBounds = foreground.Select(UiMeasure).ToArray();
        long flowStarts = MusicBackground.AnimationStartCount;
        long beatStarts = MusicBackground.BeatStartCount;
        ulong epoch = MusicBackground.BeatEpoch + 1;
        string theme = WindowLayout.ActualTheme.ToString().ToLowerInvariant();
        void Feed(ulong generation, ulong sequence, float strength = 1, int age = 0) =>
            ApplyMusicAudioActivity(new(true, 0.3f, 0.2f, 0)
            {
                BeatEpoch = generation, Beats = [new(sequence, strength, age)]
            }, TimeSpan.Zero, true);
        void CheckForeground(string stage) => UiCheck(originalBounds.SequenceEqual(foreground.Select(UiMeasure)),
            "beat motion leaves artwork text progress and button bounds unchanged " + stage);

        MusicBackground.ResetBeatActivity();
        MusicBackground.PauseFlowClockForTest(true);
        try
        {
            // Composition commands cross to a render thread; allow the held
            // phase to be presented before taking the baseline DIB.
            await Task.Delay(80);
            nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var content = UiCompositorContentBounds(window, MusicContent);
            var caption = UiCompositorContentBounds(window, AppTitleBar, padding: 0);
            var cover = UiCompositorContentBounds(window, MusicArtwork, padding: 0);
            int coverInset = (int)Math.Ceiling(12 * WindowLayout.XamlRoot.RasterizationScale);
            var baseline = await Task.Run(() => UiCaptureOwnedWindow(window));
            var watch = Stopwatch.StartNew();
            Feed(epoch, 1);
            UiCheck(MusicBackground.BeatStartCount == beatStarts + 1 && MusicBackground.BeatAnimationActive &&
                MusicBackground.AnimationChannelCount == 37,
                "a new strong beat starts one finite background impulse alongside existing flow expressions");
            Feed(epoch, 1);
            ApplyMusicAudioActivity(new(true, 0.3f, 0.2f, 0) { BeatEpoch = epoch }, TimeSpan.Zero, true);
            UiCheck(MusicBackground.BeatStartCount == beatStarts + 1 && MusicBackground.BeatAnimationActive,
                "duplicate and empty beat reads neither restart nor interrupt the current rebound");
            CheckForeground("attack");
            await Task.Delay(35);
            var peak = await Task.Run(() => UiCaptureOwnedWindow(window));
            double peakMilliseconds = watch.Elapsed.TotalMilliseconds;
            CheckForeground("peak");
            int reboundDelay = (int)Math.Max(0, 150 - watch.Elapsed.TotalMilliseconds);
            if (reboundDelay > 0) await Task.Delay(reboundDelay);
            var rebound = await Task.Run(() => UiCaptureOwnedWindow(window));
            double reboundMilliseconds = watch.Elapsed.TotalMilliseconds;
            CheckForeground("rebound");
            await Task.Delay((int)Math.Max(0, BeatImpulse.DurationMs + 80 - watch.Elapsed.TotalMilliseconds));
            var settled = await Task.Run(() => UiCaptureOwnedWindow(window));
            CheckForeground("settled");
            UiCheck(!MusicBackground.BeatAnimationActive && MusicBackground.AnimationChannelCount == 36,
                "the short impulse finishes and releases its channel while normal flow remains active");
            UiCheck(MusicBackground.AnimationStartCount == flowStarts,
                "strong-beat impulses do not restart the phase clock or its opacity expressions");
            UiCheck(baseline.Width == peak.Width && baseline.Height == peak.Height &&
                baseline.Width == settled.Width && baseline.Height == settled.Height,
                "owned-window impulse captures retain identical dimensions");
            int backgroundPeakChanged = 0, coverChanged = 0;
            long peakDifference = 0, settledDifference = 0;
            long captionPeakDifference = 0, captionSettledDifference = 0;
            for (int index = 0; index < baseline.Pixels.Length; index += 4)
            {
                int pixel = index / 4, x = pixel % baseline.Width, y = pixel / baseline.Width;
                int difference = 0, afterDifference = 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    difference += Math.Abs(baseline.Pixels[index + channel] - peak.Pixels[index + channel]);
                    afterDifference += Math.Abs(baseline.Pixels[index + channel] - settled.Pixels[index + channel]);
                }
                if (x >= cover.Left + coverInset && x < cover.Right - coverInset &&
                    y >= cover.Top + coverInset && y < cover.Bottom - coverInset && difference > 0)
                    coverChanged++;
                // Window activation independently recolors the system caption. It is
                // foreground UI, so measure it separately from the held background.
                bool captionPixel = x >= caption.Left && x < caption.Right && y >= caption.Top && y < caption.Bottom;
                if (captionPixel)
                {
                    captionPeakDifference += difference;
                    captionSettledDifference += afterDifference;
                }
                if (!captionPixel && (x < content.Left || x >= content.Right || y < content.Top || y >= content.Bottom))
                {
                    peakDifference += difference;
                    settledDifference += afterDifference;
                    if (difference >= 3) backgroundPeakChanged++;
                }
            }
            await UiSaveCompositorFrameAsync(output, theme + "-beat-background-baseline", baseline);
            await UiSaveCompositorFrameAsync(output, theme + "-beat-background-attack", peak);
            await UiSaveCompositorFrameAsync(output, theme + "-beat-background-rebound", rebound);
            await UiSaveCompositorFrameAsync(output, theme + "-beat-background-settled", settled);
            _uiTestLayouts.Add(new { phase = "strong-beat pixel evidence", theme, peakMilliseconds, reboundMilliseconds,
                backgroundPeakChanged, coverChanged, peakDifference, settledDifference,
                captionPeakDifference, captionSettledDifference,
                caption = new { caption.Left, caption.Top, caption.Right, caption.Bottom },
                cover = new { cover.Left, cover.Top, cover.Right, cover.Bottom },
                content = new { content.Left, content.Top, content.Right, content.Bottom } });
            UiCheck(backgroundPeakChanged > 1000 && peakDifference > 10000,
                "actual owned-window pixels show a background impulse with the slow flow clock held still");
            UiCheck(coverChanged <= 10,
                "opaque artwork pixels stay fixed while the surrounding background pulses");
            UiCheck(settledDifference <= peakDifference * 0.02 + 100,
                "actual background pixels return to their baseline after the finite rebound");

            Feed(epoch, 2, 0.02f);
            Feed(epoch, 2); // A rejected soft event cannot be retried as strong.
            Feed(epoch, 3, 1, 300);
            Feed(epoch, 3); // A stale event cannot become a new pulse on a later read.
            Feed(epoch - 1, 100);
            UiCheck(MusicBackground.BeatStartCount == beatStarts + 1,
                "soft stale repeated and older-epoch events never produce catch-up pulses");
            Feed(epoch, 4);
            UiCheck(MusicBackground.BeatStartCount == beatStarts + 2 && MusicBackground.BeatAnimationActive,
                "the next fresh strong beat can pulse after the prior rebound");
            float direction = MusicBackground.BeatDirectionForTest;
            await Task.Delay(BeatImpulse.MinimumIntervalMs + 10);
            Feed(epoch, 5);
            UiCheck(MusicBackground.BeatStartCount == beatStarts + 3 && MusicBackground.BeatAnimationActive &&
                MusicBackground.BeatDirectionForTest == direction && MusicBackground.AnimationStartCount == flowStarts,
                "a new beat during the damped tail keeps its offset direction and the existing flow clock");
            string flushJson = System.Text.Json.JsonSerializer.Serialize(new { revision = 1, kind = "audio", playing = true,
                title = "Track", artist = "Artist", album = "Album", positionMs = 0, durationMs = 10000,
                artworkRevision = 0, artworkBytes = 0, controls = 0,
                audioActivity = new { valid = false, energy = 0, density = 0, ageMs = int.MaxValue, beatEpoch = epoch + 1, beats = Array.Empty<object>() } });
            UiCheck(ReceiverMediaState.TryParse(System.Text.Encoding.UTF8.GetBytes(flushJson), out var flushed),
                "a real-shaped flushed snapshot preserves its new epoch before a fresh sample arrives");
            ApplyMusicAudioActivity(flushed.AudioActivity, TimeSpan.Zero, true);
            UiCheck(!MusicBackground.BeatAnimationActive,
                "a flush epoch with no events immediately clears the previous beat motion");
            Feed(epoch, 6);
            UiCheck(MusicBackground.BeatStartCount == beatStarts + 3,
                "an old history cannot restore a pulse after a newer flush epoch");
            await Task.Delay(250);
            Feed(epoch + 1, 1);
            UiCheck(MusicBackground.BeatAnimationActive, "a fresh beat in the new epoch starts normally");
            MusicBackground.SetActive(true, false);
            UiCheck(!MusicBackground.BeatAnimationActive && MusicBackground.AnimationChannelCount == 0,
                "motion policy changes immediately clear pulse and flow motion");
            Feed(epoch + 1, 2);
            MusicBackground.SetActive(true, true);
            Feed(epoch + 1, 2);
            UiCheck(!MusicBackground.BeatAnimationActive,
                "beats observed while animation is disabled are consumed without replay on resume");
            UiCheck(_musicActivityTimer?.IsRunning != true,
                "synthetic stopped-receiver media tests never start native fast polling");
            _uiTestLayouts.Add(new { phase = "strong-beat background", theme, status = "verified", peakMilliseconds, reboundMilliseconds,
                backgroundPeakChanged, coverChanged, peakDifference, settledDifference,
                foregroundBoundsUnchanged = true, phaseStartsUnchanged = true,
                note = "Synthetic parsed-shape activity drives the real host and GPU background only; PCM detection is checked independently." });
        }
        finally
        {
            MusicBackground.ResetBeatActivity();
            MusicBackground.PauseFlowClockForTest(false);
            ApplyMediaState(source with { AudioActivity = default });
        }
    }
}
#endif
