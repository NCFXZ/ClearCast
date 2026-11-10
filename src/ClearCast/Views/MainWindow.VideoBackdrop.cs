using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace ClearCast;

public sealed partial class MainWindow
{
    private byte[]? _videoBackdropPixels;
    private int _videoBackdropGeneration;
    private bool _videoBackdropPending;
    private long _videoBackdropRequestedAt;

    private void ResetVideoBackdrop()
    {
        ++_videoBackdropGeneration;
        _videoBackdropPixels = null;
        _videoBackdropRequestedAt = 0;
    }

    private async void PollVideoBackdrop()
    {
        if (!_hasVideo || _playerFullScreen || !MusicBackground.IsActive || _videoBackdropPending ||
            _receiver is not { } receiver || _stopTask is not null || _closed || _closing) return;
        const int interval = 33;
        if (_videoBackdropRequestedAt != 0 && Stopwatch.GetElapsedTime(_videoBackdropRequestedAt).TotalMilliseconds < interval) return;
        _videoBackdropPending = true;
        _videoBackdropRequestedAt = Stopwatch.GetTimestamp();
        int generation = _videoBackdropGeneration;
        try
        {
            var frame = await Task.Run(receiver.ReadVideoFrame);
            if (frame is null || generation != _videoBackdropGeneration || !ReferenceEquals(receiver, _receiver) ||
                !_hasVideo || _playerFullScreen || _closed || _closing) return;
            if (!MusicBackground.IsAnimating && _videoBackdropPixels is { } previous && previous.AsSpan().SequenceEqual(frame.Pixels)) return;
            _videoBackdropPixels = frame.Pixels;
            await MusicBackground.SetVideoFrameAsync(frame.Pixels, WindowLayout.ActualTheme == ElementTheme.Dark,
                _motionSettings.AnimationsEnabled);
            if (!_closed) UpdateMusicBackgroundState();
        }
        finally { _videoBackdropPending = false; }
    }
}
