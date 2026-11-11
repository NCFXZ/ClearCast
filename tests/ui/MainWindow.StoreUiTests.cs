#if CLEARCAST_UI_TESTS
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace ClearCast;

public sealed partial class MainWindow
{
    private async Task UiTestStoreMusicScreenshotsAsync(string output)
    {
        string? artworkPath = Environment.GetEnvironmentVariable("CLEARCAST_UI_STORE_ARTWORK");
        if (string.IsNullOrWhiteSpace(artworkPath))
            return;
        UiCheck(_receiver is not null && _receiverReady && !_hasVideo && !_hasMusic && !_playerFullScreen,
            "store music captures use the real isolated receiver with idle synthetic media");
        var receiver = _receiver;
        artworkPath = Path.GetFullPath(artworkPath);
        var artworkInfo = new FileInfo(artworkPath);
        UiCheck(artworkInfo.Exists && artworkInfo.Length is > 0 and <= 2 * 1024 * 1024,
            "store artwork is an explicitly supplied bounded original QA fixture");
        byte[] artwork = await File.ReadAllBytesAsync(artworkPath);
        string artworkHash = Convert.ToHexString(SHA256.HashData(artwork));
        string storeOutput = Path.Combine(output, "store-music");
        Directory.CreateDirectory(storeOutput);
        var captures = new List<object>();
        var previousPreferences = _preferences;
        var previousMedia = _mediaState;
        string previousStatus = _receiverStatus;
        bool previousReady = _receiverReady;
        bool previousPolling = _receiverTimer.IsRunning;
        bool previousSettingsReady = _settingsReady;
        bool previousErrorOpen = ReceiverInfoBar.IsOpen;
        object? previousNavigationItem = MainNavigation.SelectedItem;
        var previousPage = _selectedPage;
        nint window = WindowNative.GetWindowHandle(this);
        var previousPlacement = new PlayerWindowPlacement { Length = Marshal.SizeOf<PlayerWindowPlacement>() };
        bool havePlacement = GetWindowPlacement(window, ref previousPlacement);
        AppWindowPresenterKind previousPresenter = AppWindow.Presenter.Kind;
        bool previousTitleBarExtension = ExtendsContentIntoTitleBar;
        bool diagnosticOnly = Environment.GetEnvironmentVariable("CLEARCAST_UI_STORE_DIAGNOSTIC_ONLY") == "1";
        try
        {
            // Keep the native host alive for its normal control/connection UI,
            // but never let an idle native snapshot replace synthetic metadata.
            _receiverTimer.Stop();
            ReceiverInfoBar.IsOpen = false;
            _preferences = _preferences with { AutoFullScreen = false, ShowLyrics = false };
            Texts.SetReceiverName("ClearCast PC");
            // Store screenshots fill the display without capturing any desktop
            // or changing the production player's normal fullscreen controls.
            // The XAML AppTitleBar stays in the page. Disable only the window's
            // extended-caption rendering while testing the full-display capture.
            ExtendsContentIntoTitleBar = false;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            await Task.Delay(150);
            var displayBounds = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).OuterBounds;
            UiNativeRect CheckMonitorCoverage(string page)
            {
                var visible = UiCompositorVisibleBounds(window, out _);
                UiCheck(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen &&
                    !ExtendsContentIntoTitleBar && AppTitleBar.Visibility == Visibility.Visible &&
                    visible.Left == displayBounds.X && visible.Top == displayBounds.Y &&
                    visible.Width == displayBounds.Width && visible.Height == displayBounds.Height,
                    "store owned window exactly covers the native monitor in position and dimensions " + page);
                return visible;
            }
            async Task CapturePage(string locale, string theme, string page)
            {
                await UiWaitForStableLayout(page == "settings" ? SettingsPage : MirrorPage);
                await Task.Delay(150);
                CheckMonitorCoverage(page);
                var frame = await Task.Run(() => UiCaptureOwnedWindow(window));
                UiCheck(frame.Width == displayBounds.Width && frame.Height == displayBounds.Height,
                    "store page captures the owned fullscreen display at native dimensions " + page);
                string name = locale + "-" + page + "-" + theme;
                await UiSaveCompositorFrameAsync(storeOutput, name, frame);
                string file = Path.Combine(storeOutput, name + ".png");
                captures.Add(new { locale, theme, page, file, width = frame.Width, height = frame.Height, fullscreen = true,
                    extendsContentIntoTitleBar = ExtendsContentIntoTitleBar,
                    windowLayout = UiMeasure(WindowLayout), titleBar = UiMeasure(AppTitleBar),
                    sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))) });
            }
            foreach (bool english in diagnosticOnly ? new[] { true } : new[] { false, true })
            foreach (bool dark in diagnosticOnly ? new[] { true } : new[] { false, true })
            {
                ApplyMediaState(ReceiverMediaState.Empty);
                SetReceiverStatus("等待 AirPlay 连接");
                await UiNavigateAsync(SettingsNavigationItem, SettingsPage);
                LanguageButton.SelectedItem = english ? EnglishLanguageItem : ChineseLanguageItem;
                ThemeButton.SelectedItem = dark ? DarkThemeItem : LightThemeItem;
                await Task.Delay(150);
                UiCheck(Texts.IsEnglish == english && WindowLayout.ActualTheme == (dark ? ElementTheme.Dark : ElementTheme.Light),
                    "store capture applies its explicit locale and theme " + english + " " + dark);
                await UiNavigateAsync(MirrorNavigationItem, MirrorPage);
                string locale = english ? "en-US" : "zh-CN";
                string theme = dark ? "dark" : "light";
                string prefix = "store-" + locale + "-" + theme;
                await UiWaitUntil(() => LandingAnimation.LoadedFrameCount == SvgFrameAnimation.FrameCount,
                    "store waiting illustration finishes decoding", 15000);
                if (english) UiCheckEnglishVisibleText(MirrorPage, "store waiting " + prefix);
                await CapturePage(locale, theme, "waiting");
                var music = new ReceiverMediaState((ulong)(8_000 + (english ? 100 : 0) + (dark ? 1 : 0)),
                    ReceiverMediaKind.Audio, true, english ? "Drifting Light" : "流光",
                    "ClearCast Studio", english ? "Evening Sketches" : "暮色片段", 84_000, 234_000,
                    (ulong)(9_000 + (english ? 100 : 0) + (dark ? 1 : 0)), artwork.Length, MediaControls.None)
                { Connected = true, Session = 8_000 };
                ApplyMediaState(music);
                await ApplyMusicArtworkAsync(artwork, _artworkGeneration, receiver);
                MusicPanel.ChangeView(null, 0, null, true);
                await UiWaitForStableLayout(MusicPanel, MusicContent, MusicArtworkFrame, MusicTitle, MusicArtist, MusicAlbum, MusicProgress);
                await UiWaitUntil(() => MusicBackground.HasArtworkFlow && MusicBackground.TextureFrameCount == 32,
                    "store original artwork creates its real flowing texture " + prefix);
                UiCheck(MusicArtwork.Source is not null && MusicArtworkPlaceholder.Visibility == Visibility.Collapsed &&
                    MusicTransportControls.Visibility == Visibility.Collapsed && MusicControlHint.Visibility == Visibility.Collapsed,
                    "store music captures show the real unsupported-controls layout without invented sender capabilities " + prefix);
                UiCheck(MusicDisconnectButton.IsEnabled && _mediaState.Connected && MusicTitle.Text == music.Title &&
                    MusicArtist.Text == music.Artist && MusicAlbum.Text == music.Album,
                    "store captures contain only the fictional controlled music metadata " + prefix);
                UiCheckMusicGeometry(prefix);
                if (english) UiCheckEnglishVisibleText(MirrorPage, "store music " + prefix);
                var bounds = CheckMonitorCoverage("music");
                UiCheck(bounds.Width == displayBounds.Width && bounds.Height == displayBounds.Height,
                    "store owned visible frame fills the native display " + prefix);
                await UiTestOwnedCompositorCaptureAsync(output, prefix);
                string captured = Path.Combine(output, prefix + "-music-window-1.png");
                UiCheck(File.Exists(captured), "store screenshot includes the actual owned-window compositor " + prefix);
                string destination = Path.Combine(storeOutput, locale + "-music-" + theme + ".png");
                File.Copy(captured, destination, overwrite: true);
                captures.Add(new
                {
                    locale, theme, file = destination, width = bounds.Width, height = bounds.Height, fullscreen = true,
                    extendsContentIntoTitleBar = ExtendsContentIntoTitleBar,
                    windowLayout = UiMeasure(WindowLayout), titleBar = UiMeasure(AppTitleBar),
                    sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destination))),
                    title = music.Title, artist = music.Artist, album = music.Album,
                    controls = "none", sourceFrame = captured
                });
                await UiWaitUntil(() => !_lyricsPending && _lyricsTrack is not null,
                    "store synthetic track finishes its offline lyrics lookup " + prefix);
                UiCheck(_uiLyricsHandler is not null && !LyricsAvailable,
                    "store lyric demonstration starts from the isolated offline lookup " + prefix);
                try
                {
                    // Original demonstration lines are generated locally. No external lyrics
                    // service, recording or third-party song is used in Store screenshots.
                    ApplyMediaState(music with { Revision = music.Revision + 1 });
                    _lyrics = Lyrics.ParseSynced(string.Join('\n', english
                        ? ["[00:10.00]A blue horizon opens wide", "[00:35.00]We leave a little room for light",
                            "[01:00.00]The quiet colors find their way", "[01:20.00]We carry summer through the rain",
                            "[01:50.00]A drifting glow above the sea", "[02:15.00]Each little wave returns to me",
                            "[02:45.00]The evening draws another line", "[03:20.00]And leaves a softer sky behind"]
                        : new[] { "[00:10.00]把一缕晨光放进行囊", "[00:35.00]让远处的海映出晴朗",
                            "[01:00.00]风经过窗前留下回响", "[01:20.00]我们沿着微光慢慢走",
                            "[01:50.00]听云朵轻轻越过山岗", "[02:15.00]将此刻写成温柔篇章",
                            "[02:45.00]等晚霞铺满安静街巷", "[03:20.00]再把好梦交给月亮" }));
                    _preferences = _preferences with { ShowLyrics = true };
                    UpdateLyricsPresentation(true);
                    await UiWaitForStableLayout(MusicPanel, MusicContent, MusicLyrics, MusicArtworkFrame);
                    await Task.Delay(1600);
                    UpdateLyricsPosition();
                    UiCheck(!_lyricsFailed && _lyricsOpen && MusicLyricsButton.IsEnabled && MusicLyricsButton.IsChecked == true &&
                        MusicLyrics.Visibility == Visibility.Visible && MusicLyrics.ActiveLineIndex == 3,
                        "store original synced lyrics show the active line through the real presentation path " + prefix);
                    UiBounds playerBounds = UiMeasure(MusicContent), lyricsBounds = UiMeasure(MusicLyrics);
                    UiCheck(lyricsBounds.Width > 0 && lyricsBounds.Height > 120 &&
                        (MusicPanel.ViewportWidth >= MusicLyricsSideBySideWidth
                            ? lyricsBounds.X >= playerBounds.X + playerBounds.Width
                            : lyricsBounds.Y >= playerBounds.Y + playerBounds.Height),
                        "store lyrics and player do not overlap " + prefix);
                    if (english) UiCheckEnglishVisibleText(MirrorPage, "store lyrics " + prefix);
                    await CapturePage(locale, theme, "lyrics");
                }
                finally
                {
                    _preferences = _preferences with { ShowLyrics = false };
                    ResetLyrics();
                    UpdateLyricsPresentation(false);
                }
                UiCheck(!_lyricsOpen && !_lyricsClosing && MusicLyrics.Visibility == Visibility.Collapsed,
                    "store lyrics are cleared and collapsed before the settings capture " + prefix);
                await UiNavigateAsync(SettingsNavigationItem, SettingsPage);
                if (english) UiCheckEnglishVisibleText(SettingsPage, "store settings " + prefix);
                await CapturePage(locale, theme, "settings");
            }
            var provenance = new
            {
                artwork = artworkPath, artworkSha256 = artworkHash,
                source = "Original geometric artwork supplied by release QA; fictional demonstration track and album names. No third-party album art or recording.",
                lyricsSource = "Original eight-line Chinese and English demonstration verses generated offline in the Store UI harness; no third-party lyrics, remote lookup or recording.",
                captureMethod = "PrintWindow(PW_RENDERFULLCONTENT=2), restricted to the test process's fullscreen HWND filling the native display; exact DWM visible-frame pixels without desktop capture, resampling or painting.",
                assemblyVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                syntheticMedia = true, remoteCommandsSent = false,
                captureTitleBarExtension = ExtendsContentIntoTitleBar,
                previousTitleBarExtension,
                diagnosticOnly,
                borderTreatment = "Only the owned fullscreen Store capture disables ExtendsContentIntoTitleBar; the XAML title bar is retained and the original window setting is restored. No HWND style or DWM border override and no PNG pixel modification.",
                monitor = new { displayBounds.X, displayBounds.Y, displayBounds.Width, displayBounds.Height },
                locales = "zh-CN for Chinese; en-US artwork and screenshots for other Store languages.",
                captures
            };
            await File.WriteAllTextAsync(Path.Combine(storeOutput, "provenance.json"),
                JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            ExtendsContentIntoTitleBar = previousTitleBarExtension;
            _receiverTimer.Stop();
            _musicActivityTimer?.Stop();
            ApplyMediaState(previousMedia);
            _settingsReady = false;
            try
            {
                _preferences = previousPreferences;
                ThemeButton.SelectedItem = previousPreferences.Theme switch
                {
                    AppTheme.Light => LightThemeItem, AppTheme.Dark => DarkThemeItem, _ => SystemThemeItem
                };
                LanguageButton.SelectedItem = previousPreferences.Language == AppLanguage.English ? EnglishLanguageItem : ChineseLanguageItem;
            }
            finally { _settingsReady = previousSettingsReady; }
            bool english = previousPreferences.Language == AppLanguage.English;
            Texts.SetReceiverName(NativeReceiver.AdvertisedName);
            Texts.SetEnglish(english);
            WindowLayout.Language = english ? "en-US" : "zh-CN";
            ApplyAppTheme();
            RefreshComboBoxLabels();
            RefreshDynamicText();
            SetReceiverStatus(previousStatus);
            ReceiverInfoBar.IsOpen = previousErrorOpen;
            _receiverReady = previousReady;
            _preferences.Save(_root);
            AppWindow.SetPresenter(previousPresenter);
            if (havePlacement) SetWindowPlacement(window, in previousPlacement);
            if (previousNavigationItem is Microsoft.UI.Xaml.Controls.NavigationViewItem item && previousPage is not null)
                await UiNavigateAsync(item, previousPage);
            if (previousPolling) _receiverTimer.Start();
        }
    }
}
#endif
