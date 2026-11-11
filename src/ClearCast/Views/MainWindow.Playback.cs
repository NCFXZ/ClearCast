using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ClearCast;

public sealed partial class MainWindow
{
    private PlaybackSeekBar? _playbackSeekSlider;
    private ReceiverMediaState? _playbackSeekState;
    private bool _playbackControlPending;
    private string _playbackControlError = "";

    private void InitializePlaybackControls()
    {
        foreach (PlaybackSeekBar slider in new[] { MusicSeekSlider, VideoSeekSlider })
        {
            slider.SeekStarted += (_, _) => BeginPlaybackSeek(slider);
            slider.SeekCompleted += async (_, _) => await CommitPlaybackSeekAsync();
            slider.SeekCanceled += (_, _) => CancelPlaybackSeek();
        }
    }

    private void CancelPlaybackSeek()
    {
        _playbackSeekSlider?.CancelDrag();
        _playbackSeekSlider = null;
        _playbackSeekState = null;
    }

    private void UpdatePlaybackControls()
    {
        bool ready = _receiver is not null && !_closing && !_closed && _stopTask is null &&
            !_resettingPairings && !_startingReceiver && !_playbackControlPending && !_mediaCommandPending;
        bool direct = _hasVideo && _mediaState.IsDirectVideo;
        bool transport = _hasVideo && _mediaState.CanControlVideo;
        VideoTransport.Visibility = transport ? Visibility.Visible : Visibility.Collapsed;
        VideoPlaybackActions.Visibility = transport ? Visibility.Visible : Visibility.Collapsed;
        VideoPlayPauseIcon.Symbol = _mediaState.Playing ? Symbol.Pause : Symbol.Play;
        string label = _mediaState.Playing ? Texts.MusicPause : Texts.MusicPlay;
        AutomationProperties.SetName(VideoPlayPauseButton, label);
        ToolTipService.SetToolTip(VideoPlayPauseButton, label);
        VideoPlayPauseButton.IsEnabled = ready && transport && _mediaState.Controls.HasFlag(MediaControls.PlayPause);
        VideoSkipBackwardButton.IsEnabled = ready && transport && _mediaState.CanSeek && _mediaState.PositionMs > _mediaState.SeekStartMs;
        VideoSkipForwardButton.IsEnabled = ready && transport && _mediaState.CanSeek && _mediaState.PositionMs < _mediaState.SeekEndMs;
        if (direct && !_mediaState.Playing) ShowPlayerOverlay();
        bool musicSeek = _hasMusic && _mediaState.CanSeek;
        MusicProgress.Visibility = musicSeek ? Visibility.Collapsed : Visibility.Visible;
        MusicSeekSlider.Visibility = musicSeek ? Visibility.Visible : Visibility.Collapsed;
        VideoSeekSlider.IsEnabled = ready && transport && _mediaState.CanSeek;
        VideoSeekSlider.Visibility = transport ? Visibility.Visible : Visibility.Collapsed;
        MusicSeekSlider.IsEnabled = ready && musicSeek;
        foreach (PlaybackSeekBar slider in new[] { MusicSeekSlider, VideoSeekSlider })
        {
            if (ReferenceEquals(slider, _playbackSeekSlider)) continue;
            // Keep the UI range stable while duration and live seek windows change.
            slider.Value = ReferenceEquals(slider, VideoSeekSlider) && !_mediaState.CanSeek && _mediaState.DurationMs > 0
                ? Math.Clamp((double)_mediaState.PositionMs / _mediaState.DurationMs * 1000, 0, 1000)
                : _mediaState.SeekSliderValue(_mediaState.PositionMs);
        }
        VideoElapsedText.Text = FormatMusicTime(_mediaState.PositionMs);
        VideoRemainingText.Text = "−" + FormatMusicTime(Math.Max(0, _mediaState.DurationMs - _mediaState.PositionMs));
        VideoControlHint.Text = _playbackControlError.Length > 0 ? Texts.MusicControlFailed : "";
        VideoControlHint.Visibility = direct && _playbackControlError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BeginPlaybackSeek(object sender)
    {
        if (_playbackSeekSlider is null && sender is PlaybackSeekBar { IsEnabled: true } slider && _mediaState.CanSeek)
        {
            _playbackSeekSlider = slider;
            _playbackSeekState = _mediaState;
        }
    }
    private async Task CommitPlaybackSeekAsync()
    {
        PlaybackSeekBar? slider = _playbackSeekSlider;
        ReceiverMediaState? state = _playbackSeekState;
        CancelPlaybackSeek();
        if (slider is null || state is null || state.ControlSession != _mediaState.ControlSession ||
            state.ContentRevision != _mediaState.ContentRevision) return;
        await SendPlaybackControlAsync(state, MediaCommand.Seek, state.SeekPosition(slider.Value));
    }
    private async void VideoPlayPauseClicked(object sender, RoutedEventArgs args) => await ToggleCurrentPlaybackAsync();

    private async void VideoSkipBackwardClicked(object sender, RoutedEventArgs args) => await SkipVideoAsync(-10_000);
    private async void VideoSkipForwardClicked(object sender, RoutedEventArgs args) => await SkipVideoAsync(10_000);

    private Task SkipVideoAsync(long offset) => _hasVideo && _mediaState.CanControlVideo && _mediaState.CanSeek
        ? SendPlaybackControlAsync(_mediaState, MediaCommand.Skip, offset) : Task.CompletedTask;

    private bool CanToggleCurrentPlayback => IsMirrorPageSelected &&
        (_hasVideo ? VideoPlayPauseButton.IsEnabled : _hasMusic && MusicPlayPauseButton.IsEnabled);

    private Task ToggleCurrentPlaybackAsync() => !CanToggleCurrentPlayback ? Task.CompletedTask : _hasVideo
        ? SendPlaybackControlAsync(_mediaState, MediaCommand.PlayPause) : SendMusicCommandAsync(MediaCommand.PlayPause);

    private bool ShouldHandlePlaybackSpace => IsMirrorPageSelected && (_hasVideo || _hasMusic);

    private async void PlaybackPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        // Video full screen handles Escape in its native HWND; music full screen is all XAML.
        if (args.Key == VirtualKey.Escape && _playerFullScreen && !_hasVideo)
        {
            args.Handled = true;
            SetPlayerFullScreen(false);
            return;
        }
        if (args.Key != VirtualKey.Space || !ShouldHandlePlaybackSpace) return;
        args.Handled = true;
        if (!args.KeyStatus.WasKeyDown && CanToggleCurrentPlayback) await ToggleCurrentPlaybackAsync();
    }

    private void PlaybackPreviewKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Space && ShouldHandlePlaybackSpace) args.Handled = true;
    }

    private async Task SendPlaybackControlAsync(ReceiverMediaState state, MediaCommand command, long value = 0)
    {
        if (_receiver is not { } receiver || _playbackControlPending || _mediaCommandPending || _closing || _stopTask is not null) return;
        _playbackControlPending = true;
        _playbackControlError = "";
        UpdatePlaybackControls();
        UpdateMusicControls();
        try { await receiver.ControlMediaAsync(state, command, value); }
        catch (Win32Exception)
        {
            if (ReferenceEquals(receiver, _receiver) && state.ControlSession == _mediaState.ControlSession &&
                state.ContentRevision == _mediaState.ContentRevision)
            {
                _playbackControlError = Texts.MusicControlFailed;
                if (state.Kind == ReceiverMediaKind.Audio) _mediaControlError = _playbackControlError;
            }
        }
        finally
        {
            _playbackControlPending = false;
            if (!_closed) { UpdatePlaybackControls(); UpdateMusicControls(); }
        }
    }
}
