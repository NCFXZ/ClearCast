#if CLEARCAST_UI_TESTS
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ClearCast;

public sealed partial class MainWindow
{
    private async Task UiTestLyricsBackpressureAuditAsync(string output)
    {
        UiCheck(_receiver is null, "lyrics lookup audit never starts a receiver or occupies its ports");
        ApplyMediaState(ReceiverMediaState.Empty);
        ResetLyrics();
        await _lyricsLookupTask;
        var originalClient = _lyricsClient;
        var originalHandler = _uiLyricsHandler;
        var originalPreferences = _preferences;
        var originalStop = _stopTask;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHeaders = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopHeaders = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryHeaders = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        const string firstTitle = "Audit held first", latestTitle = "Audit latest 999";
        const string stopTitle = "Audit held shutdown", discardedTitle = "Audit discarded shutdown";
        const string recoveryTitle = "Audit failed stop recovery";
        var titles = new List<string>();
        var gate = new object();
        int active = 0, maximumActive = 0, cancellationSignals = 0, recoveryRequests = 0;
        void Phase(string name)
        {
            string json;
            lock (gate)
                json = JsonSerializer.Serialize(new { phase = name, utc = DateTimeOffset.UtcNow,
                    active, maximumActive, cancellationSignals, titles = titles.ToArray(),
                    lyricsPending = _lyricsPending, lyricsFailed = _lyricsFailed });
            File.AppendAllText(Path.Combine(output, "lyrics-lookup-backpressure.jsonl"), json + Environment.NewLine);
            File.AppendAllText(Path.Combine(_root, "ui-check-progress.log"), "lyrics-lookup-backpressure " + name + Environment.NewLine);
        }
        static string RequestTitle(HttpRequestMessage request)
        {
            string value = request.RequestUri!.Query.TrimStart('?').Split('&')
                .First(part => part.StartsWith("track_name=", StringComparison.Ordinal))["track_name=".Length..];
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        static HttpResponseMessage Reply(string title) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                trackName = title, artistName = "Audit artist", albumName = "Audit album", duration = 180,
                syncedLyrics = "[00:00.00]" + title + "\n[00:30.00]Current audit line\n[01:00.00]Next audit line"
            }), Encoding.UTF8, "application/json")
        };
        var handler = new UiOfflineLyricsHandler
        {
            Respond = async (request, token) =>
            {
                string title = RequestTitle(request);
                lock (gate)
                {
                    ++active;
                    maximumActive = Math.Max(maximumActive, active);
                    titles.Add(title);
                }
                using var canceled = token.Register(() => Interlocked.Increment(ref cancellationSignals));
                try
                {
                    if (title == firstTitle)
                    {
                        firstEntered.TrySetResult();
                        // Intentionally ignore cancellation until the test releases the headers.
                        return await firstHeaders.Task;
                    }
                    if (title == stopTitle)
                    {
                        stopEntered.TrySetResult();
                        return await stopHeaders.Task;
                    }
                    if (title == recoveryTitle && Interlocked.Increment(ref recoveryRequests) == 1)
                    {
                        recoveryEntered.TrySetResult();
                        return await recoveryHeaders.Task;
                    }
                    return Reply(title);
                }
                finally { lock (gate) --active; }
            }
        };
        using var client = new LyricsClient(handler);
        _lyricsClient = client;
        _uiLyricsHandler = handler;
        _preferences = _preferences with { ShowLyrics = true, AutoFullScreen = false };
        var audio = new ReceiverMediaState(1, ReceiverMediaKind.Audio, false, firstTitle, "Audit artist", "Audit album",
            45_000, 180_000, 0, 0, MediaControls.None) { Session = 96, Source = "audio" };
        try
        {
            Phase("first-request-starting");
            ApplyMediaState(audio);
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Phase("first-headers-held");
            for (int index = 0; index < 1000; ++index)
                ApplyMediaState(audio with { Revision = (ulong)(index + 2), Title = "Audit latest " + index });
            // Waiting beyond the lookup debounce exposes an uncapped implementation: it sends
            // the newest request while the canceled first handler is still waiting for headers.
            await Task.Delay(LyricsLookupDelay + TimeSpan.FromMilliseconds(300));
            Phase("thousand-track-flood-settled");
            lock (gate)
                UiCheck(active == 1 && maximumActive == 1 && titles.SequenceEqual([firstTitle]) && cancellationSignals > 0,
                    "a thousand rapid track changes cancel immediately and retain one active HTTP request while cancellation drains");
            firstHeaders.SetResult(Reply(firstTitle));
            await UiWaitUntil(() => !_lyricsPending && _lyricsTrack?.Title == latestTitle,
                "lyrics lookup audit newest queued track completes after the canceled request drains");
            await _lyricsLookupTask;
            Phase("latest-result-applied");
            lock (gate)
                UiCheck(active == 0 && maximumActive == 1 && titles.SequenceEqual([firstTitle, latestTitle]),
                    "the lookup pump sends only the first and newest track and discards all intermediate tracks");
            UiCheck(!_lyricsFailed && LyricsAvailable && _lyricsOpen && MusicLyricsButton.IsEnabled &&
                _lyrics!.Lines[0].Text == latestTitle,
                "the newest lyrics remain usable and the canceled first result cannot replace them");
            await UiWaitForStableLayout(MusicPanel, MusicContent, MusicLyrics);
            await UiCaptureAsync(output, "lyrics-lookup-backpressure-latest");

            var unchanged = audio with { Revision = 2000, Title = recoveryTitle };
            ApplyMediaState(unchanged);
            await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CancelPendingLyricsWork();
            int canceledGeneration = _lyricsGeneration;
            _stopTask = Task.FromResult(false);
            ApplyMediaState(unchanged);
            UiCheck(!_lyricsPending && _lyricsTrack is null && _lyricsGeneration == canceledGeneration,
                "a pending native stop cannot restart the lyrics lookup it just canceled");
            _stopTask = originalStop;
            ApplyMediaState(unchanged);
            UiCheck(_lyricsPending && _lyricsTrack?.Title == recoveryTitle && _pendingLyricsLookup is not null,
                "a failed stop resumes the canceled lookup on an identical metadata snapshot");
            await Task.Delay(LyricsLookupDelay + TimeSpan.FromMilliseconds(300));
            UiCheck(recoveryRequests == 1 && maximumActive == 1,
                "failed-stop recovery waits for the canceled HTTP request without opening another worker");
            recoveryHeaders.SetResult(Reply(recoveryTitle));
            await UiWaitUntil(() => !_lyricsPending && _lyricsTrack?.Title == recoveryTitle,
                "failed-stop lyrics recovery completes after the stale response drains");
            await _lyricsLookupTask;
            UiCheck(recoveryRequests == 2 && LyricsAvailable && _lyrics!.Lines[0].Text == recoveryTitle && !_lyricsFailed,
                "the recovered lookup applies current lyrics exactly once after cancellation");
            int stableRequests = handler.Requests, stableGeneration = _lyricsGeneration;
            for (int sample = 0; sample < 1000; sample++) ApplyMediaState(unchanged);
            await Task.Delay(LyricsLookupDelay + TimeSpan.FromMilliseconds(100));
            UiCheck(handler.Requests == stableRequests && _lyricsGeneration == stableGeneration && !_lyricsPending,
                "identical metadata does not repeatedly restart a completed recovery lookup");

            _lyricsFailed = true;
            CancelPendingLyricsWork();
            stableGeneration = _lyricsGeneration;
            for (int sample = 0; sample < 1000; sample++) ApplyMediaState(unchanged);
            UiCheck(handler.Requests == stableRequests && _lyricsGeneration == stableGeneration && !_lyricsPending,
                "failed lyrics remain disabled without resetting or looking up each unchanged snapshot");
            _lyricsFailed = false;
            var unnamed = unchanged with { Title = "" };
            ApplyMediaState(unnamed);
            stableGeneration = _lyricsGeneration;
            for (int sample = 0; sample < 1000; sample++) ApplyMediaState(unnamed);
            await Task.Delay(LyricsLookupDelay + TimeSpan.FromMilliseconds(100));
            UiCheck(handler.Requests == stableRequests && _lyricsGeneration == stableGeneration && !_lyricsPending && _lyricsTrack is null,
                "unnamed music does not repeatedly reset lyrics or issue lookup requests");
            Phase("failed-stop-recovery-settled");

            ApplyMediaState(audio with { Revision = 2001, Title = stopTitle });
            await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ApplyMediaState(audio with { Revision = 2002, Title = discardedTitle });
            Phase("shutdown-with-active-and-pending");
            StopLyrics();
            UiCheck(_lyricsClient is null, "stopping lyrics disposes and clears its HTTP client");
            stopHeaders.SetResult(Reply(stopTitle));
            await _lyricsLookupTask.WaitAsync(TimeSpan.FromSeconds(5));
            Phase("shutdown-drained");
            lock (gate)
                UiCheck(active == 0 && maximumActive == 1 && titles.SequenceEqual([firstTitle, latestTitle, recoveryTitle, recoveryTitle, stopTitle]),
                    "stopping lyrics revokes the pending track and drains the canceled active request without another HTTP send");
            UiCheck(!_lyricsFailed, "stale lookup cancellation during shutdown does not disable lyrics");
        }
        finally
        {
            if (!firstHeaders.Task.IsCompleted) firstHeaders.TrySetResult(Reply(firstTitle));
            if (!stopHeaders.Task.IsCompleted) stopHeaders.TrySetResult(Reply(stopTitle));
            if (!recoveryHeaders.Task.IsCompleted) recoveryHeaders.TrySetResult(Reply(recoveryTitle));
            _stopTask = originalStop;
            _lyricsFailed = false;
            ResetLyrics();
            ApplyMediaState(ReceiverMediaState.Empty);
            await _lyricsLookupTask.WaitAsync(TimeSpan.FromSeconds(5));
            _lyricsClient = originalClient;
            _uiLyricsHandler = originalHandler;
            _preferences = originalPreferences;
            UpdateLyricsPresentation(false);
            Phase("cleanup-complete");
        }
    }
}
#endif
