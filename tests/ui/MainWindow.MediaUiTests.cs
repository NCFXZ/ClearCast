#if CLEARCAST_UI_TESTS
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace ClearCast;

public sealed partial class MainWindow
{
    private async Task UiTestLivePlaybackControlsAsync(string output)
    {
        var preferences = _preferences;
        var state = _mediaState;
        _receiverTimer.Stop();
        try
        {
            UiCheck(_receiver is not null && _receiverReady, "live playback controls use the running isolated receiver");
            foreach (bool fullScreen in new[] { false, true })
            {
                _preferences = _preferences with { AutoFullScreen = fullScreen };
                var direct = new ReceiverMediaState(1, ReceiverMediaKind.Video, true, "", "", "", 0, 0, 0, 0,
                    MediaControls.None) { Source = "direct", ControlSession = 0x8000000000000042, ContentRevision = 0x8000000000000042 };
                ApplyMediaState(direct);
                UiCheck(_mediaState == direct && !_hasVideo, "live direct metadata arrives before the first video frame " + fullScreen);
                SetPlayerFrameSize(1920, 1080);
                SetReceiverStatus("AirPlay 已连接");
                SetVideoAvailable(true);
                UiCheck(_hasVideo && PlayerFrame.Visibility == Visibility.Visible, "live video surface becomes available " + fullScreen);
                for (int frame = 0; frame < 80; ++frame)
                {
                    long duration = frame < 5 ? 0 : frame < 40 ? 7_200_000 : 604_800_000;
                    ApplyMediaState(direct with { Revision = (ulong)frame + 2, PositionMs = frame < 6 ? 0 : frame * 150,
                        DurationMs = duration, SeekEndMs = duration, Controls = duration > 0 ? MediaControls.PlayPause | MediaControls.Seek : MediaControls.PlayPause });
                    if (duration > 0) UiCheck(VideoSeekSlider.Maximum == 1000 && VideoSeekSlider.Minimum == 0,
                        "live seek bounds remain normalized " + fullScreen + " " + frame);
                    else UiCheck(VideoSeekSlider.Visibility == Visibility.Collapsed, "unknown duration has no seek control " + fullScreen + " " + frame);
                    if (frame == 0) UiCheck(VideoTransport.Visibility == Visibility.Collapsed &&
                        VideoPlaybackActions.Visibility == Visibility.Collapsed && !CanToggleCurrentPlayback,
                        "live or not-yet-seekable video has no transport controls or Space action " + fullScreen);
                    if (duration > 0) UiCheck(VideoPlayPauseButton.IsEnabled && VideoSeekSlider.IsEnabled,
                        "live direct video enables controls with dynamic range " + fullScreen + " " + frame);
                    await Task.Delay(30);
                }
                await UiWaitForStableLayout(VideoTransport);
                UiCheck(Grid.GetRow(PlayerControls) == (fullScreen ? 1 : 2),
                    "player controls use the overlay row only in full screen " + fullScreen);
                UiBounds videoBounds = UiMeasure(VideoHostSlot), controlsBounds = UiMeasure(PlayerControls);
                UiCheck(fullScreen ? controlsBounds.Y + controlsBounds.Height <= videoBounds.Y + videoBounds.Height + 1 :
                    controlsBounds.Y >= videoBounds.Y + videoBounds.Height,
                    "windowed controls sit below video and full-screen controls stay inside it " + fullScreen);
                UiBounds pauseBounds = UiMeasure(VideoPlayPauseButton), toolsBounds = UiMeasure(PlayerActions), seekBounds = UiMeasure(VideoSeekSlider);
                UiCheck(Math.Abs(pauseBounds.CenterY - toolsBounds.CenterY) <= 1 &&
                    Math.Abs(pauseBounds.X + pauseBounds.Width / 2 - controlsBounds.X - controlsBounds.Width / 2) <= 1 &&
                    UiMeasure(PlayerStopButton).X < UiMeasure(VideoPlaybackActions).X && seekBounds.Y < pauseBounds.Y,
                    "playback stays centered between the text disconnect and right-aligned video actions " + fullScreen);
                UiCheck(UiMeasure(VideoElapsedText).X < seekBounds.X &&
                    UiMeasure(VideoRemainingText).X > seekBounds.X + seekBounds.Width && VideoRemainingText.Text.StartsWith('−'),
                    "elapsed and remaining time flank the video timeline " + fullScreen);
                if (!fullScreen && _captionThemeSettings?.HighContrast != true)
                    await UiTestVideoBackdropAsync(output);
                UiCheck(MusicBackground.IsActive == (!fullScreen && _captionThemeSettings?.HighContrast != true),
                    "video backdrop is app-wide only outside full screen " + fullScreen);
                ShowPlayerOverlay();
                await UiCaptureAsync(output, "live-video-controls-" + fullScreen);
                SetVideoAvailable(false);
                ApplyMediaState(ReceiverMediaState.Empty);
            }
            _preferences = _preferences with { AutoFullScreen = false };
            ApplyMediaState(new ReceiverMediaState(190, ReceiverMediaKind.Video, true, "", "", "", 0, 0, 0, 0,
                MediaControls.None) { Source = "mirror", Connected = true });
            SetPlayerFrameSize(720, 1280);
            SetVideoAvailable(true);
            await UiWaitForStableLayout(PlayerControls);
            UiCheck(VideoTransport.Visibility == Visibility.Collapsed && VideoPlaybackActions.Visibility == Visibility.Collapsed &&
                !CanToggleCurrentPlayback && PlayerStopButton.IsEnabled && PlayerActions.Visibility == Visibility.Visible,
                "mirroring offers only text disconnect rotation and full screen");
            await UiCaptureAsync(output, "mirror-controls");
            ApplyMediaState(new ReceiverMediaState(191, ReceiverMediaKind.Video, true, "", "", "", 20_000, 60_000, 0, 0,
                MediaControls.PlayPause | MediaControls.Seek) { Source = "direct", Connected = true, IsLive = true,
                ControlSession = 191, ContentRevision = 191, SeekEndMs = 60_000 });
            UiCheck(VideoTransport.Visibility == Visibility.Collapsed && VideoPlaybackActions.Visibility == Visibility.Collapsed &&
                !CanToggleCurrentPlayback, "live video with a seekable DVR window still omits playback controls");
            await UiCaptureAsync(output, "live-controls");
            ApplyMediaState(_mediaState with { Revision = 192, IsLive = false, Controls = MediaControls.PlayPause });
            UiCheck(VideoPlaybackActions.Visibility == Visibility.Visible && VideoPlayPauseButton.IsEnabled &&
                !VideoSeekSlider.IsEnabled && !VideoSkipBackwardButton.IsEnabled && !VideoSkipForwardButton.IsEnabled &&
                VideoSeekSlider.Value > 0, "finite video without seek support keeps pause and read-only progress");
            SetVideoAvailable(false);
            ApplyMediaState(ReceiverMediaState.Empty);
            var audio = new ReceiverMediaState(200, ReceiverMediaKind.Audio, true, "Live seek track", "Test artist", "", 60_000,
                180_000, 0, 0, MediaControls.PlayPause | MediaControls.Seek)
                { Source = "audio", ControlSession = 42, ContentRevision = 43, SeekEndMs = 180_000 };
            ApplyMediaState(audio);
            UiCheck(MusicSeekSlider.IsEnabled && MusicSeekSlider.Visibility == Visibility.Visible &&
                MusicSeekSlider.Maximum == 1000 && CanToggleCurrentPlayback, "live music enables the normalized seek control and Space action");
            await UiWaitForStableLayout(MusicSeekSlider);
            BeginPlaybackSeek(MusicSeekSlider);
            MusicSeekSlider.Value = 500;
            ApplyMediaState(audio with { Revision = 201, PositionMs = 61_000 });
            UiCheck(MusicSeekSlider.Value == 500 && _playbackSeekState == audio,
                "live music polling preserves a drag and its captured track");
            ApplyMediaState(audio with { Revision = 202, ContentRevision = 44 });
            UiCheck(_playbackSeekSlider is null, "live track replacement cancels the old drag");
        }
        finally
        {
            CancelPlaybackSeek();
            SetVideoAvailable(false);
            _preferences = preferences;
            ApplyMediaState(state);
            _receiverTimer.Start();
        }
    }

    private async Task UiTestVideoBackdropAsync(string output)
    {
        byte[] Frame(byte red, byte green, byte blue)
        {
            var pixels = new byte[ReceiverVideoFrame.ByteCount];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int x = i / 4 % ReceiverVideoFrame.Side, y = i / 4 / ReceiverVideoFrame.Side;
                pixels[i] = (byte)(blue * (0.35 + 0.65 * x / 31));
                pixels[i + 1] = (byte)(green * (0.35 + 0.65 * y / 31));
                pixels[i + 2] = (byte)(red * (0.35 + 0.65 * (31 - x) / 31));
                pixels[i + 3] = 255;
            }
            return pixels;
        }
        bool dark = WindowLayout.ActualTheme == ElementTheme.Dark;
        _videoBackdropPixels = Frame(220, 80, 35);
        await MusicBackground.SetVideoFrameAsync(_videoBackdropPixels, dark, false);
        UpdateMusicBackgroundState();
        UiCheckAppWideMusicBackdrop("windowed video");
        UiBounds backdrop = UiMeasure(MusicBackground), window = UiMeasure(WindowLayout);
        UiCheck(backdrop.X == window.X && backdrop.Y == window.Y && Math.Abs(backdrop.Width - window.Width) <= 1 &&
            Math.Abs(backdrop.Height - window.Height) <= 1, "video backdrop covers title navigation content and player controls edge to edge");
        MusicBackground.PauseFlowClockForTest(true);
        await Task.Delay(150);
        nint ownedWindow = WindowNative.GetWindowHandle(this);
        var beforeCut = await Task.Run(() => UiCaptureOwnedWindow(ownedWindow));
        var oldPalette = MusicBackground.Palette;
        _videoBackdropPixels = Frame(35, 85, 225);
        var sceneChangeWatch = Stopwatch.StartNew();
        Task transition = MusicBackground.SetVideoFrameAsync(_videoBackdropPixels, dark);
        await UiWaitUntil(() => MusicBackground.IsVideoTransitioning, "video backdrop crossfade starts");
        double preparationMilliseconds = sceneChangeWatch.Elapsed.TotalMilliseconds;
        MusicBackground.PauseFlowClockForTest(true);
        await Task.Delay(MusicBackdrop.VideoTransitionMilliseconds / 3);
        UiCheck(!transition.IsCompleted && MusicBackground.IsVideoTransitioning && MusicBackground.Palette == oldPalette,
            "an abrupt video scene change retains the previous base palette while crossfading");
        var duringCut = await Task.Run(() => UiCaptureOwnedWindow(ownedWindow));
        await transition;
        UiCheck(!MusicBackground.IsVideoTransitioning && MusicBackground.Palette != oldPalette && MusicBackground.HasArtworkFlow,
            "video scene transition finishes without dropping the shared flowing texture set");
        var afterCut = await Task.Run(() => UiCaptureOwnedWindow(ownedWindow));
        double[] Mean(UiCompositorFrame frame)
        {
            var channels = new double[3];
            int count = 0;
            for (int y = frame.Height / 4; y < frame.Height * 2 / 3; y += 8)
                for (int x = frame.Width / 25; x < frame.Width / 9; x += 8)
                {
                    int offset = (y * frame.Width + x) * 4;
                    for (int channel = 0; channel < 3; ++channel) channels[channel] += frame.Pixels[offset + channel];
                    ++count;
                }
            return channels.Select(value => value / Math.Max(1, count)).ToArray();
        }
        double[] first = Mean(beforeCut), middle = Mean(duringCut), last = Mean(afterCut);
        double squaredSpan = Enumerable.Range(0, 3).Sum(channel => Math.Pow(last[channel] - first[channel], 2));
        double blend = squaredSpan > 0 ? Enumerable.Range(0, 3).Sum(channel =>
            (middle[channel] - first[channel]) * (last[channel] - first[channel])) / squaredSpan : 0;
        bool captured = squaredSpan > 625 && beforeCut.Width == afterCut.Width && beforeCut.Height == afterCut.Height;
        if (captured)
            UiCheck(blend > 0.05 && blend < 0.95, "owned-window pixels show an intermediate video backdrop color rather than a hard cut");
        _uiTestLayouts.Add(new { phase = "video scene crossfade", status = captured ? "captured" : "unsupported",
            first, middle, last, blend, squaredSpan, preparationMilliseconds,
            completedMilliseconds = sceneChangeWatch.Elapsed.TotalMilliseconds,
            transitionMilliseconds = MusicBackdrop.VideoTransitionMilliseconds });
        await UiSaveCompositorFrameAsync(output, "video-cut-before", beforeCut);
        await UiSaveCompositorFrameAsync(output, "video-cut-during", duringCut);
        await UiSaveCompositorFrameAsync(output, "video-cut-after", afterCut);
        MusicBackground.PauseFlowClockForTest(false);
        var timings = new List<double>();
        for (int index = 0; index < 12; ++index)
        {
            var updateWatch = Stopwatch.StartNew();
            await MusicBackground.SetVideoFrameAsync(Frame((byte)(40 + index * 12), 95, (byte)(220 - index * 12)), dark);
            timings.Add(updateWatch.Elapsed.TotalMilliseconds);
        }
        UiCheck(MusicBackground.TextureFrameCount == 1 && timings.Max() <= 250,
            "video updates reuse the shared warp renderer with one texture and finish within 250 ms on this host");
        _uiTestLayouts.Add(new { phase = "video backdrop update latency", samples = timings,
            meanMilliseconds = timings.Average(), maximumMilliseconds = timings.Max(),
            transitionMilliseconds = MusicBackdrop.VideoTransitionMilliseconds });
        await UiNavigateAsync(SettingsNavigationItem, SettingsPage);
        UiCheckAppWideMusicBackdrop("video settings");
        await UiCaptureAsync(output, "video-background-settings");
        await UiNavigateAsync(MirrorNavigationItem, MirrorPage);
        await UiCaptureAsync(output, "video-background-player");
        var previousSize = AppWindow.Size;
        try
        {
            double scale = WindowLayout.XamlRoot.RasterizationScale;
            foreach (int width in new[] { 640, 880, 1120 })
            {
                AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(760 * scale)));
                await UiWaitForStableLayout(PlayerControls, VideoHostSlot);
                UiBounds bar = UiMeasure(PlayerControls), transport = UiMeasure(VideoPlaybackActions), actions = UiMeasure(PlayerActions);
                _uiTestLayouts.Add(new { phase = "video control resize", width, bar, transport, actions,
                    disconnect = UiMeasure(PlayerStopButton), timeline = UiMeasure(VideoSeekSlider) });
                UiCheck(UiMeasure(PlayerStopButton).X + UiMeasure(PlayerStopButton).Width + 8 <= transport.X &&
                    transport.X + transport.Width + 8 <= actions.X && actions.X + actions.Width <= bar.X + bar.Width - 8 &&
                    UiMeasure(VideoSeekSlider).Width >= 120,
                    "player transport and secondary actions remain separate at width " + width);
                UiCheck(bar.Y >= UiMeasure(VideoHostSlot).Y + UiMeasure(VideoHostSlot).Height,
                    "resizing keeps the windowed control bar below the video at width " + width);
                await UiCaptureAsync(output, "video-player-width-" + width);
            }
        }
        finally
        {
            AppWindow.Resize(previousSize);
            await UiWaitForStableLayout(PlayerControls, VideoHostSlot);
        }
    }

    private async Task UiTestMediaFeaturesAsync(string output)
    {
        // Real XAML/WinRT image decoding and owned-window presenter paths, with
        // synthetic media state and a stopped receiver. No stream or remote command.
        var previousPreferences = _preferences;
        string previousStatus = _receiverStatus;
        nint window = WindowNative.GetWindowHandle(this);
        var previousPlacement = new PlayerWindowPlacement { Length = Marshal.SizeOf<PlayerWindowPlacement>() };
        bool havePlacement = GetWindowPlacement(window, ref previousPlacement);
        try
        {
            UiCheck(_receiver is null && !_hasVideo && !_hasMusic && !_playerFullScreen,
                "media feature UI tests begin with a stopped receiver and idle player");
            await UiNavigateAsync(SettingsNavigationItem, SettingsPage);
            AutoFullScreenToggle.IsOn = false;
            AutoFullScreenToggle.IsOn = true;
            UiCheck(_preferences.AutoFullScreen && ReceiverPreferences.Load(_root).AutoFullScreen,
                "automatic full-screen switch persists its enabled value");
            await UiNavigateAsync(MirrorNavigationItem, MirrorPage);

            var audio = new ReceiverMediaState(1, ReceiverMediaKind.Audio, true, "Test track", "Test artist", "Test album",
                30_000, 180_000, 0, 0, MediaControls.None);
            ApplyMediaState(audio);
            UiCheck(_hasMusic && !_hasVideo && MusicPanel.Visibility == Visibility.Visible &&
                VideoPlaceholder.Visibility == Visibility.Collapsed && PlayerFrame.Visibility == Visibility.Collapsed && !_playerFullScreen,
                "audio-only media shows music without a video player or automatic full screen");
            UiCheck(MusicTitle.Text == audio.Title && MusicArtist.Text == audio.Artist && MusicAlbum.Text == audio.Album &&
                MusicAlbum.Visibility == Visibility.Visible, "music metadata reaches its title artist and album fields");
            UiCheck(!MusicProgress.IsIndeterminate && MusicProgress.Maximum == 180_000 &&
                MusicProgress.Value >= 30_000 && MusicProgress.Value < 31_000 && MusicDuration.Text == "3:00",
                "known music duration uses a bounded determinate progress bar");
            UiCheck(!MusicPlayPauseButton.IsEnabled && !MusicPreviousButton.IsEnabled && !MusicNextButton.IsEnabled &&
                MusicTransportControls.Visibility == Visibility.Collapsed && MusicControlHint.Visibility == Visibility.Collapsed,
                "unsupported music transport controls and hint occupy no layout space");
            var seekAudio = audio with { Controls = MediaControls.PlayPause | MediaControls.Seek,
                ControlSession = 41, ContentRevision = 2, SeekEndMs = 180_000 };
            ApplyMediaState(seekAudio);
            UiCheck(MusicSeekSlider.Visibility == Visibility.Visible && MusicProgress.Visibility == Visibility.Collapsed &&
                !MusicSeekSlider.IsEnabled, "supported music seek shows a slider but a stopped receiver cannot send commands");
            await UiWaitForStableLayout(MusicSeekSlider);
            await UiCaptureAsync(output, "playback-music-seek");
            MusicSeekSlider.IsEnabled = true;
            BeginPlaybackSeek(MusicSeekSlider);
            MusicSeekSlider.Value = 500;
            UpdateMusicProgress();
            UiCheck(MusicSeekSlider.Value == 500 && seekAudio.SeekPosition(500) == 90_000, "metadata polling cannot overwrite the position being dragged");
            ApplyMediaState(seekAudio with { ContentRevision = 3 });
            UiCheck(_playbackSeekSlider is null, "a replacement track cancels an unfinished drag");
            SetVideoAvailable(true);
            ApplyMediaState(new ReceiverMediaState(3, ReceiverMediaKind.Video, true, "", "", "", 10_000, 120_000, 0, 0,
                MediaControls.PlayPause | MediaControls.Seek) { Source = "direct", ControlSession = 55, ContentRevision = 55, SeekEndMs = 120_000 });
            UiCheck(VideoTransport.Visibility == Visibility.Visible && VideoSeekSlider.Maximum == 1000 &&
                !VideoPlayPauseButton.IsEnabled && !VideoSeekSlider.IsEnabled, "direct video exposes transport without sending commands from a stopped receiver");
            await UiWaitForStableLayout(VideoTransport);
            await UiCaptureAsync(output, "playback-video-controls");
            ApplyMediaState(_mediaState with { Source = "mirror", Controls = MediaControls.None });
            UiCheck(VideoTransport.Visibility == Visibility.Collapsed, "screen mirroring has no video transport controls");
            SetVideoAvailable(false);
            ApplyMediaState(audio with { Controls = MediaControls.PlayPause | MediaControls.Previous | MediaControls.Next });
            UiCheck(MusicTransportControls.Visibility == Visibility.Visible && MusicPlayPauseButton.Visibility == Visibility.Visible &&
                MusicPreviousButton.Visibility == Visibility.Visible && MusicNextButton.Visibility == Visibility.Visible,
                "available sender transport capabilities expose their controls");
            ApplyMediaState(audio with { Controls = MediaControls.PlayPause });
            UiCheck(MusicPlayPauseButton.Visibility == Visibility.Visible && MusicPreviousButton.Visibility == Visibility.Collapsed &&
                MusicNextButton.Visibility == Visibility.Collapsed,
                "unsupported individual transport actions do not reserve button space");
            _mediaCommandPending = true;
            _mediaControlError = Texts.MusicControlFailed;
            UpdateMusicControls();
            ApplyMediaState(audio with { Controls = MediaControls.PlayPause, Session = 1 });
            UiCheck(!_mediaCommandPending && MusicControlHint.Visibility == Visibility.Collapsed,
                "a new audio session clears pending commands and stale failure text even when its metadata is identical");
            ApplyMediaState(audio);
            MusicBackground.SetActive(true, false);
            UiCheck(MusicBackground.IsActive && !MusicBackground.IsAnimating,
                "disabled background motion retains a static cover-colored backdrop");
            MusicBackground.SetActive(false, false);
            UiCheck(!MusicBackground.IsActive && !MusicBackground.IsAnimating,
                "inactive or high-contrast backdrop requests hide it and stop compositor motion");
            UpdateMusicBackgroundState();

            ApplyMediaState(audio with { Revision = 2, PositionMs = 179_500 });
            _mediaSnapshotAt = Stopwatch.GetTimestamp() - 2 * Stopwatch.Frequency;
            UpdateMusicProgress();
            UiCheck(MusicProgress.Value == 180_000 && MusicElapsed.Text == "3:00",
                "displayed music progress clamps at the track duration");
            ApplyMediaState(audio with { Revision = 3, Playing = false });
            await Task.Delay(120);
            UpdateMusicProgress();
            UiCheck(MusicProgress.Value == 30_000 && MusicElapsed.Text == "0:30" &&
                MusicPlayPauseIcon.Symbol == Symbol.Play,
                "paused music does not advance and exposes the play action");
            UiCheck(!MusicBackground.IsAnimating && MusicBackground.AnimationChannelCount == 0,
                "paused music stops and releases every compositor animation channel");
            ApplyMediaState(audio with { Revision = 4, DurationMs = 0 });
            UiCheck(MusicProgress.IsIndeterminate && MusicProgress.Value == 0 && MusicDuration.Text == "—:—",
                "unknown music duration uses an indeterminate bar and unknown duration label");
            ApplyMediaState(audio with { Revision = 5, Title = "", Artist = "", Album = "" });
            UiCheck(MusicTitle.Text == Texts.MusicUnknownTitle && MusicArtist.Text == Texts.MusicUnknownArtist &&
                MusicAlbum.Visibility == Visibility.Collapsed, "empty music metadata uses localized fallbacks and hides the album row");

            byte[] pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXYxDpOfEfAASIAmgnDVFdAAAAAElFTkSuQmCC");
            ApplyMediaState(audio with { Revision = 6, ArtworkRevision = 1, ArtworkBytes = pixel.Length });
            int artworkGeneration = _artworkGeneration;
            await ApplyMusicArtworkAsync(pixel, artworkGeneration, null);
            UiCheck(MusicArtwork.Source is BitmapImage { PixelWidth: > 0, PixelHeight: > 0 } &&
                MusicArtworkPlaceholder.Visibility == Visibility.Collapsed, "a valid one-pixel PNG decodes through the real artwork path");
            var acceptedArtwork = MusicArtwork.Source;
            await ApplyMusicArtworkAsync(pixel, artworkGeneration - 1, null);
            UiCheck(ReferenceEquals(acceptedArtwork, MusicArtwork.Source), "superseded artwork generations cannot replace the current image");
            ApplyMediaState(audio with { Revision = 7, ArtworkRevision = 2, ArtworkBytes = 4 });
            await ApplyMusicArtworkAsync(new byte[] { 0, 1, 2, 3 }, _artworkGeneration, null);
            UiCheck(MusicArtwork.Source is null && MusicArtworkPlaceholder.Visibility == Visibility.Visible,
                "invalid artwork leaves a harmless placeholder");

            byte[] screenshotArtwork = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "ClearCast.png"));
            foreach (bool english in new[] { false, true })
            {
                await UiNavigateAsync(SettingsNavigationItem, SettingsPage);
                LanguageButton.SelectedItem = english ? EnglishLanguageItem : ChineseLanguageItem;
                ThemeButton.SelectedItem = english ? DarkThemeItem : LightThemeItem;
                await Task.Delay(100);
                UiCheck(Texts.IsEnglish == english && WindowLayout.ActualTheme == (english ? ElementTheme.Dark : ElementTheme.Light),
                    "music screenshot language and theme applied " + english);
                UiCheckAutoFullScreenCardGeometry(english ? "en-dark" : "zh-light");
                UiCheckAppWideMusicBackdrop("settings " + english);
                if (english)
                {
                    await UiWaitForStableLayout(SettingsLayout);
                    await UiTestOwnedCompositorCaptureAsync(output, "en-dark-settings", SettingsLayout);
                }
                await UiNavigateAsync(AboutNavigationItem, AboutPage);
                UiCheckAppWideMusicBackdrop("about " + english);
                if (english)
                    await UiTestOwnedCompositorCaptureAsync(output, "en-dark-about", AboutLayout);
                await UiNavigateAsync(MirrorNavigationItem, MirrorPage);
                UiCheckAppWideMusicBackdrop("airplay " + english);
                foreach (int logicalWidth in new[] { 640, 1120 })
                {
                    double scale = WindowLayout.XamlRoot.RasterizationScale;
                    AppWindow.Resize(new SizeInt32((int)Math.Round(logicalWidth * scale), (int)Math.Round(720 * scale)));
                    ApplyMediaState(audio with
                    {
                        Revision = (ulong)(100 + logicalWidth + (english ? 10_000 : 0)), PositionMs = 45_000,
                        Title = english ? "A quiet afternoon" : "午后的音乐", Artist = english ? "ClearCast test artist" : "ClearCast 测试歌手",
                        Album = english ? "Synthetic UI test album" : "界面测试专辑",
                        ArtworkRevision = (ulong)(100 + logicalWidth + (english ? 10_000 : 0)), ArtworkBytes = screenshotArtwork.Length
                    });
                    await ApplyMusicArtworkAsync(screenshotArtwork, _artworkGeneration, null);
                    UiCheck(MusicArtwork.Source is not null && MusicArtworkPlaceholder.Visibility == Visibility.Collapsed,
                        "music captures use the project logo as synthetic artwork and derive its backdrop palette " + logicalWidth + " " + english);
                    MusicPanel.ChangeView(null, 0, null, true);
                    await UiWaitForStableLayout(MusicPanel, MusicTitle, MusicArtist, MusicAlbum, MusicProgress);
                    string prefix = (english ? "en-dark-" : "zh-light-") + logicalWidth;
                    UiCheckMusicGeometry(prefix);
                    if (english)
                        UiCheckEnglishVisibleText(MusicPanel, "music " + prefix);
                    await UiCaptureAsync(output, prefix + "-music");
                    if (logicalWidth == 1120)
                        await UiTestOwnedCompositorCaptureAsync(output, prefix);
                }
            }

            byte[] rhythmArtwork = await UiCreateRhythmArtworkAsync();
            ApplyMediaState(_mediaState with { ArtworkRevision = _mediaState.ArtworkRevision + 1, ArtworkBytes = rhythmArtwork.Length });
            await ApplyMusicArtworkAsync(rhythmArtwork, _artworkGeneration, null);
            await UiWaitForStableLayout(MusicPanel);
            await UiTestOwnedCompositorCaptureAsync(output, "en-dark-rhythm-texture");
            await UiTestRhythmMotionAsync();
            await UiTestBeatMotionAsync(output);
            ThemeButton.SelectedItem = LightThemeItem;
            byte[] mutedArtwork = await UiCreateRhythmArtworkAsync(muted: true);
            ApplyMediaState(_mediaState with { ArtworkRevision = _mediaState.ArtworkRevision + 1, ArtworkBytes = mutedArtwork.Length });
            await ApplyMusicArtworkAsync(mutedArtwork, _artworkGeneration, null);
            await UiWaitForStableLayout(MusicPanel, MusicArtworkFrame);
            await UiTestOwnedCompositorCaptureAsync(output, "en-light-rhythm-texture");
            await UiTestBeatMotionAsync(output);
            ApplyMediaState(ReceiverMediaState.Empty);
            UiCheck(!_hasMusic && MusicPanel.Visibility == Visibility.Collapsed && VideoPlaceholder.Visibility == Visibility.Visible,
                "ending audio clears the music card and restores the landing view");
            UiCheck(!MusicBackground.IsActive && !MusicBackground.IsAnimating && MusicBackground.AnimationChannelCount == 0,
                "ending audio removes the app-wide music backdrop and its animation");
            await UiNavigateAsync(SettingsNavigationItem, SettingsPage);
            AutoFullScreenToggle.IsOn = false;
            AutoFullScreenToggle.IsOn = true;
            UiCheck(!_playerFullScreen, "changing automatic full screen while idle does not enter full screen");
            UiCheck(UiGetWindowRect(window, out var beforeAutoFullScreen), "record window bounds before automatic full screen");
            SetPlayerFrameSize(320, 180);
            SetVideoAvailable(true);
            await UiWaitUntil(() => _playerFullScreen && IsMirrorPageSelected, "new video session automatically enters full screen from settings");
            await UiWaitForStableLayout(VideoHostSlot);
            UiCheck(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen && !MainNavigation.IsPaneVisible,
                "automatic full screen uses the existing full-screen presenter and hides navigation");
            SetPlayerFullScreen(false);
            await UiWaitForStableLayout(VideoHostSlot);
            SetPlayerFrameSize(640, 360);
            SetVideoAvailable(true);
            UiCheck(!_playerFullScreen, "later video geometry and repeated availability respect a manual full-screen exit");
            AutoFullScreenToggle.IsOn = false;
            AutoFullScreenToggle.IsOn = true;
            UiCheck(!_playerFullScreen && _preferences.AutoFullScreen && ReceiverPreferences.Load(_root).AutoFullScreen,
                "changing the switch during active video persists without forcing full screen");
            SetVideoAvailable(false);
            await UiWaitForStableLayout(MirrorPage);
            UiCheck(!_playerFullScreen && UiGetWindowRect(window, out var afterAutoFullScreen) &&
                beforeAutoFullScreen.Width == afterAutoFullScreen.Width && beforeAutoFullScreen.Height == afterAutoFullScreen.Height,
                "ending video restores the window dimensions after automatic full screen");
            SetPlayerFrameSize(320, 180);
            SetVideoAvailable(true);
            await UiWaitUntil(() => _playerFullScreen, "the next video session re-arms automatic full screen");
            SetVideoAvailable(false);
            UiCheck(!_playerFullScreen, "disconnecting an automatically full-screen video exits full screen");
        }
        finally
        {
            SetVideoAvailable(false);
            SetPlayerFullScreen(false);
            ApplyMediaState(ReceiverMediaState.Empty);
            ++_artworkGeneration;
            MusicArtwork.Source = null;
            MusicArtworkPlaceholder.Visibility = Visibility.Visible;
            bool settingsReady = _settingsReady;
            _settingsReady = false;
            try
            {
                _preferences = previousPreferences;
                ThemeButton.SelectedItem = previousPreferences.Theme switch
                {
                    AppTheme.Light => LightThemeItem, AppTheme.Dark => DarkThemeItem, _ => SystemThemeItem
                };
                LanguageButton.SelectedItem = previousPreferences.Language == AppLanguage.English ? EnglishLanguageItem : ChineseLanguageItem;
                DisplaySizeButton.SelectedItem = previousPreferences.OriginalSize ? OriginalDisplayItem : FitDisplayItem;
                AutoFullScreenToggle.IsOn = previousPreferences.AutoFullScreen;
            }
            finally { _settingsReady = settingsReady; }
            bool english = previousPreferences.Language == AppLanguage.English;
            Texts.SetEnglish(english);
            WindowLayout.Language = english ? "en-US" : "zh-CN";
            ApplyAppTheme();
            RefreshComboBoxLabels();
            RefreshDynamicText();
            SetReceiverStatus(previousStatus);
            _preferences.Save(_root);
            if (havePlacement)
                SetWindowPlacement(window, in previousPlacement);
        }
        UiCheck(!_hasVideo && !_hasMusic && !_playerFullScreen && !_playerOverlayTimer.IsRunning &&
            _preferences == previousPreferences, "media feature UI test cleanup restores preferences and idle player state");
    }

    private static async Task<byte[]> UiCreateRhythmArtworkAsync(bool muted = false)
    {
        const int side = 96;
        byte[] pixels = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            // Asymmetric synthetic colors expose texture deformation without
            // relying on a private album cover or a mostly white app logo.
            uint color = x > 39 - y / 3 && y < 60 + x / 4 ? 0xFF14244Cu : 0xFFF1C94Eu;
            if (x is >= 3 and < 35 && y is >= 68 and < 87) color = 0xFF999182u;
            if (muted)
            {
                color = x < 16 || x > 83 - y / 4 ? 0xFF382D32u : 0xFFCCA9A4u;
                if (x is >= 68 and < 85 && y is >= 18 and < 82) color = 0xFF20ADB9u;
                if (x < 30 && y > 68) color = 0xFF9896A0u;
            }
            int offset = (y * side + x) * 4;
            pixels[offset] = (byte)color; pixels[offset + 1] = (byte)(color >> 8);
            pixels[offset + 2] = (byte)(color >> 16); pixels[offset + 3] = 255;
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, side, side, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        byte[] bytes = new byte[checked((int)stream.Size)];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task UiTestRhythmMotionAsync()
    {
        if (!MusicBackground.IsAnimating || !MusicBackground.HasArtworkFlow)
        {
            _uiTestLayouts.Add(new { phase = "audio-reactive background", status = "unsupported",
                reason = "The current motion/accessibility policy or artwork does not enable background animation." });
            return;
        }
        UiCheck(MusicBackground.TextureFrameCount == 32 && MusicBackground.RateControlledChannelCount == 1,
            "audio-reactive artwork shares exactly one compositor playback-rate controller");
        var source = _mediaState;
        long starts = MusicBackground.AnimationStartCount;
        MusicBackground.ResetAudioActivity();
        async Task Feed(float energy, float density, int windows)
        {
            for (int index = 0; index < windows; index++)
            {
                await Task.Delay(150);
                ApplyMediaState(source with { AudioActivity = new(true, energy, density, 0) });
            }
        }
        float? quietPhaseBefore = MusicBackground.Phase;
        await Feed(0.1f, 0, 5);
        float quietRate = MusicBackground.MotionRate;
        float? quietPhaseAfter = MusicBackground.Phase;
        await Feed(0.6f, 1, 7);
        float denseRate = MusicBackground.MotionRate;
        float? appliedRate = MusicBackground.AppliedPlaybackRate;
        UiCheck(denseRate > quietRate + 0.7f && appliedRate.HasValue &&
            Math.Abs(appliedRate.Value - denseRate / MusicMotion.BaseRate) < 0.05f,
            "dense decoded-audio activity increases the real compositor playback rate over quiet music");
        UiCheck(MusicBackground.AnimationStartCount == starts,
            "audio activity updates do not restart the artwork phase clock or opacity expressions");
        float? densePhaseBefore = MusicBackground.Phase;
        await Feed(0.6f, 1, 5);
        float? densePhaseAfter = MusicBackground.Phase;
        _mediaSnapshotAt = Stopwatch.GetTimestamp() - 2 * Stopwatch.Frequency;
        for (int index = 0; index < 5; index++)
        {
            await Task.Delay(150);
            UpdateMusicAudioActivity();
        }
        float expiredRate = MusicBackground.MotionRate;
        UiCheck(expiredRate < denseRate - 0.2f && MusicBackground.AnimationStartCount == starts,
            "expired audio samples return smoothly toward base flow without restarting animation");
        ApplyMediaState(source with { Session = source.Session == ulong.MaxValue ? 1 : source.Session + 1, AudioActivity = default });
        UiCheck(Math.Abs(MusicBackground.MotionRate - MusicMotion.BaseRate) < 0.001f &&
            MusicBackground.AnimationStartCount == starts,
            "a new audio session clears the prior rhythm while preserving the artwork phase");
        ApplyMediaState(source with { Playing = false, AudioActivity = new(true, 1, 1, 0) });
        UiCheck(!MusicBackground.IsAnimating && MusicBackground.AnimationChannelCount == 0 &&
            Math.Abs(MusicBackground.MotionRate - MusicMotion.BaseRate) < 0.001f,
            "paused music stops all phase animations and resets its reactive speed");
        _uiTestLayouts.Add(new { phase = "audio-reactive background", status = "verified", quietRate, denseRate,
            expiredRate, appliedRate, quietPhaseBefore, quietPhaseAfter, densePhaseBefore, densePhaseAfter,
            note = "Synthetic activity drives the real XAML bridge/controller; decoded PCM analysis is verified by separate native fixtures." });
        ApplyMediaState(source with { AudioActivity = default });
    }

    private void UiCheckAppWideMusicBackdrop(string page)
    {
        bool colorEnabled = _captionThemeSettings?.HighContrast != true;
        bool motionEnabled = colorEnabled && MusicBackground.HasArtworkFlow && _mediaState.Playing && _motionSettings.AnimationsEnabled;
        UiCheck(MusicBackground.IsActive == colorEnabled && MusicBackground.IsAnimating == motionEnabled,
            "music backdrop remains app-wide across tabs and respects the current motion/accessibility policy " + page);
        if (motionEnabled)
        {
            UiCheck(_hasVideo ? MusicBackground.TextureFrameCount == 1 && MusicBackground.AnimationChannelCount <= 1 :
                MusicBackground.TextureFrameCount == 32 && MusicBackground.AnimationChannelCount == 36,
                "artwork textures and their shared phase clock continue across tabs " + page);
        }
        else
            UiCheck(MusicBackground.AnimationChannelCount == 0,
                "motion or accessibility restrictions leave no background animation channels running " + page);
    }

    private void UiCheckAutoFullScreenCardGeometry(string prefix)
    {
        var grid = (Grid)AutoFullScreenToggle.Parent;
        var text = grid.Children.OfType<StackPanel>().Single();
        UiBounds textBounds = UiMeasure(text), toggleBounds = UiMeasure(AutoFullScreenToggle), cardBounds = UiMeasure((FrameworkElement)grid.Parent);
        var track = UiDescendants<FrameworkElement>(AutoFullScreenToggle).Single(element => element.Name == "OuterBorder");
        UiBounds trackBounds = UiMeasure(track);
        var card = (Border)grid.Parent;
        _uiTestLayouts.Add(new { phase = "automatic full-screen settings card", prefix, textBounds, toggleBounds, trackBounds, cardBounds });
        UiCheck(textBounds.X + textBounds.Width <= toggleBounds.X + 1 &&
            toggleBounds.X + toggleBounds.Width <= cardBounds.X + cardBounds.Width + 1,
            "automatic full-screen settings text and switch do not overlap " + prefix);
        UiCheck(Math.Abs(trackBounds.X + trackBounds.Width - (cardBounds.X + cardBounds.Width - card.Padding.Right - card.BorderThickness.Right)) <= 1 &&
            Math.Abs(trackBounds.CenterY - cardBounds.CenterY) <= 1,
            "native full-screen switch track aligns to the card right inset and vertical center " + prefix);
    }

    private void UiCheckMusicGeometry(string prefix)
    {
        var artworkBorder = (FrameworkElement)((FrameworkElement)MusicArtwork.Parent).Parent;
        UiBounds artwork = UiMeasure(artworkBorder), title = UiMeasure(MusicTitle), artist = UiMeasure(MusicArtist),
            album = UiMeasure(MusicAlbum), progress = UiMeasure(MusicProgress), elapsed = UiMeasure(MusicElapsed),
            previous = UiMeasure(MusicPreviousButton), playPause = UiMeasure(MusicPlayPauseButton), next = UiMeasure(MusicNextButton);
        UiBounds content = UiMeasure(MusicContent), layout = UiMeasure(MusicLayout), backdrop = UiMeasure(MusicBackground), window = UiMeasure(WindowLayout);
        _uiTestLayouts.Add(new { phase = "music geometry", prefix, artwork, title, artist, album, progress, elapsed, previous, playPause, next });
        _uiTestLayouts.Add(new { phase = "music centering and app-wide backdrop", prefix, content, layout, backdrop, window });
        UiCheck(Math.Abs(content.X + content.Width / 2 - layout.X - layout.Width / 2) <= 1 &&
            Math.Abs(content.CenterY - layout.CenterY) <= 1,
            "music content centers within the page excluding the navigation region " + prefix);
        UiCheck(Math.Abs(backdrop.X - window.X) <= 1 && Math.Abs(backdrop.Y - window.Y) <= 1 &&
            Math.Abs(backdrop.Width - window.Width) <= 1 && Math.Abs(backdrop.Height - window.Height) <= 1,
            "music backdrop spans the entire app including title bar and navigation " + prefix);
        UiCheck(MusicTitle.ActualWidth > 0 && MusicProgress.ActualWidth > 0 &&
            MusicProgress.ActualWidth <= MusicPanel.ViewportWidth + 1, "music content fits its horizontal viewport " + prefix);
        UiCheck(artwork.Y + artwork.Height <= title.Y + 1 && title.Y + title.Height <= artist.Y + 1 &&
            artist.Y + artist.Height <= album.Y + 1 && album.Y + album.Height <= progress.Y + 1 &&
            progress.Y + progress.Height <= elapsed.Y + 1,
            "music artwork metadata and progress do not overlap " + prefix);
        if (MusicTransportControls.Visibility == Visibility.Visible)
        {
            UiCheck(elapsed.Y + elapsed.Height <= Math.Min(previous.Y, Math.Min(playPause.Y, next.Y)) + 1 &&
                previous.X + previous.Width <= playPause.X + 1 && playPause.X + playPause.Width <= next.X + 1,
                "music transport controls do not overlap text or one another " + prefix);
            UiCheck(previous.Width >= 44 && previous.Height >= 44 && playPause.Width >= 52 && playPause.Height >= 52 &&
                next.Width >= 44 && next.Height >= 44, "music transport controls retain their touch target sizes " + prefix);
        }
        else
        {
            UiBounds progressGroup = UiMeasure((FrameworkElement)MusicProgress.Parent), disconnect = UiMeasure(MusicDisconnectButton);
            UiCheck(MusicControlHint.Visibility == Visibility.Collapsed &&
                Math.Abs(disconnect.Y - progressGroup.Y - progressGroup.Height - MusicContent.Spacing) <= 1,
                "unsupported transport and hint leave no empty block below music progress " + prefix);
        }
    }
}
#endif
