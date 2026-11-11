#if CLEARCAST_UI_TESTS
using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClearCast;

public sealed partial class MainWindow
{
    private async Task UiTestReceiverNetworkChangesAsync()
    {
        UiCheck(_receiver is null && !_receiverTimer.IsRunning && !_startingReceiver && _stopTask is null,
            "network transition checks use a stopped receiver and no receiver ports");
        StopReceiverNetworkChanges();
        await UiWaitUntil(() => !_networkCheckRunning && Volatile.Read(ref _networkCheckQueued) == 0,
            "earlier network notifications drain");
        bool previousReady = _receiverReady;
        bool previousRecovery = _networkRecoveryRequested;
        // These objects are identity tokens only. The test-only delegates
        // intercept every probe/stop/start; no receiver member is invoked.
        var first = (AirPlayReceiver)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AirPlayReceiver));
        var second = (AirPlayReceiver)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AirPlayReceiver));
        var gates = new List<TaskCompletionSource<bool>>();
        int probes = 0, active = 0, maximumActive = 0, stops = 0, starts = 0;
        Task Drain() => UiWaitUntil(() => !_networkCheckRunning && Volatile.Read(ref _networkCheckQueued) == 0,
            "bounded network pump drains");
        try
        {
            _receiver = first;
            _receiverReady = true;
            _networkRecoveryRequested = false;
            _uiNetworkStop = () => { stops++; return Task.FromResult(true); };
            _uiNetworkStart = () => starts++;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            gates.Add(release);
            _uiNetworkProbe = async () =>
            {
                probes++;
                maximumActive = Math.Max(maximumActive, ++active);
                entered.TrySetResult();
                try { return await release.Task; }
                finally { active--; }
            };
            await Task.Run(() => Parallel.For(0, 5000, _ => QueueReceiverNetworkCheck()));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => Parallel.For(0, 5000, _ => QueueReceiverNetworkCheck()));
            await UiWaitUntil(() => _networkCheckPending && Volatile.Read(ref _networkCheckQueued) == 0,
                "network burst retains its single pending check");
            UiCheck(probes == 1 && active == 1 && _networkCheckRunning,
                "concurrent network notifications keep one active probe and one pending flag");
            release.SetResult(true);
            await Drain();
            UiCheck(probes == 2 && maximumActive == 1 && stops == 0 && starts == 0 && ReferenceEquals(_receiver, first),
                "valid selected interface survives burst and preferred-interface changes without restart");

            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            gates.Add(release);
            _uiNetworkProbe = () => { probes++; entered.TrySetResult(); return release.Task; };
            QueueReceiverNetworkCheck();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _receiver = second;
            release.SetResult(false);
            await Drain();
            UiCheck(stops == 0 && starts == 0 && ReferenceEquals(_receiver, second),
                "an old invalid probe released after receiver replacement cannot stop the new receiver");

            _uiNetworkProbe = () => { probes++; return Task.FromException<bool>(new EntryPointNotFoundException("fixture probe unavailable")); };
            QueueReceiverNetworkCheck();
            await Drain();
            UiCheck(stops == 0 && starts == 0 && ReferenceEquals(_receiver, second),
                "network interop exceptions retain the receiver and release the dispatcher pump");
            int afterFailure = probes;
            _uiNetworkProbe = () => { probes++; return Task.FromResult(true); };
            QueueReceiverNetworkCheck();
            await Drain();
            UiCheck(probes == afterFailure + 1 && stops == 0,
                "a later network notification retries a previously indeterminate probe");

            int confirmations = 0;
            _uiNetworkProbe = () => Task.FromResult(++confirmations != 1);
            var confirmationTime = System.Diagnostics.Stopwatch.StartNew();
            QueueReceiverNetworkCheck();
            await Drain();
            UiCheck(confirmations == 2 && confirmationTime.ElapsedMilliseconds >= 200 && stops == 0 && starts == 0,
                "a transient invalid scope is confirmed after a bounded delay without interrupting playback");

            _uiNetworkProbe = () => { probes++; return Task.FromResult(false); };
            _uiNetworkStop = () =>
            {
                stops++;
                _receiver = null;
                _receiverReady = false;
                return Task.FromResult(true);
            };
            _uiNetworkStart = () => starts++; // Simulate startup finding no adapter.
            int beforeInvalid = probes;
            QueueReceiverNetworkCheck();
            await Drain();
            UiCheck(probes == beforeInvalid + 2 && stops == 1 && starts == 1 && _receiver is null && _networkRecoveryRequested,
                "confirmed invalid scope stops once and attempts recovery on an available adapter");
            await Task.Delay(350);
            UiCheck(starts == 1 && !_networkCheckRunning && !_networkCheckPending,
                "no-adapter recovery waits for another notification instead of spinning or accumulating retries");
            _uiNetworkStart = () =>
            {
                starts++;
                _receiver = second;
                _receiverReady = true;
                _networkRecoveryRequested = false; // The real Ready handler clears it.
            };
            QueueReceiverNetworkCheck();
            await Drain();
            UiCheck(stops == 1 && starts == 2 && ReferenceEquals(_receiver, second),
                "the next network notification recovers a receiver after a no-adapter startup");
            _uiNetworkProbe = () => Task.FromResult(true);
            QueueReceiverNetworkCheck();
            await Drain();
            UiCheck(stops == 1 && starts == 2 && !_networkRecoveryRequested,
                "the recovered valid scope remains stable on following notifications");
        }
        finally
        {
            _receiver = null;
            _receiverReady = previousReady;
            foreach (var gate in gates) gate.TrySetResult(true);
            try { await Drain(); }
            finally
            {
                _uiNetworkProbe = null;
                _uiNetworkStop = null;
                _uiNetworkStart = null;
                _networkRecoveryRequested = previousRecovery;
                InitializeReceiverNetworkChanges();
            }
        }
    }

    private async Task UiTestMusicArtworkBackpressureAsync()
    {
        UiCheck(_receiver is null && !_artworkLoading, "artwork backpressure uses a stopped receiver and drained loader");
        byte[] pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXYxDpOfEfAASIAmgnDVFdAAAAAElFTkSuQmCC");
        var audio = new ReceiverMediaState(1, ReceiverMediaKind.Audio, false, "fixture", "", "", 0, 1000, 0, 0,
            MediaControls.None) { Session = 9001 };
        ApplyMediaState(audio);
        using (var release = new ManualResetEventSlim())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int reads = 0, latestRead = 0;
            Task loading = QueueMusicArtworkAsync(() =>
            {
                Interlocked.Increment(ref reads);
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Held artwork read was not released.");
                return pixel;
            }, ++_artworkGeneration, null);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (int index = 1; index <= 5000; index++)
                {
                    int revision = index;
                    await QueueMusicArtworkAsync(() =>
                    {
                        Interlocked.Increment(ref reads);
                        latestRead = revision;
                        return pixel;
                    }, ++_artworkGeneration, null);
                }
                UiCheck(reads == 1 && _artworkLoading && _pendingMusicArtwork.HasValue,
                    "5000 artwork revisions retain one active read and one latest descriptor");
            }
            finally { release.Set(); await loading.WaitAsync(TimeSpan.FromSeconds(10)); }
            UiCheck(reads == 2 && latestRead == 5000 && !_artworkLoading && !_pendingMusicArtwork.HasValue &&
                ReferenceEquals(_musicArtworkBytes, pixel) && MusicArtwork.Source is BitmapImage,
                "only the newest pending artwork is read and decoded after a slow consumer drains");
        }
        using (var release = new ManualResetEventSlim())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int reads = 0;
            Task loading = QueueMusicArtworkAsync(() =>
            {
                Interlocked.Increment(ref reads);
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Held artwork read was not released.");
                return pixel;
            }, ++_artworkGeneration, null);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await QueueMusicArtworkAsync(() => { Interlocked.Increment(ref reads); return pixel; }, ++_artworkGeneration, null);
                ApplyMediaState(ReceiverMediaState.Empty);
                UiCheck(!_pendingMusicArtwork.HasValue && MusicArtwork.Source is null,
                    "ending the source clears pending artwork without waiting for its active read");
            }
            finally { release.Set(); await loading.WaitAsync(TimeSpan.FromSeconds(10)); }
            UiCheck(reads == 1 && MusicArtwork.Source is null && !_artworkLoading,
                "a released old artwork read cannot revive a finished source or read its discarded pending cover");
        }
        ApplyMediaState(audio);
        await QueueMusicArtworkAsync(() => throw new IOException("fixture artwork failure"), ++_artworkGeneration, null);
        await QueueMusicArtworkAsync(() => pixel, ++_artworkGeneration, null);
        UiCheck(ReferenceEquals(_musicArtworkBytes, pixel) && !_artworkLoading,
            "artwork read failures release the pump and later valid artwork still decodes");
        ApplyMediaState(ReceiverMediaState.Empty);
    }
}
#endif
