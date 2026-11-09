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
            UiCheck(MusicBackground.TextureFrameCount == 32 && MusicBackground.AnimationChannelCount == 36,
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
