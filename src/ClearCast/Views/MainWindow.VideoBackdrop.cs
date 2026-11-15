using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClearCast;

public sealed partial class MainWindow
{
    private byte[]? _videoBackdropPixels;
    private byte[]? _windowedVideoBackdropPixels;
    private int _videoBackdropGeneration;
    private bool _videoBackdropPending;
    private long _videoBackdropRequestedAt;
    private readonly AdaptiveWorkCadence _videoBackdropCadence = new(33, 264);
    private readonly VideoFrost _playerControlsFrost = new();
    private readonly AdaptiveWorkCadence _playerControlsFrostCadence = new(33, 264);
    private WriteableBitmap? _playerControlsFrostBitmap;
    private readonly record struct PlayerFrostState(byte[] Pixels, RectangleF Picture, RectangleF Controls, int Rotation, ElementTheme Theme);
    private sealed record PlayerFrostRequest(PlayerFrostState State, int Generation, int BackdropGeneration);
    private PlayerFrostState? _requestedPlayerControlsFrost, _appliedPlayerControlsFrost;
    private PlayerFrostRequest? _pendingPlayerControlsFrost;
    private CancellationTokenSource? _playerControlsFrostCancellation;
    private Task _playerControlsFrostTask = Task.CompletedTask;
    private bool _playerControlsFrostRunning;
    private int _playerControlsFrostGeneration;
    private long _playerControlsFrostStartedAt;
    private bool _playerControlsFrostFailed;
#if CLEARCAST_UI_TESTS
    private Func<CancellationToken, Task>? _uiBeforeFrostRenderAsync;
    private Func<CancellationToken, Task>? _uiAfterFrostRenderAsync;
    private int _uiFrostWorkActive, _uiMaxFrostWorkActive, _uiFrostWorkStarts, _uiFrostWorkThread;
#endif

    private bool PlayerControlsFrostWanted => _hasVideo && _playerFullScreen && _playerOverlayTargetVisible &&
        IsMirrorPageSelected && !_closing && !_closed && _stopTask is null && !_playerControlsFrostFailed &&
        _captionThemeSettings?.HighContrast != true && _motionSettings.AdvancedEffectsEnabled &&
        AppWindow.IsVisible && WindowLayout.XamlRoot?.IsHostVisible == true &&
        AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    private void ResetVideoBackdrop()
    {
        ++_videoBackdropGeneration;
        _videoBackdropPixels = null;
        _windowedVideoBackdropPixels = null;
        _videoBackdropRequestedAt = 0;
        _videoBackdropCadence.Reset();
        _playerControlsFrostCadence.Reset();
        _playerControlsFrostStartedAt = 0;
        _playerControlsFrostBitmap = null;
        _playerControlsFrostFailed = false;
        ClearPlayerControlsFrost();
    }

    private void CancelVideoBackdropWork()
    {
        ++_videoBackdropGeneration;
        ClearPlayerControlsFrost();
    }

    private void ClearPlayerControlsFrost()
    {
        ++_playerControlsFrostGeneration;
        _pendingPlayerControlsFrost = null;
        _requestedPlayerControlsFrost = null;
        _playerControlsFrostCancellation?.Cancel();
        _appliedPlayerControlsFrost = null;
        if (_closed) return;
        PlayerControlsFrost.Visibility = Visibility.Collapsed;
        PlayerControlsFrostBrush.ImageSource = null;
    }

    private PlayerFrostState? GetPlayerControlsFrostState()
    {
        if (!PlayerControlsFrostWanted || _videoBackdropPixels is not { } pixels ||
            VideoHostSlot.XamlRoot is not { } root || GetVideoSlotBounds(root.RasterizationScale) is not { } slot ||
            GetPlayerOverlayBounds(root.RasterizationScale) is not { } controls)
        {
            return null;
        }

        var layout = GetPlayerVideoLayout(slot);
        // A native-size picture may be clipped; map the complete source frame before clipping.
        var picture = _preferences.OriginalSize ? layout.Host : layout.Picture;
        double scale = root.RasterizationScale;
        return new PlayerFrostState(pixels, new RectangleF((float)(picture.X / scale), (float)(picture.Y / scale),
                (float)(picture.Width / scale), (float)(picture.Height / scale)),
            new RectangleF((float)(controls.X / scale), (float)(controls.Y / scale),
                (float)(controls.Width / scale), (float)(controls.Height / scale)), _playerRotation, WindowLayout.ActualTheme);
    }

    private static bool SameFrostGeometry(PlayerFrostState first, PlayerFrostState second) =>
        first.Picture == second.Picture && first.Controls == second.Controls && first.Rotation == second.Rotation && first.Theme == second.Theme;

    private void UpdatePlayerControlsFrost()
    {
        if (GetPlayerControlsFrostState() is not { } state) { ClearPlayerControlsFrost(); return; }
        if (_requestedPlayerControlsFrost == state) return;
        // New pixels keep the active bounded render useful. A changed layout or
        // theme cancels it; a single newest descriptor replaces every queued one.
        if (_requestedPlayerControlsFrost is { } previous && !SameFrostGeometry(previous, state))
            _playerControlsFrostCancellation?.Cancel();
        _requestedPlayerControlsFrost = state;
        _pendingPlayerControlsFrost = new(state, _playerControlsFrostGeneration, _videoBackdropGeneration);
        if (!_playerControlsFrostRunning) _playerControlsFrostTask = RenderPendingPlayerControlsFrostAsync();
    }

    private bool IsCurrentPlayerControlsFrost(PlayerFrostRequest request) =>
        request.Generation == _playerControlsFrostGeneration && request.BackdropGeneration == _videoBackdropGeneration &&
        GetPlayerControlsFrostState() is { } current && SameFrostGeometry(current, request.State);

    private Task<byte[]> RenderPlayerControlsFrostAsync(PlayerFrostState state, CancellationToken cancellation)
    {
#if CLEARCAST_UI_TESTS
        return Task.Run(async () =>
        {
            int active = Interlocked.Increment(ref _uiFrostWorkActive);
            _uiMaxFrostWorkActive = Math.Max(_uiMaxFrostWorkActive, active);
            Interlocked.Increment(ref _uiFrostWorkStarts);
            _uiFrostWorkThread = Environment.CurrentManagedThreadId;
            try
            {
                if (_uiBeforeFrostRenderAsync is { } hook) await hook(cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                byte[] pixels = _playerControlsFrost.Render(state.Pixels, state.Picture, state.Controls, state.Rotation, cancellation);
                if (_uiAfterFrostRenderAsync is { } after) await after(cancellation).ConfigureAwait(false);
                return pixels;
            }
            finally { Interlocked.Decrement(ref _uiFrostWorkActive); }
        }, cancellation);
#else
        return Task.Run(() => _playerControlsFrost.Render(state.Pixels, state.Picture, state.Controls, state.Rotation, cancellation), cancellation);
#endif
    }

    private async Task RenderPendingPlayerControlsFrostAsync()
    {
        _playerControlsFrostRunning = true;
        try
        {
            while (_pendingPlayerControlsFrost is { } request)
            {
                _pendingPlayerControlsFrost = null;
                if (!IsCurrentPlayerControlsFrost(request)) continue;
                using var cancellation = new CancellationTokenSource();
                _playerControlsFrostCancellation = cancellation;
                try
                {
                    // The normal tier adds no delay to existing layout refreshes.
                    // Only sustained measured pressure throttles auxiliary work.
                    if (_playerControlsFrostCadence.IsThrottled && _playerControlsFrostStartedAt != 0)
                    {
                        double remaining = _playerControlsFrostCadence.IntervalMilliseconds - Stopwatch.GetElapsedTime(_playerControlsFrostStartedAt).TotalMilliseconds;
                        if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), cancellation.Token);
                    }
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (_pendingPlayerControlsFrost is { } latest)
                    {
                        request = latest;
                        _pendingPlayerControlsFrost = null;
                    }
                    if (!IsCurrentPlayerControlsFrost(request)) continue;
                    _playerControlsFrostStartedAt = Stopwatch.GetTimestamp();
                    byte[] frosted = await RenderPlayerControlsFrostAsync(request.State, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    _playerControlsFrostCadence.Observe(Stopwatch.GetElapsedTime(_playerControlsFrostStartedAt));
                    if (!IsCurrentPlayerControlsFrost(request)) continue;
                    if (_appliedPlayerControlsFrost == request.State) continue;
                    // Only WinUI publication stays on the UI thread. Copy before
                    // admitting another worker because the renderer reuses output.
                    _playerControlsFrostBitmap ??= new WriteableBitmap(VideoFrost.Width, VideoFrost.Height);
                    using (var stream = _playerControlsFrostBitmap.PixelBuffer.AsStream()) stream.Write(frosted);
                    _playerControlsFrostBitmap.Invalidate();
                    PlayerControlsFrostBrush.ImageSource = _playerControlsFrostBitmap;
                    PlayerControlsFrost.Visibility = Visibility.Visible;
                    _appliedPlayerControlsFrost = request.State;
                }
                catch (OperationCanceledException) { }
                finally
                {
                    if (ReferenceEquals(_playerControlsFrostCancellation, cancellation)) _playerControlsFrostCancellation = null;
                }
            }
        }
        catch (Exception error) when (error is COMException or IOException or ObjectDisposedException)
        {
            _playerControlsFrostFailed = true;
            ClearPlayerControlsFrost();
            App.WriteUiError("Player controls frost disabled for this video: " + error);
        }
        finally { _playerControlsFrostRunning = false; }
    }

    private async void PollVideoBackdrop()
    {
        if (!_hasVideo || !(_playerFullScreen ? PlayerControlsFrostWanted : MusicBackground.IsActive) ||
            _videoBackdropPending || _receiver is not { } receiver || _stopTask is not null || _closed || _closing) return;
        int interval = _playerFullScreen ? 100 * _playerControlsFrostCadence.Multiplier : _videoBackdropCadence.IntervalMilliseconds;
        if (_videoBackdropRequestedAt != 0 && Stopwatch.GetElapsedTime(_videoBackdropRequestedAt).TotalMilliseconds < interval) return;
        _videoBackdropPending = true;
        _videoBackdropRequestedAt = Stopwatch.GetTimestamp();
        int generation = _videoBackdropGeneration;
        bool measuredWindowed = false, transitionDelay = false;
        try
        {
            var frame = await Task.Run(receiver.ReadVideoFrame);
            if (frame is null || generation != _videoBackdropGeneration || !ReferenceEquals(receiver, _receiver) ||
                !_hasVideo || _closed || _closing || _stopTask is not null) return;
            if (_playerFullScreen)
            {
                if (!PlayerControlsFrostWanted) return;
                if (_videoBackdropPixels is not { } previous || !previous.AsSpan().SequenceEqual(frame.Pixels))
                    _videoBackdropPixels = frame.Pixels;
                UpdatePlayerControlsFrost();
            }
            else
            {
                measuredWindowed = true;
                if (!MusicBackground.IsActive || !MusicBackground.IsAnimating &&
                    _windowedVideoBackdropPixels is { } previous && previous.AsSpan().SequenceEqual(frame.Pixels)) return;
                _videoBackdropPixels = frame.Pixels;
                _windowedVideoBackdropPixels = frame.Pixels;
                transitionDelay = _motionSettings.AnimationsEnabled;
                await MusicBackground.SetVideoFrameAsync(frame.Pixels, WindowLayout.ActualTheme == ElementTheme.Dark, transitionDelay);
            }
            if (!_closed) UpdateMusicBackgroundState();
        }
        finally
        {
            if (measuredWindowed && generation == _videoBackdropGeneration && !_closed && !_closing)
            {
                double work = Stopwatch.GetElapsedTime(_videoBackdropRequestedAt).TotalMilliseconds -
                    (transitionDelay ? MusicBackdrop.VideoTransitionMilliseconds : 0);
                _videoBackdropCadence.Observe(TimeSpan.FromMilliseconds(Math.Max(0, work)));
            }
            _videoBackdropPending = false;
        }
    }
}
