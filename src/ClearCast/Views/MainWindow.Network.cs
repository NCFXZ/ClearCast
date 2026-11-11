using System.Net.NetworkInformation;
using Microsoft.UI.Dispatching;

namespace ClearCast;

public sealed partial class MainWindow
{
    private int _networkCheckQueued;
    private bool _networkCheckRunning;
    private bool _networkCheckPending;
    private bool _networkRecoveryRequested;
#if CLEARCAST_UI_TESTS
    private Func<Task<bool>>? _uiNetworkProbe;
    private Func<Task<bool>>? _uiNetworkStop;
    private Action? _uiNetworkStart;
#endif

    private void InitializeReceiverNetworkChanges()
    {
        NetworkChange.NetworkAddressChanged += ReceiverNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += ReceiverNetworkAvailabilityChanged;
    }

    private void StopReceiverNetworkChanges()
    {
        NetworkChange.NetworkAddressChanged -= ReceiverNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= ReceiverNetworkAvailabilityChanged;
    }

    private void ReceiverNetworkAddressChanged(object? sender, EventArgs args) => QueueReceiverNetworkCheck();
    private void ReceiverNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => QueueReceiverNetworkCheck();

    // NetworkChange can arrive on several worker threads. Keep only one posted
    // callback and one current probe plus a pending flag, regardless of burst size.
    private void QueueReceiverNetworkCheck()
    {
        if (Interlocked.Exchange(ref _networkCheckQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, CheckReceiverNetwork))
            Interlocked.Exchange(ref _networkCheckQueued, 0);
    }

    private async void CheckReceiverNetwork()
    {
        Interlocked.Exchange(ref _networkCheckQueued, 0);
        if (_closed || _closing) return;
        _networkCheckPending = true;
        if (_networkCheckRunning) return;
        _networkCheckRunning = true;
        try
        {
            while (_networkCheckPending && !_closed && !_closing)
            {
                _networkCheckPending = false;
                if (_startingReceiver || _stopTask is not null || _resettingPairings) continue;
                if (_receiver is not { } receiver)
                {
                    if (_networkRecoveryRequested) StartReceiverAfterNetworkChange();
                    continue;
                }
                if (!_receiverReady) continue;
                // The native snapshot checks the exact adapter LUID, indices and
                // bound addresses. A new preferred VPN/NIC must not interrupt an
                // existing, still-valid connection.
                bool current;
                try
                {
                    current = await ProbeReceiverNetworkAsync();
                    if (!current)
                    {
                        // Adapter enumeration may fail transiently during a
                        // network transition. Confirm once within this same
                        // bounded pump before interrupting a playing session.
                        await Task.Delay(250);
                        if (_closed || _closing || !ReferenceEquals(_receiver, receiver) ||
                            _stopTask is not null || _resettingPairings) continue;
                        current = await ProbeReceiverNetworkAsync();
                    }
                }
                catch
                {
                    // An unavailable interop probe is indeterminate; a later
                    // Ready/network notification retries without a dispatcher
                    // exception or replacing an otherwise working receiver.
                    continue;
                }
                if (current || _closed || _closing || !ReferenceEquals(_receiver, receiver) ||
                    _stopTask is not null || _resettingPairings) continue;
                _networkRecoveryRequested = true;
                if (await StopReceiverAfterNetworkChangeAsync() && !_closed && !_closing)
                    StartReceiverAfterNetworkChange();
            }
        }
        finally { _networkCheckRunning = false; }
    }

    private Task<bool> ProbeReceiverNetworkAsync()
    {
#if CLEARCAST_UI_TESTS
        if (_uiNetworkProbe is { } probe) return probe();
#endif
        return Task.Run(() => NativeReceiver.NetworkCurrent() != 0);
    }

    private Task<bool> StopReceiverAfterNetworkChangeAsync()
    {
#if CLEARCAST_UI_TESTS
        if (_uiNetworkStop is { } stop) return stop();
#endif
        return StopReceiverAsync();
    }

    private void StartReceiverAfterNetworkChange()
    {
#if CLEARCAST_UI_TESTS
        if (_uiNetworkStart is { } start) { start(); return; }
#endif
        StartAirPlay();
    }
}
