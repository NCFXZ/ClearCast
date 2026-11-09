#if CLEARCAST_UI_TESTS
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Graphics;
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
        try
        {
            // Keep the native host alive for its normal control/connection UI,
            // but never let an idle native snapshot replace synthetic metadata.
            _receiverTimer.Stop();
            ReceiverInfoBar.IsOpen = false;
            _preferences = _preferences with { AutoFullScreen = false };
            foreach (bool english in new[] { false, true })
            foreach (bool dark in new[] { false, true })
            {
                await UiNavigateAsync(SettingsNavigationItem, SettingsPage);
                LanguageButton.SelectedItem = english ? EnglishLanguageItem : ChineseLanguageItem;
                ThemeButton.SelectedItem = dark ? DarkThemeItem : LightThemeItem;
                await Task.Delay(150);
                UiCheck(Texts.IsEnglish == english && WindowLayout.ActualTheme == (dark ? ElementTheme.Dark : ElementTheme.Light),
                    "store capture applies its explicit locale and theme " + english + " " + dark);
                AppWindow.Resize(new SizeInt32(1920, 1080));
                await UiNavigateAsync(MirrorNavigationItem, MirrorPage);
                string locale = english ? "en-US" : "zh-CN";
                string theme = dark ? "dark" : "light";
                string prefix = "store-" + locale + "-" + theme;
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
                UiCheck(UiGetWindowRect(window, out var bounds) && bounds.Width == 1920 && bounds.Height == 1080,
                    "store owned window has the requested full-HD capture size " + prefix);
                await UiTestOwnedCompositorCaptureAsync(output, prefix);
                string captured = Path.Combine(output, prefix + "-music-window-1.png");
                UiCheck(File.Exists(captured), "store screenshot includes the actual owned-window compositor " + prefix);
                string destination = Path.Combine(storeOutput, locale + "-music-" + theme + ".png");
                File.Copy(captured, destination, overwrite: true);
                captures.Add(new
                {
                    locale, theme, file = destination, width = bounds.Width, height = bounds.Height,
                    sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destination))),
                    title = music.Title, artist = music.Artist, album = music.Album,
                    controls = "none", sourceFrame = captured
                });
            }
            var provenance = new
            {
                artwork = artworkPath, artworkSha256 = artworkHash,
                source = "Original geometric artwork supplied by release QA; fictional demonstration track and album names. No third-party album art or recording.",
                captureMethod = "PrintWindow(PW_RENDERFULLCONTENT=2), restricted to the test process's own HWND; exact unedited application frame.",
                assemblyVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                syntheticMedia = true, remoteCommandsSent = false,
                locales = "zh-CN for Chinese; en-US artwork and screenshots for other Store languages.",
                captures
            };
            await File.WriteAllTextAsync(Path.Combine(storeOutput, "provenance.json"),
                JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
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
            Texts.SetEnglish(english);
            WindowLayout.Language = english ? "en-US" : "zh-CN";
            ApplyAppTheme();
            RefreshComboBoxLabels();
            RefreshDynamicText();
            SetReceiverStatus(previousStatus);
            ReceiverInfoBar.IsOpen = previousErrorOpen;
            _receiverReady = previousReady;
            _preferences.Save(_root);
            if (havePlacement) SetWindowPlacement(window, in previousPlacement);
            if (previousNavigationItem is Microsoft.UI.Xaml.Controls.NavigationViewItem item && previousPage is not null)
                await UiNavigateAsync(item, previousPage);
            if (previousPolling) _receiverTimer.Start();
        }
    }
}
#endif
