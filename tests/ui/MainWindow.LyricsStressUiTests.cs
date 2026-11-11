#if CLEARCAST_UI_TESTS
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClearCast;

public sealed partial class MainWindow
{
    private async Task UiTestMusicFullScreenIsolationAsync(string output)
    {
        UiCheck(_receiver is null, "music full-screen isolation never starts a receiver or occupies its ports");
        var handler = _uiLyricsHandler ?? throw new InvalidOperationException("Missing offline lyrics handler.");
        handler.Respond = null;
        _preferences = _preferences with { ShowLyrics = false, AutoFullScreen = false };
        ApplyMediaState(ReceiverMediaState.Empty);
        var audio = new ReceiverMediaState(1, ReceiverMediaKind.Audio, true,
            "Full-screen isolation", "Test artist", "Test album", 30_000, 180_000, 0, 0, MediaControls.None)
            { Session = 91, Source = "audio", ContentRevision = 1 };
        ApplyMediaState(audio);
        await UiWaitUntil(() => !_lyricsPending, "music full-screen isolation offline no-match lookup settles");
        await UiWaitForStableLayout(MusicPanel, MusicContent);
        UiCheck(!_lyricsOpen && _lyrics is null && MusicArtwork.Source is null,
            "music full-screen isolation starts without rendered lyric lines or artwork");
        await UiCaptureAsync(output, "music-fullscreen-isolation-initial");
        void Checkpoint(int cycle, string phase) => File.AppendAllText(Path.Combine(_root, "ui-check-progress.log"),
            $"music-fullscreen-isolation {cycle} {phase} {DateTimeOffset.UtcNow:O}" + Environment.NewLine);
        try
        {
            for (int cycle = 0; cycle < 3; ++cycle)
            {
                Checkpoint(cycle, "before-enter");
                TogglePlayerFullScreen();
                Checkpoint(cycle, "after-enter-call");
                await UiWaitForStableLayout(MusicPanel, MusicContent);
                await Task.Delay(900);
                UiCheck(_playerFullScreen && AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen &&
                    !_lyricsOpen && _lyrics is null && MusicArtwork.Source is null,
                    "music full-screen isolation enters the shared presenter without lyric lines or artwork " + cycle);
                await UiCaptureAsync(output, "music-fullscreen-isolation-enter-" + cycle);
                Checkpoint(cycle, "before-exit");
                SetPlayerFullScreen(false);
                Checkpoint(cycle, "after-exit-call");
                await UiWaitForStableLayout(MusicPanel, MusicContent);
                await Task.Delay(900);
                UiCheck(!_playerFullScreen && AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped &&
                    !_lyricsOpen && _lyrics is null && MusicArtwork.Source is null,
                    "music full-screen isolation restores the window without lyric lines or artwork " + cycle);
                await UiCaptureAsync(output, "music-fullscreen-isolation-exit-" + cycle);
                Checkpoint(cycle, "cycle-complete");
            }
        }
        finally
        {
            SetPlayerFullScreen(false);
            ApplyMediaState(ReceiverMediaState.Empty);
        }
    }

    private async Task UiTestLyricsStressAsync(string output, string mode)
    {
        if (mode is not ("lyrics" or "artwork" or "both")) throw new ArgumentException("Unknown lyrics stress mode.", nameof(mode));
        bool renderLyrics = mode != "artwork", replaceArtwork = mode != "lyrics";
        UiCheck(_receiver is null, "lyrics stress " + mode + " never starts a receiver or occupies its ports");
        var handler = _uiLyricsHandler ?? throw new InvalidOperationException("Missing offline lyrics handler.");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string synced = string.Join('\n', Enumerable.Range(0, 90).Select(index =>
            $"[{index * 2 / 60:00}:{index * 2 % 60:00}.00]Stress {index:00} 한국어 中文 English"));
        string plain = string.Join('\n', Enumerable.Range(0, 60).Select(index => $"Plain {index:00} 한국어 中文 English"));
        long duration = 180_000;
        if (Environment.GetEnvironmentVariable("CLEARCAST_UI_LYRICS_FIXTURE") is { Length: > 0 } fixturePath)
        {
            var fixture = JsonSerializer.Deserialize<LrclibRecord>(await File.ReadAllTextAsync(fixturePath), options)
                ?? throw new InvalidDataException("Missing lyrics stress fixture.");
            synced = fixture.SyncedLyrics ?? throw new InvalidDataException("Stress fixture needs synced lyrics.");
            plain = fixture.PlainLyrics ?? plain;
            duration = (long)((fixture.Duration ?? 180) * 1000);
            UiCheck(duration > 0 && Lyrics.ParseSynced(synced) is not null, "lyrics stress fixture contains valid timed lines");
        }
        byte[]? artwork = replaceArtwork
            ? await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "ClearCast.png")) : null;
        var pendingArtwork = new List<Task>();
        var elapsed = Stopwatch.StartNew();
        var gate = new object();
        string trace = Path.Combine(output, "lyrics-stress-" + mode + ".jsonl");
        string latest = Path.Combine(output, "lyrics-stress-" + mode + "-latest.json");
        int canceledLookups = 0, syncedDisplays = 0, plainDisplays = 0, misses = 0;
        void Checkpoint(int cycle, string phase)
        {
            var entry = new
            {
                mode, cycle, phase, utc = DateTimeOffset.UtcNow, elapsedMs = elapsed.ElapsedMilliseconds,
                lyricsOpen = _lyricsOpen, lyricsPending = _lyricsPending, lyricsClosing = _lyricsClosing,
                lyricsFailed = _lyricsFailed, lyricsKind = _lyrics?.Kind.ToString(), lineCount = _lyrics?.Lines.Count ?? 0,
                lyricsGeneration = _lyricsGeneration, artworkGeneration = _artworkGeneration,
                artworkLoading = _artworkLoading, requests = handler.Requests, canceledLookups,
                syncedDisplays, plainDisplays, misses
            };
            string json = JsonSerializer.Serialize(entry, options);
            lock (gate)
            {
                File.AppendAllText(trace, json + Environment.NewLine);
                File.WriteAllText(latest, json);
                File.AppendAllText(Path.Combine(_root, "ui-check-progress.log"),
                    $"lyrics-stress {mode} {cycle:00} {phase} {elapsed.ElapsedMilliseconds}ms" + Environment.NewLine);
            }
        }
        static HttpResponseMessage Reply(LrclibRecord record, bool search, JsonSerializerOptions options) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(search ? JsonSerializer.Serialize(new[] { record }, options)
                : JsonSerializer.Serialize(record, options), Encoding.UTF8, "application/json")
        };
        LrclibRecord Response(int cycle, int kind) => new(
            TrackName: "Stress song " + cycle, ArtistName: "Stress artist " + cycle % 3,
            AlbumName: "Stress album " + cycle % 7, Duration: duration / 1000d,
            PlainLyrics: kind == 2 ? plain : null, SyncedLyrics: kind == 0 ? synced : null);
        void SetResponse(int cycle, int kind, int delay)
        {
            var record = Response(cycle, kind);
            handler.Respond = async (request, token) =>
            {
                Checkpoint(cycle, "lookup-start");
                try
                {
                    if (delay > 0) await Task.Delay(delay, token);
                    token.ThrowIfCancellationRequested();
                    Checkpoint(cycle, "lookup-response-" + kind);
                    return kind == 1 ? new HttpResponseMessage(HttpStatusCode.NotFound)
                        : Reply(record, request.RequestUri!.AbsolutePath.EndsWith("/search"), options);
                }
                catch (OperationCanceledException)
                {
                    ++canceledLookups;
                    Checkpoint(cycle, "lookup-canceled");
                    throw;
                }
            };
        }
        ReceiverMediaState Track(int cycle) => new(
            (ulong)(cycle + 2), ReceiverMediaKind.Audio, true, "Stress song " + cycle,
            "Stress artist " + cycle % 3, "Stress album " + cycle % 7,
            cycle < 0 ? 0 : cycle * 1937 % duration, duration,
            replaceArtwork ? (ulong)(cycle + 2) : 0, artwork?.Length ?? 0,
            cycle % 2 == 0 ? MediaControls.None : MediaControls.PlayPause | MediaControls.Previous | MediaControls.Next)
            { Session = 71, Source = "audio", ContentRevision = (ulong)(cycle + 2) };

        _preferences = _preferences with { ShowLyrics = renderLyrics, AutoFullScreen = false };
        ApplyMediaState(ReceiverMediaState.Empty);
        Checkpoint(-1, "start");
        try
        {
            // Reach the view through the actual delayed lookup before stressing its resource replacement.
            SetResponse(-1, renderLyrics ? 0 : 1, 0);
            ApplyMediaState(Track(-1));
            if (artwork is not null) pendingArtwork.Add(QueueMusicArtworkAsync(() => artwork, _artworkGeneration, null));
            await UiWaitUntil(() => !_lyricsPending, "lyrics stress initial offline lookup completes");
            if (renderLyrics)
            {
                UiCheck(!_lyricsFailed && _lyricsOpen && _lyrics is { Kind: LyricsKind.Synced },
                    "lyrics stress reaches the unchanged rendering path through a real async lookup");
                await Task.Delay(800);
                Checkpoint(-1, "same-turn-burst-start");
                // Do not yield between drops: the next Composition commit must encounter resources
                // replaced and collected within one dispatcher turn, as rapid skipping can cause.
                for (int burst = 0; burst < 30; ++burst)
                {
                    _lyrics = Lyrics.ParseSynced(synced);
                    _lyricsPending = false;
                    UpdateLyricsPresentation(true);
                    Checkpoint(burst, "same-turn-burst-before-gc");
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Checkpoint(burst, "same-turn-burst-after-gc");
                }
                Checkpoint(-1, "same-turn-burst-yield");
                await Task.Delay(1000);
                UiCheck(!_lyricsFailed && _lyricsOpen, "lyrics stress same-turn replacements survive the following Composition commit");
            }

            int[] holds = [100, 750, 1100, 850];
            for (int cycle = 0; cycle < 80; ++cycle)
            {
                int kind = renderLyrics ? (cycle % 5) switch { 2 => 2, 3 => 1, _ => 0 } : 1;
                int delay = cycle % 6 is 1 or 2 ? 450 : 0;
                SetResponse(cycle, kind, delay);
                if (replaceArtwork && AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                {
                    if (cycle % 20 == 8) { presenter.Minimize(); Checkpoint(cycle, "minimized"); }
                    else if (cycle % 20 == 12) { presenter.Restore(); Checkpoint(cycle, "restored"); }
                }
                Checkpoint(cycle, "before-media-change");
                ApplyMediaState(Track(cycle));
                Checkpoint(cycle, "after-media-change");
                if (artwork is not null)
                {
                    pendingArtwork.Add(QueueMusicArtworkAsync(() => artwork, _artworkGeneration, null));
                    Checkpoint(cycle, "artwork-queued");
                }
                if (cycle % 3 == 0)
                {
                    Checkpoint(cycle, "before-gc");
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Checkpoint(cycle, "after-gc");
                }
                await Task.Delay(holds[cycle % holds.Length]);
                if (!_lyricsPending)
                {
                    if (_lyrics is { Kind: LyricsKind.Synced }) ++syncedDisplays;
                    else if (_lyrics is { Kind: LyricsKind.Plain }) ++plainDisplays;
                    else ++misses;
                }
                Checkpoint(cycle, "cycle-complete");
                UiCheck(!_lyricsFailed && (renderLyrics || !_lyricsOpen),
                    $"lyrics stress {mode} cycle {cycle:00} completes without a rendering failure");
            }
            await Task.WhenAll(pendingArtwork).WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(1200);
            Checkpoint(80, "settled");
            UiCheck(handler.Requests > 20 && canceledLookups > 0, "lyrics stress exercises live and canceled delayed offline lookups");
            UiCheck(!renderLyrics || syncedDisplays > 0 && plainDisplays > 0 && misses > 0,
                "lyrics stress cycles through synced, plain and unavailable songs");
            UiCheck(artwork is null || MusicArtwork.Source is BitmapImage { PixelWidth: > 0 },
                "lyrics stress artwork generations settle on a decoded local project logo");
            await UiCaptureAsync(output, "lyrics-stress-" + mode + "-settled");
        }
        finally
        {
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
                presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized) presenter.Restore();
            Checkpoint(81, "cleanup-start");
            handler.Respond = null;
            ApplyMediaState(ReceiverMediaState.Empty);
            await Task.WhenAll(pendingArtwork).WaitAsync(TimeSpan.FromSeconds(15));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(1000);
            Checkpoint(81, "cleanup-complete");
        }
    }
}
#endif
