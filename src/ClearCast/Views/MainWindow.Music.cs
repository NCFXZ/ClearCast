using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.Graphics.Imaging;
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
    private bool _artworkLoading;
    private (Func<byte[]?> Read, int Generation, AirPlayReceiver? Receiver)? _pendingMusicArtwork;
    private bool _musicThemeRefreshing;
    private bool _musicThemeRefreshPending;
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
        // Lyrics sit beside the player when there is room, otherwise below it.
        bool lyrics = LyricsShown, sideBySide = lyrics && width >= MusicLyricsSideBySideWidth;
        double stackedLyrics = lyrics && !sideBySide ? Math.Clamp(height * 0.4, 200, 320) : 0;
        double columnWidth = sideBySide ? width / 2 : width;
        MusicLyrics.Visibility = lyrics || _lyricsClosing ? Visibility.Visible : Visibility.Collapsed;
        MusicLyricsColumn.Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        MusicLyricsRow.Height = new GridLength(stackedLyrics);
        if (!_lyricsClosing)
        {
            // A closing panel keeps its previous box until its fade-out completes.
            Grid.SetColumn(MusicLyrics, sideBySide ? 1 : 0);
            Grid.SetRow(MusicLyrics, sideBySide ? 0 : 1);
            MusicLyrics.HorizontalAlignment = HorizontalAlignment.Stretch;
            MusicLyrics.Width = double.NaN;
            MusicLyrics.Margin = sideBySide ? new Thickness(16, 48, 32, 48) : new Thickness(20, 0, 20, 24);
            MusicLyrics.Height = Math.Max(0, (sideBySide ? height : stackedLyrics) - MusicLyrics.Margin.Top - MusicLyrics.Margin.Bottom);
        }
        MusicLayout.MinHeight = height;
        MusicContent.Width = Math.Max(0, Math.Min(560, columnWidth - 40));
        double otherHeight = MusicContent.ActualHeight > MusicArtworkFrame.ActualHeight && MusicArtworkFrame.ActualHeight > 0
            ? MusicContent.ActualHeight - MusicArtworkFrame.ActualHeight + MusicContent.Margin.Top + MusicContent.Margin.Bottom
            : 320;
        double artworkSize = Math.Max(144, Math.Min(480, Math.Min(columnWidth - 64, height - stackedLyrics - Math.Max(240, otherHeight))));
        MusicArtworkFrame.Width = MusicArtworkFrame.Height = artworkSize;
    }

    private void UpdateMusicBackgroundState()
    {
        if (_closed) return;
        UpdateMusicAudioActivity();
        bool active = !_closed && !_closing && (_hasMusic && !_hasVideo || _hasVideo && !_playerFullScreen) &&
            _captionThemeSettings?.HighContrast != true && AppWindow.IsVisible &&
            WindowLayout.XamlRoot?.IsHostVisible == true &&
            AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        MusicBackground.SetActive(active, active && _mediaState.Playing && _motionSettings.AnimationsEnabled);
        MusicLyrics.EffectsEnabled = _captionThemeSettings?.HighContrast != true;
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
        if (_closed || _closing) return;
        _musicThemeRefreshPending = true;
        if (_musicThemeRefreshing) return;
        _musicThemeRefreshing = true;
        try
        {
            do
            {
                _musicThemeRefreshPending = false;
                if (_hasVideo && _videoBackdropPixels is { } pixels)
                    await MusicBackground.SetVideoFrameAsync(pixels, WindowLayout.ActualTheme == ElementTheme.Dark, _motionSettings.AnimationsEnabled);
                else
                    await MusicBackground.SetArtworkAsync(_hasVideo ? null : _musicArtworkBytes, WindowLayout.ActualTheme == ElementTheme.Dark);
                UpdateMusicBackgroundState();
            } while (_musicThemeRefreshPending && !_closed && !_closing);
        }
        finally { _musicThemeRefreshing = false; }
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
            ResetMediaDiagnosticsRaster();
            ResetMediaDiagnosticsStats();
            if (_hasVideo || state.Kind == ReceiverMediaKind.Video) ResetVideoBackdrop();
            ++_mediaCommandGeneration;
            _mediaCommandPending = false;
            _mediaControlError = "";
            _playbackControlError = "";
        }
        bool changed = state.Revision != _mediaState.Revision || state.Kind != _mediaState.Kind || state.Session != _mediaState.Session ||
            state.Playing != _mediaState.Playing || state.Controls != _mediaState.Controls ||
            state.Title != _mediaState.Title || state.Artist != _mediaState.Artist || state.Album != _mediaState.Album;
        bool lyricsDurationChanged = state.DurationMs != _mediaState.DurationMs;
        _mediaState = state;
        if (sourceChanged) CancelPlaybackSeek();
        _mediaSnapshotAt = Stopwatch.GetTimestamp();
        _hasMusic = state.Kind == ReceiverMediaKind.Audio;
        if (changed)
            _mediaControlError = "";

        MusicPanel.Visibility = _hasMusic && !_hasVideo ? Visibility.Visible : Visibility.Collapsed;
        VideoPlaceholder.Visibility = _hasMusic || _hasVideo ? Visibility.Collapsed : Visibility.Visible;
        if (wasMusic && !_hasMusic && !_hasVideo)
            SetPlayerFullScreen(false);
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
        else if (lyricsDurationChanged)
            RefreshLyrics();

        ulong revision = _hasMusic && state.ArtworkBytes > 0 ? state.ArtworkRevision : 0;
        if (sourceChanged || _artworkRevision != revision)
        {
            _artworkRevision = revision;
            int generation = ++_artworkGeneration;
            _pendingMusicArtwork = null;
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
        UpdateMediaDiagnostics();
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
        RefreshLyrics();
    }

    private void UpdateMusicControls()
    {
        bool ready = _hasMusic && _receiver is not null && !_mediaCommandPending && !_playbackControlPending &&
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
        // Full screen and lyrics join the transport row, or the disconnect row when there is no transport.
        Grid.SetRow(MusicFullScreenButton, hasControls ? 0 : 2);
        Grid.SetRow(MusicLyricsButton, hasControls ? 0 : 2);
        MusicControlHint.Margin = new Thickness(0, hasControls ? MusicContent.Spacing : 0, 0, 0);
        MusicDisconnectButton.Margin = new Thickness(0,
            hasControls || MusicControlHint.Visibility == Visibility.Visible ? MusicContent.Spacing : 0, 0, 0);
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
        UpdateLyricsPosition();
    }

    private static string FormatMusicTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1
            ? $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(long)time.TotalMinutes}:{time.Seconds:00}";
    }

    private Task LoadMusicArtworkAsync(AirPlayReceiver receiver, ulong revision, int length, int generation) =>
        QueueMusicArtworkAsync(() => receiver.ReadArtwork(revision, length), generation, receiver);

    private bool IsCurrentMusicArtwork(int generation, AirPlayReceiver? receiver) => generation == _artworkGeneration &&
        !_closed && !_closing && _hasMusic && ReferenceEquals(receiver, _receiver);

    private async Task QueueMusicArtworkAsync(Func<byte[]?> read, int generation, AirPlayReceiver? receiver)
    {
        if (!IsCurrentMusicArtwork(generation, receiver)) return;
        // Keep one active read/decode/background chain and one latest descriptor,
        // not a new 2 MiB read and WinRT decode for every incoming revision.
        _pendingMusicArtwork = (read, generation, receiver);
        if (_artworkLoading) return;
        _artworkLoading = true;
        try
        {
            while (_pendingMusicArtwork is { } request)
            {
                _pendingMusicArtwork = null;
                if (!IsCurrentMusicArtwork(request.Generation, request.Receiver)) continue;
                try
                {
                    byte[]? bytes = await Task.Run(request.Read);
                    if (bytes is not null && IsCurrentMusicArtwork(request.Generation, request.Receiver))
                        await ApplyMusicArtworkAsync(bytes, request.Generation, request.Receiver);
                }
                catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException or IOException)
                {
                    // A failed or superseded request cannot prevent the latest cover.
                }
            }
        }
        finally { _artworkLoading = false; }
    }

    private async Task ApplyMusicArtworkAsync(byte[] bytes, int generation, AirPlayReceiver? expectedReceiver)
    {
        if (bytes.Length is <= 0 or > 2 * 1024 * 1024 || !IsCurrentMusicArtwork(generation, expectedReceiver))
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
            if (!IsCurrentMusicArtwork(generation, expectedReceiver)) return;
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (!IsCurrentMusicArtwork(generation, expectedReceiver) || !ArtworkImage.IsSupported(decoder)) return;
            // Bound both dimensions before the XAML decoder sees sender-controlled artwork.
            double scale = Math.Min(1, 640d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var image = new BitmapImage
            {
                DecodePixelWidth = Math.Max(1, (int)Math.Round(decoder.PixelWidth * scale)),
                DecodePixelHeight = Math.Max(1, (int)Math.Round(decoder.PixelHeight * scale))
            };
            stream.Seek(0);
            await image.SetSourceAsync(stream);
            if (IsCurrentMusicArtwork(generation, expectedReceiver))
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
        if (!_hasMusic || _receiver is not { } receiver || _mediaCommandPending || _playbackControlPending || _closing ||
            _stopTask is not null || !_mediaState.Controls.HasFlag(capability))
            return;
        int generation = ++_mediaCommandGeneration;
        _mediaCommandPending = true;
        _mediaControlError = "";
        UpdateMusicControls();
        UpdatePlaybackControls();
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
            {
                UpdateMusicControls();
                UpdatePlaybackControls();
            }
        }
    }
}
