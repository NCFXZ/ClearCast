using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.UI.ViewManagement;
using Windows.Storage.Streams;

namespace ClearCast;

public sealed partial class MainWindow
{
    private ReceiverMediaState _mediaState = ReceiverMediaState.Empty;
    private long _mediaSnapshotAt;
    private bool _hasMusic;
    private bool _mediaCommandPending;
    private string _mediaControlError = "";
    private ulong _artworkRevision;
    private int _artworkGeneration;
    private int _mediaCommandGeneration;
    private byte[]? _musicArtworkBytes;
#if CLEARCAST_UI_TESTS
    private long _uiMusicActivityReads;
#endif

    private void MusicViewportChanged(object sender, SizeChangedEventArgs args) => UpdateMusicLayout();
    private void MusicContentSizeChanged(object sender, SizeChangedEventArgs args) => UpdateMusicLayout();

    private void UpdateMusicLayout()
    {
        double width = MusicPanel.ViewportWidth;
        double height = MusicPanel.ViewportHeight;
        if (width <= 0 || height <= 0)
            return;
        MusicLayout.MinHeight = height;
        MusicContent.Width = Math.Max(0, Math.Min(560, width - 40));
        double otherHeight = MusicContent.ActualHeight > MusicArtworkFrame.ActualHeight && MusicArtworkFrame.ActualHeight > 0
            ? MusicContent.ActualHeight - MusicArtworkFrame.ActualHeight + MusicContent.Margin.Top + MusicContent.Margin.Bottom
            : 320;
        double artworkSize = Math.Max(144, Math.Min(480, Math.Min(width - 64, height - Math.Max(240, otherHeight))));
        MusicArtworkFrame.Width = MusicArtworkFrame.Height = artworkSize;
    }

    private void UpdateMusicBackgroundState()
    {
        UpdateMusicAudioActivity();
        bool active = !_closed && !_closing && (_hasMusic && !_hasVideo || _hasVideo && !_playerFullScreen) &&
            _captionThemeSettings?.HighContrast != true && AppWindow.IsVisible &&
            WindowLayout.XamlRoot?.IsHostVisible == true &&
            AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        MusicBackground.SetActive(active, active && _mediaState.Playing && _motionSettings.AnimationsEnabled);
        if (_videoBackdropTimer is { } videoTimer)
        {
            bool fastVideo = active && _hasVideo && _receiver is not null && _stopTask is null;
            if (fastVideo && !videoTimer.IsRunning) videoTimer.Start();
            else if (!fastVideo) videoTimer.Stop();
        }
        if (_musicActivityTimer is { } timer)
        {
            bool fast = _hasMusic && !_hasVideo && MusicBackground.IsAnimating && _receiver is not null && _stopTask is null;
            if (fast && !timer.IsRunning) timer.Start();
            else if (!fast) timer.Stop();
        }
    }

    private void UpdateMusicAudioActivity()
    {
        // The fast path owns audio-only updates while it is running. Metadata
        // snapshots must not re-feed an older history between its reads.
        if (_musicActivityTimer?.IsRunning == true) return;
        TimeSpan elapsed = _mediaSnapshotAt == 0 ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(_mediaSnapshotAt);
        ApplyMusicAudioActivity(_mediaState.AudioActivity, elapsed, _hasMusic && !_hasVideo && _mediaState.Playing);
    }

    private void PollMusicAudioActivity()
    {
        if (_closed || _closing || _stopTask is not null || _receiver is not { } receiver ||
            !MusicBackground.IsAnimating)
        {
            _musicActivityTimer?.Stop();
            return;
        }
        if (receiver.TryReadMedia(out var state))
        {
#if CLEARCAST_UI_TESTS
            _uiMusicActivityReads++;
#endif
            bool sameTrack = state.Kind == ReceiverMediaKind.Audio && state.Playing && state.Session == _mediaState.Session &&
                state.Title == _mediaState.Title && state.Artist == _mediaState.Artist && state.Album == _mediaState.Album;
            ApplyMusicAudioActivity(state.AudioActivity, TimeSpan.Zero, sameTrack);
        }
        else
            ApplyMusicAudioActivity(default, TimeSpan.Zero, false);
    }

    private void ApplyMusicAudioActivity(ReceiverAudioActivity activity, TimeSpan elapsed, bool allowed)
    {
        bool fresh = allowed && activity.IsFresh(elapsed);
        MusicBackground.SetAudioActivity(fresh ? activity.Energy : 0, fresh ? activity.Density : 0, fresh);
        var beat = activity.Beats is { Length: > 0 } history ? history[^1] : default;
        MusicBackground.SetBeatActivity(activity.BeatEpoch, beat.Sequence, beat.StrengthAt(elapsed), fresh && beat.IsFresh(elapsed));
    }

    private async void RefreshMusicBackgroundTheme()
    {
        if (_hasVideo && _videoBackdropPixels is { } pixels)
            await MusicBackground.SetVideoFrameAsync(pixels, WindowLayout.ActualTheme == ElementTheme.Dark, _motionSettings.AnimationsEnabled);
        else
            await MusicBackground.SetArtworkAsync(_hasVideo ? null : _musicArtworkBytes, WindowLayout.ActualTheme == ElementTheme.Dark);
        UpdateMusicBackgroundState();
    }

    private void MusicMotionChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateMusicBackgroundState);

    private void ApplyMediaState(ReceiverMediaState state)
    {
        bool wasMusic = _hasMusic;
        bool sourceChanged = state.Kind != _mediaState.Kind || state.Session != _mediaState.Session ||
            state.ControlSession != _mediaState.ControlSession || state.ContentRevision != _mediaState.ContentRevision;
        bool trackChanged = wasMusic && state.Kind == ReceiverMediaKind.Audio &&
            (state.Title != _mediaState.Title || state.Artist != _mediaState.Artist || state.Album != _mediaState.Album);
        long expectedPosition = _mediaState.PositionAt(_mediaSnapshotAt == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_mediaSnapshotAt));
        bool seeked = wasMusic && state.Kind == ReceiverMediaKind.Audio &&
            (state.PositionMs > expectedPosition + 5_000 || state.PositionMs < expectedPosition - 5_000);
        if (sourceChanged || trackChanged || seeked)
            MusicBackground.ResetAudioActivity();
        if (sourceChanged)
        {
            if (_hasVideo || state.Kind == ReceiverMediaKind.Video) ResetVideoBackdrop();
            ++_mediaCommandGeneration;
            _mediaCommandPending = false;
            _mediaControlError = "";
            _playbackControlError = "";
        }
        bool changed = state.Revision != _mediaState.Revision || state.Kind != _mediaState.Kind || state.Session != _mediaState.Session ||
            state.Playing != _mediaState.Playing || state.Controls != _mediaState.Controls ||
            state.Title != _mediaState.Title || state.Artist != _mediaState.Artist || state.Album != _mediaState.Album;
        _mediaState = state;
        if (sourceChanged) CancelPlaybackSeek();
        _mediaSnapshotAt = Stopwatch.GetTimestamp();
        _hasMusic = state.Kind == ReceiverMediaKind.Audio;
        if (changed)
            _mediaControlError = "";

        MusicPanel.Visibility = _hasMusic && !_hasVideo ? Visibility.Visible : Visibility.Collapsed;
        VideoPlaceholder.Visibility = _hasMusic || _hasVideo ? Visibility.Collapsed : Visibility.Visible;
        if (_hasMusic && !wasMusic)
        {
            ReceiverInfoBar.IsOpen = false;
            HidePairingCode();
        }
        if (wasMusic != _hasMusic)
        {
            UpdateLandingAnimationState();
            UpdateReceiverButtons();
        }
        if (changed)
            RefreshMusicText();

        ulong revision = _hasMusic && state.ArtworkBytes > 0 ? state.ArtworkRevision : 0;
        if (_artworkRevision != revision)
        {
            _artworkRevision = revision;
            int generation = ++_artworkGeneration;
            MusicArtwork.Source = null;
            _musicArtworkBytes = null;
            if (!_hasVideo) _ = MusicBackground.SetArtworkAsync(null, WindowLayout.ActualTheme == ElementTheme.Dark);
            MusicArtworkPlaceholder.Visibility = Visibility.Visible;
            if (revision != 0 && _receiver is { } receiver)
                _ = LoadMusicArtworkAsync(receiver, revision, state.ArtworkBytes, generation);
        }
        if (wasMusic && state.Kind == ReceiverMediaKind.None && !_hasVideo && _receiverReady)
            WaitForMirror();
        UpdateMusicLayout();
        UpdateMusicBackgroundState();
        RefreshIdleConnectionStatus();
        UpdatePlaybackControls();
    }

    private void RefreshMusicText()
    {
        MusicTitle.Text = _mediaState.Title.Length > 0 ? _mediaState.Title : Texts.MusicUnknownTitle;
        MusicArtist.Text = _mediaState.Artist.Length > 0 ? _mediaState.Artist : Texts.MusicUnknownArtist;
        MusicAlbum.Text = _mediaState.Album;
        MusicAlbum.Visibility = _mediaState.Album.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_hasMusic && !_hasVideo)
            SetReceiverStatus(_mediaState.Playing ? Texts.MusicPlaying : Texts.MusicPaused);
        UpdateMusicControls();
        UpdateMusicProgress();
    }

    private void UpdateMusicControls()
    {
        bool ready = _hasMusic && _receiver is not null && !_mediaCommandPending &&
            !_resettingPairings && !_startingReceiver && !_closing && _stopTask is null;
        bool hasControls = _hasMusic && (_mediaState.Controls & ~MediaControls.Seek) != MediaControls.None;
        MusicTransportControls.Visibility = hasControls ? Visibility.Visible : Visibility.Collapsed;
        MusicPlayPauseButton.Visibility = _mediaState.Controls.HasFlag(MediaControls.PlayPause) ? Visibility.Visible : Visibility.Collapsed;
        MusicPreviousButton.Visibility = _mediaState.Controls.HasFlag(MediaControls.Previous) ? Visibility.Visible : Visibility.Collapsed;
        MusicNextButton.Visibility = _mediaState.Controls.HasFlag(MediaControls.Next) ? Visibility.Visible : Visibility.Collapsed;
        MusicPlayPauseButton.IsEnabled = ready && _mediaState.Controls.HasFlag(MediaControls.PlayPause);
        MusicPreviousButton.IsEnabled = ready && _mediaState.Controls.HasFlag(MediaControls.Previous);
        MusicNextButton.IsEnabled = ready && _mediaState.Controls.HasFlag(MediaControls.Next);
        MusicDisconnectButton.IsEnabled = _hasMusic && _receiver is not null && !_closing && _stopTask is null;
        string label = _mediaState.Playing ? Texts.MusicPause : Texts.MusicPlay;
        MusicPlayPauseIcon.Symbol = _mediaState.Playing ? Symbol.Pause : Symbol.Play;
        AutomationProperties.SetName(MusicPlayPauseButton, label);
        ToolTipService.SetToolTip(MusicPlayPauseButton, label);
        MusicControlHint.Text = hasControls && _mediaControlError.Length > 0 ? Texts.MusicControlFailed : "";
        MusicControlHint.Visibility = MusicControlHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMusicProgress()
    {
        if (!_hasMusic)
            return;
        long position = _mediaState.PositionAt(_mediaSnapshotAt == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_mediaSnapshotAt));
        MusicProgress.IsIndeterminate = _mediaState.DurationMs == 0 && _mediaState.Playing;
        MusicProgress.Maximum = Math.Max(1, _mediaState.DurationMs);
        MusicProgress.Value = _mediaState.DurationMs > 0 ? Math.Min(position, _mediaState.DurationMs) : 0;
        MusicElapsed.Text = FormatMusicTime(position);
        MusicDuration.Text = _mediaState.DurationMs > 0 ? FormatMusicTime(_mediaState.DurationMs) : "—:—";
        UpdatePlaybackControls();
    }

    private static string FormatMusicTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1
            ? $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(long)time.TotalMinutes}:{time.Seconds:00}";
    }

    private async Task LoadMusicArtworkAsync(AirPlayReceiver receiver, ulong revision, int length, int generation)
    {
        try
        {
            byte[]? bytes = await Task.Run(() => receiver.ReadArtwork(revision, length));
            if (bytes is null || generation != _artworkGeneration || _closed || !_hasMusic)
                return;
            await ApplyMusicArtworkAsync(bytes, generation, receiver);
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException or IOException)
        {
            // Invalid or superseded sender artwork leaves the local placeholder.
        }
    }

    private async Task ApplyMusicArtworkAsync(byte[] bytes, int generation, AirPlayReceiver? expectedReceiver)
    {
        if (bytes.Length is <= 0 or > 2 * 1024 * 1024 || generation != _artworkGeneration || !_hasMusic || _closed)
            return;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var image = new BitmapImage { DecodePixelWidth = 640 };
            await image.SetSourceAsync(stream);
            if (generation == _artworkGeneration && !_closed && _hasMusic && ReferenceEquals(expectedReceiver, _receiver))
            {
                MusicArtwork.Source = image;
                MusicArtworkPlaceholder.Visibility = Visibility.Collapsed;
                _musicArtworkBytes = bytes;
                if (!_hasVideo) await MusicBackground.SetArtworkAsync(bytes, WindowLayout.ActualTheme == ElementTheme.Dark);
                UpdateMusicBackgroundState();
            }
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException or IOException)
        {
            // Invalid or superseded sender artwork leaves the local placeholder.
        }
    }

    private async void MusicPlayPauseClicked(object sender, RoutedEventArgs args) => await ToggleCurrentPlaybackAsync();
    private async void MusicPreviousClicked(object sender, RoutedEventArgs args) => await SendMusicCommandAsync(MediaCommand.Previous);
    private async void MusicNextClicked(object sender, RoutedEventArgs args) => await SendMusicCommandAsync(MediaCommand.Next);

    private async Task SendMusicCommandAsync(MediaCommand command)
    {
        MediaControls capability = command switch
        {
            MediaCommand.Previous => MediaControls.Previous,
            MediaCommand.Next => MediaControls.Next,
            _ => MediaControls.PlayPause
        };
        if (!_hasMusic || _receiver is not { } receiver || _mediaCommandPending || _closing ||
            _stopTask is not null || !_mediaState.Controls.HasFlag(capability))
            return;
        int generation = ++_mediaCommandGeneration;
        _mediaCommandPending = true;
        _mediaControlError = "";
        UpdateMusicControls();
        try
        {
            if (_mediaState.ControlSession != 0)
                await receiver.ControlMediaAsync(_mediaState, command);
            else
                await receiver.SendMediaCommandAsync(command);
        }
        catch (Win32Exception)
        {
            if (generation == _mediaCommandGeneration && ReferenceEquals(receiver, _receiver) && _hasMusic)
                _mediaControlError = Texts.MusicControlFailed;
        }
        finally
        {
            if (generation == _mediaCommandGeneration)
                _mediaCommandPending = false;
            if (!_closed)
                UpdateMusicControls();
        }
    }
}
