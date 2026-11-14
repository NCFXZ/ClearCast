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
    private readonly VideoFrost _playerControlsFrost = new();
    private WriteableBitmap? _playerControlsFrostBitmap;
    private (byte[] Pixels, RectangleF Picture, RectangleF Controls, int Rotation)? _appliedPlayerControlsFrost;
    private bool _playerControlsFrostFailed;

    private bool PlayerControlsFrostWanted => _hasVideo && _playerFullScreen && _playerOverlayTargetVisible &&
        IsMirrorPageSelected && !_closing && !_closed && !_playerControlsFrostFailed &&
        _captionThemeSettings?.HighContrast != true && _motionSettings.AdvancedEffectsEnabled &&
        AppWindow.IsVisible && WindowLayout.XamlRoot?.IsHostVisible == true &&
        AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    private void ResetVideoBackdrop()
    {
        ++_videoBackdropGeneration;
        _videoBackdropPixels = null;
        _windowedVideoBackdropPixels = null;
        _videoBackdropRequestedAt = 0;
        _playerControlsFrostBitmap = null;
        _playerControlsFrostFailed = false;
        ClearPlayerControlsFrost();
    }

    private void ClearPlayerControlsFrost()
    {
        _appliedPlayerControlsFrost = null;
        if (_closed) return;
        PlayerControlsFrost.Visibility = Visibility.Collapsed;
        PlayerControlsFrostBrush.ImageSource = null;
    }

    private void UpdatePlayerControlsFrost()
    {
        if (!PlayerControlsFrostWanted || _videoBackdropPixels is not { } pixels ||
            VideoHostSlot.XamlRoot is not { } root || GetVideoSlotBounds(root.RasterizationScale) is not { } slot ||
            GetPlayerOverlayBounds(root.RasterizationScale) is not { } controls)
        {
            ClearPlayerControlsFrost();
            return;
        }

        var layout = GetPlayerVideoLayout(slot);
        // A native-size picture may be clipped; map the complete source frame before clipping.
        var picture = _preferences.OriginalSize ? layout.Host : layout.Picture;
        double scale = root.RasterizationScale;
        var state = (Pixels: pixels, Picture: new RectangleF((float)(picture.X / scale), (float)(picture.Y / scale),
                (float)(picture.Width / scale), (float)(picture.Height / scale)),
            Controls: new RectangleF((float)(controls.X / scale), (float)(controls.Y / scale),
                (float)(controls.Width / scale), (float)(controls.Height / scale)), Rotation: _playerRotation);
        if (_appliedPlayerControlsFrost == state)
            return;
        try
        {
            byte[] frosted = _playerControlsFrost.Render(pixels, state.Picture, state.Controls, _playerRotation);
            _playerControlsFrostBitmap ??= new WriteableBitmap(VideoFrost.Width, VideoFrost.Height);
            using (var stream = _playerControlsFrostBitmap.PixelBuffer.AsStream())
                stream.Write(frosted);
            _playerControlsFrostBitmap.Invalidate();
            PlayerControlsFrostBrush.ImageSource = _playerControlsFrostBitmap;
            PlayerControlsFrost.Visibility = Visibility.Visible;
            _appliedPlayerControlsFrost = state;
        }
        catch (Exception error) when (error is COMException or IOException or ObjectDisposedException)
        {
            _playerControlsFrostFailed = true;
            ClearPlayerControlsFrost();
            App.WriteUiError("Player controls frost disabled for this video: " + error);
        }
    }

    private async void PollVideoBackdrop()
    {
        if (!_hasVideo || !(_playerFullScreen ? PlayerControlsFrostWanted : MusicBackground.IsActive) ||
            _videoBackdropPending || _receiver is not { } receiver || _stopTask is not null || _closed || _closing) return;
        int interval = _playerFullScreen ? 100 : 33;
        if (_videoBackdropRequestedAt != 0 && Stopwatch.GetElapsedTime(_videoBackdropRequestedAt).TotalMilliseconds < interval) return;
        _videoBackdropPending = true;
        _videoBackdropRequestedAt = Stopwatch.GetTimestamp();
        int generation = _videoBackdropGeneration;
        try
        {
            var frame = await Task.Run(receiver.ReadVideoFrame);
            if (frame is null || generation != _videoBackdropGeneration || !ReferenceEquals(receiver, _receiver) ||
                !_hasVideo || _closed || _closing) return;
            if (_playerFullScreen)
            {
                if (!PlayerControlsFrostWanted) return;
                if (_videoBackdropPixels is not { } previous || !previous.AsSpan().SequenceEqual(frame.Pixels))
                    _videoBackdropPixels = frame.Pixels;
                UpdatePlayerControlsFrost();
            }
            else
            {
                if (!MusicBackground.IsActive || !MusicBackground.IsAnimating &&
                    _windowedVideoBackdropPixels is { } previous && previous.AsSpan().SequenceEqual(frame.Pixels)) return;
                _videoBackdropPixels = frame.Pixels;
                _windowedVideoBackdropPixels = frame.Pixels;
                await MusicBackground.SetVideoFrameAsync(frame.Pixels, WindowLayout.ActualTheme == ElementTheme.Dark,
                    _motionSettings.AnimationsEnabled);
            }
            if (!_closed) UpdateMusicBackgroundState();
        }
        finally { _videoBackdropPending = false; }
    }
}
