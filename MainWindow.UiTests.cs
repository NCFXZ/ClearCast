#if CLEARCAST_UI_TESTS
// Compiled only by -p:EnableUiTests=true; the release has no test entry point.
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace ClearCast;

public sealed partial class MainWindow
{
    private readonly List<string> _uiTestChecks = [];
    private readonly List<object> _uiTestLayouts = [];
    private readonly List<string> _uiTestScreenshots = [];
    private readonly List<string> _uiTestCaptureErrors = [];

    internal static bool UiTestsAreConfigured()
    {
        string? root = Environment.GetEnvironmentVariable("CLEARCAST_HOME");
        return Environment.GetEnvironmentVariable("CLEARCAST_UI_TEST_RUN") == "1" &&
            !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) &&
            File.Exists(Path.Combine(root, "clearcast-ui-test-state.marker"));
    }

    internal void StartUiTests() => DispatcherQueue.TryEnqueue(async () => await RunUiTestsAsync());

    private void UiCheck(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _uiTestChecks.Add(name);
    }

    private static async Task UiWaitUntil(Func<bool> condition, string name, int timeoutMilliseconds = 10000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds >= timeoutMilliseconds) throw new TimeoutException(name);
            await Task.Delay(40);
        }
    }

    private static IEnumerable<T> UiDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); ++index)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T result) yield return result;
            foreach (var descendant in UiDescendants<T>(child)) yield return descendant;
        }
    }

    private static void UiInvoke(Button button)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("No invoke pattern for " + button.Name);
        invoke.Invoke();
    }

    private sealed record UiBounds(double X, double Y, double Width, double Height)
    {
        public double CenterY => Y + Height / 2;
    }

    private UiBounds UiMeasure(FrameworkElement element)
    {
        var rect = element.TransformToVisual(WindowLayout).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        return new(rect.X, rect.Y, rect.Width, rect.Height);
    }

    private async Task UiWaitForStableLayout(params FrameworkElement[] elements)
    {
        UiBounds[]? previous = null;
        int stableSamples = 0;
        await UiWaitUntil(() =>
        {
            var current = elements.Select(UiMeasure).ToArray();
            stableSamples = previous is not null && previous.SequenceEqual(current) ? stableSamples + 1 : 0;
            previous = current;
            return stableSamples >= 4;
        }, "stable layout geometry", 5000);
    }

    private async Task UiCaptureAsync(string folder, string name)
    {
        Brush originalBackground = WindowLayout.Background;
        Border? resourceProbe = null;
        try
        {
            // RenderTargetBitmap does not capture the DWM/Mica backdrop. Resolve an
            // opaque theme resource in this window for the test capture only.
            resourceProbe = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource SolidBackgroundFillColorBaseBrush}\" />");
            Grid.SetRowSpan(resourceProbe, WindowLayout.RowDefinitions.Count);
            WindowLayout.Children.Insert(0, resourceProbe);
            await Task.Delay(40);
            WindowLayout.Background = resourceProbe.Background;
            WindowLayout.Children.Remove(resourceProbe);
            resourceProbe = null;
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(WindowLayout);
            var buffer = await bitmap.GetPixelsAsync();
            byte[] bytes = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
            var directory = await StorageFolder.GetFolderFromPathAsync(folder);
            var file = await directory.CreateFileAsync(name + ".png", CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
            await encoder.FlushAsync();
            _uiTestScreenshots.Add(file.Path);
        }
        catch (Exception error)
        {
            _uiTestCaptureErrors.Add(name + ": " + error.Message);
        }
        finally
        {
            if (resourceProbe is not null) WindowLayout.Children.Remove(resourceProbe);
            WindowLayout.Background = originalBackground;
        }
    }

    private async Task UiNavigateAsync(NavigationViewItem item, FrameworkElement page)
    {
        MainNavigation.SelectedItem = item;
        await UiWaitUntil(() => ReferenceEquals(_selectedPage, page), "page selection");
        await Task.Delay(500);
        await UiWaitForStableLayout(page);
        UiCheck(page.XamlRoot is not null && page.ActualWidth > 0, "visible page " + item.Tag);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UiNativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiGetWindowRect(nint window, out UiNativeRect rect);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiIsWindowVisible(nint window);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiPostMessage(nint window, uint message, nuint wparam, nint lparam);

    [DllImport("user32.dll", EntryPoint = "GetWindowRgn", ExactSpelling = true)]
    private static extern int UiGetWindowRgn(nint window, nint region);

    [DllImport("gdi32.dll", EntryPoint = "CreateRectRgn", ExactSpelling = true)]
    private static extern nint UiCreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", EntryPoint = "GetRgnBox", ExactSpelling = true)]
    private static extern int UiGetRgnBox(nint region, out UiNativeRect rect);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiDeleteObject(nint value);

    private async Task UiTestPlayerWindowPathsAsync(string output)
    {
        // Exercise the real UI and this process's native video-host window, using
        // synthetic frame dimensions only. No encoded or decoded video is supplied.
        // This does not establish media quality, aspect ratio inside GStreamer,
        // audio, frame rotation, or sender orientation handling.
        await UiNavigateAsync(MirrorNavigationItem, MirrorPage);
        var previousPreferences = _preferences;
        try
        {
            UiCheck(_receiver is null && _videoHost is not null, "player window test uses stopped receiver");
            SetPlayerFrameSize(320, 180);
            SetVideoAvailable(true);
            await UiWaitForStableLayout(VideoHostSlot, PlayerOverlay);
            DisplaySizeButton.SelectedItem = OriginalDisplayItem;
            UpdateVideoHost();
            UiCheck(UiGetWindowRect(_videoHost!.Handle, out var actualSmall), "read owned player HWND geometry");
            _uiTestLayouts.Add(new { playerWindowPhase = "actual size", syntheticWidth = 320, syntheticHeight = 180,
                actualWidth = actualSmall.Width, actualHeight = actualSmall.Height });
            UiCheck(actualSmall.Width == 320 && actualSmall.Height == 180, "actual-size host uses frame pixel dimensions");

            DisplaySizeButton.SelectedItem = FitDisplayItem;
            UpdateVideoHost();
            double scale = VideoHostSlot.XamlRoot.RasterizationScale;
            var fitSlot = GetVideoSlotBounds(scale) ?? throw new InvalidOperationException("Player slot missing.");
            UiCheck(UiGetWindowRect(_videoHost.Handle, out var actualFit), "read fit-window HWND geometry");
            _uiTestLayouts.Add(new { playerWindowPhase = "fit host to slot", slotWidth = fitSlot.Width, slotHeight = fitSlot.Height,
                actualWidth = actualFit.Width, actualHeight = actualFit.Height });
            UiCheck(actualFit.Width == fitSlot.Width && actualFit.Height == fitSlot.Height, "fit host fills the UI video slot");

            SetPlayerFrameSize(3840, 2160);
            DisplaySizeButton.SelectedItem = OriginalDisplayItem;
            UpdateVideoHost();
            UiCheck(UiGetWindowRect(_videoHost.Handle, out var actualLarge) && actualLarge.Width == 3840 && actualLarge.Height == 2160,
                "large actual-size host retains frame dimensions");
            nint region = UiCreateRectRgn(0, 0, 0, 0);
            try
            {
                UiCheck(region != 0 && UiGetWindowRgn(_videoHost.Handle, region) > 0, "oversized host has a native clip region");
                UiCheck(UiGetRgnBox(region, out var clip) > 0 && clip.Width <= fitSlot.Width && clip.Height <= fitSlot.Height,
                    "oversized host clip stays inside the video slot");
            }
            finally { if (region != 0) UiDeleteObject(region); }

            DisplaySizeButton.SelectedItem = FitDisplayItem;
            UpdateVideoHost();
            UiCheck(!RotateLeftButton.IsEnabled && !RotateRightButton.IsEnabled, "rotation stays disabled without a receiver");
            nint window = WindowNative.GetWindowHandle(this);
            UiCheck(UiGetWindowRect(window, out var beforeFullScreen), "record window bounds before full screen");
            UiInvoke(FullScreenButton);
            await UiWaitUntil(() => _playerFullScreen, "enter full screen through player button");
            await UiWaitForStableLayout(VideoHostSlot);
            UiCheck(AppTitleBar.Visibility == Visibility.Collapsed && !MainNavigation.IsPaneVisible &&
                FullScreenIcon.Symbol == Symbol.BackToWindow, "full-screen presenter hides app chrome");
            UiCheck(Texts.ExitFullScreen == "Exit full screen", "full-screen action label translated");
            UpdateVideoHost();
            await UiWaitUntil(() => UiIsWindowVisible(_videoHost.Handle), "full-screen host becomes visible");
            await UiCaptureAsync(output, "synthetic-player-ui-fullscreen");

            // Queue Escape to this test window. The existing NativeVideoHost hook
            // observes its own HWND thread and raises the real EscapePressed event.
            _videoHost.RefreshInputHooks();
            UiCheck(UiPostMessage(window, 0x0100, 0x1B, 0), "queue Escape to own test window");
            await UiWaitUntil(() => !_playerFullScreen, "Escape restores the window through native input hook");
            await UiWaitForStableLayout(VideoHostSlot);
            UiCheck(UiGetWindowRect(window, out var afterFullScreen), "read restored window bounds");
            _uiTestLayouts.Add(new { playerWindowPhase = "full screen and Escape", beforeWidth = beforeFullScreen.Width,
                beforeHeight = beforeFullScreen.Height, afterWidth = afterFullScreen.Width, afterHeight = afterFullScreen.Height });
            UiCheck(beforeFullScreen.Width == afterFullScreen.Width && beforeFullScreen.Height == afterFullScreen.Height,
                "Escape restores previous window dimensions");
            UiCheck(AppTitleBar.Visibility == Visibility.Visible && MainNavigation.IsPaneVisible &&
                FullScreenIcon.Symbol == Symbol.FullScreen, "Escape restores chrome and full-screen action");
        }
        finally
        {
            SetVideoAvailable(false);
            _preferences = previousPreferences;
            DisplaySizeButton.SelectedItem = previousPreferences.OriginalSize ? OriginalDisplayItem : FitDisplayItem;
            _preferences.Save(_root);
        }
        UiCheck(!_playerFullScreen && !_hasVideo && !_playerOverlayTimer.IsRunning,
            "synthetic player cleanup stops overlay polling and hides video");
    }

    private async Task RunUiTestsAsync()
    {
        string? configuredRoot = Environment.GetEnvironmentVariable("CLEARCAST_HOME");
        string output = Path.Combine(_root, "ui-test-output");
        var elapsed = Stopwatch.StartNew();
        string? failure = null;
        bool receiverStopped = false;
        try
        {
            // A test build refuses to run against the user's usual app state.
            if (string.IsNullOrWhiteSpace(configuredRoot) ||
                Environment.GetEnvironmentVariable("CLEARCAST_UI_TEST_RUN") != "1" ||
                !File.Exists(Path.Combine(_root, "clearcast-ui-test-state.marker")))
                throw new InvalidOperationException("An explicitly marked disposable CLEARCAST_HOME is required.");
            Directory.CreateDirectory(output);
            await UiWaitUntil(() => WindowLayout.XamlRoot is not null, "window loading");
            await UiWaitUntil(() => _receiverReady || ReceiverInfoBar.IsOpen, "receiver startup");
            UiCheck(_receiverReady, "receiver starts successfully");
            UiCheck(await StopReceiverAsync(), "receiver stops without blocking the window");
            receiverStopped = true;

            await UiNavigateAsync(SettingsNavigationItem, SettingsPage);
            foreach (var decoder in new[] { SoftwareDecoderItem, HardwareDecoderItem, AutoDecoderItem })
            {
                DecoderButton.SelectedItem = decoder;
                await Task.Delay(40);
                UiCheck(ReferenceEquals(DecoderButton.SelectedItem, decoder), "decoder preference " + decoder.Name);
            }
            DisplaySizeButton.SelectedItem = OriginalDisplayItem;
            UiCheck(_preferences.OriginalSize, "actual-size preference");
            DisplaySizeButton.SelectedItem = FitDisplayItem;
            UiCheck(!_preferences.OriginalSize, "fit-window preference");
            UiCheck(ReceiverPreferences.Load(_root) == _preferences, "settings persisted");

            foreach (bool english in new[] { false, true })
            {
                LanguageButton.SelectedItem = english ? EnglishLanguageItem : ChineseLanguageItem;
                await Task.Delay(80);
                UiCheck(Texts.IsEnglish == english, "language selection " + english);
                UiCheck((string?)AutoDecoderItem.Content == Texts.AutomaticDecoder, "decoder label refreshed " + english);
                UiCheck((string?)FitDisplayItem.Content == Texts.FitWindow, "display label refreshed " + english);
                foreach (bool dark in new[] { false, true })
                {
                    ThemeButton.SelectedItem = dark ? DarkThemeItem : LightThemeItem;
                    await Task.Delay(80);
                    UiCheck(WindowLayout.ActualTheme == (dark ? ElementTheme.Dark : ElementTheme.Light), "theme applied " + dark);
                    foreach (int logicalWidth in new[] { 640, 1120 })
                    {
                        double scale = WindowLayout.XamlRoot.RasterizationScale;
                        AppWindow.Resize(new SizeInt32((int)Math.Round(logicalWidth * scale), (int)Math.Round(720 * scale)));
                        await Task.Delay(150);
                        string prefix = (english ? "en" : "zh") + "-" + (dark ? "dark" : "light") + "-" + logicalWidth;
                        await UiNavigateAsync(SettingsNavigationItem, SettingsPage);
                        UiCheck(ThemeButton.ActualWidth > 0 && ThemeButton.ActualWidth <= SettingsLayout.ActualWidth,
                            "settings selector fits " + prefix);
                        if (english)
                            foreach (var text in UiDescendants<TextBlock>(SettingsPage))
                                if (text.Text != Texts.ChineseLanguage)
                                    UiCheck(!Regex.IsMatch(text.Text ?? "", "[\\p{IsCJKUnifiedIdeographs}]"), "English settings text " + text.Text);
                        await UiCaptureAsync(output, prefix + "-settings");

                        await UiNavigateAsync(AboutNavigationItem, AboutPage);
                        UiCheck(AboutVersionLabel.Text == Texts.AboutVersion, "about version " + prefix);
                        await UiCaptureAsync(output, prefix + "-about");
                        await UiNavigateAsync(MirrorNavigationItem, MirrorPage);
                        ShowReceiveError("设备验证未通过，请在苹果设备上重新选择 ClearCast 后重试。", 470);
                        await Task.Delay(80);
                        var icon = UiDescendants<Grid>(ReceiverInfoBar).Single(element => element.Name == "ErrorIcon");
                        var close = UiDescendants<Button>(ReceiverInfoBar).Single(element => element.Name == "CloseButton");
                        await UiWaitForStableLayout(ReceiverInfoBar, icon, close, CopyErrorButton);
                        UiBounds iconBounds = UiMeasure(icon), closeBounds = UiMeasure(close), copyBefore = UiMeasure(CopyErrorButton);
                        double Pixel(double value) => Math.Round(value * scale, MidpointRounding.AwayFromZero);
                        _uiTestLayouts.Add(new
                        {
                            prefix, phase = "before checks", scale, iconBounds, closeBounds, copyBefore,
                            logicalCloseWidth = close.ActualWidth, logicalCloseHeight = close.ActualHeight,
                            logicalCopyHeight = CopyErrorButton.ActualHeight,
                            physicalCloseWidth = Pixel(closeBounds.Width), physicalCloseHeight = Pixel(closeBounds.Height),
                            physicalCopyHeight = Pixel(copyBefore.Height)
                        });
                        await UiCaptureAsync(output, prefix + "-error-before-copy");
                        UiCheck(Math.Abs(iconBounds.CenterY - copyBefore.CenterY) <= 1, "error icon centered " + prefix);
                        UiCheck(Math.Abs(closeBounds.CenterY - copyBefore.CenterY) <= 1, "close centered " + prefix);
                        // TransformToVisual uses single-precision matrices: widths
                        // derived at large X positions can differ by a few float ULPs.
                        // Require exact logical dimensions and exact rendered pixel
                        // dimensions instead of comparing transformed doubles bitwise.
                        UiCheck(close.ActualWidth == CopyErrorButton.ActualHeight && close.ActualHeight == CopyErrorButton.ActualHeight,
                            "logical close square matches copy height " + prefix);
                        UiCheck(Pixel(closeBounds.Width) == Pixel(copyBefore.Height) && Pixel(closeBounds.Height) == Pixel(copyBefore.Height),
                            "close square matches copy height " + prefix);
                        UiCheck(close.Padding.Equals(new Thickness(0)), "close glyph has space " + prefix);
                        UiInvoke(CopyErrorButton);
                        await Task.Delay(80);
                        UiBounds copyAfter = UiMeasure(CopyErrorButton);
                        UiCheck(Pixel(copyBefore.X) == Pixel(copyAfter.X) && Pixel(copyBefore.Y) == Pixel(copyAfter.Y) &&
                            Pixel(copyBefore.Width) == Pixel(copyAfter.Width) && Pixel(copyBefore.Height) == Pixel(copyAfter.Height),
                            "copy feedback preserves bounds " + prefix);
                        UiCheck((string?)CopyErrorButton.Content == Texts.Translate(_copyErrorLabel), "copy feedback language " + prefix);
                        _uiTestLayouts.Add(new { prefix, scale, iconBounds, closeBounds, copyBefore, copyAfter });
                        await UiCaptureAsync(output, prefix + "-error-after-copy");
                        UiInvoke(close);
                        await UiWaitUntil(() => !ReceiverInfoBar.IsOpen, "close error banner");
                        UiCheck(!ReceiverInfoBar.IsOpen, "close error banner " + prefix);
                    }
                }
            }

            ShowPairingCode("1234");
            UiCheck(PinText.Text == "1234" && WaitingPairing.Visibility == Visibility.Visible, "pairing challenge shown");
            UiCheck(PairingPrompt.Text == "Enter these four digits on your device.", "pairing challenge translated");
            HidePairingCode();
            UiCheck(WaitingPairing.Visibility == Visibility.Collapsed, "pairing challenge hidden");
            for (int index = 0; index < 100; ++index)
                MainNavigation.SelectedItem = (index % 3) switch { 0 => SettingsNavigationItem, 1 => AboutNavigationItem, _ => MirrorNavigationItem };
            await Task.Delay(500);
            UiCheck(_pageHosts.Count <= 3 && NavigationFrame.BackStack.Count == 0, "100 navigations keep bounded page history");
            RotatePlayer(1);
            TogglePlayerFullScreen();
            UiCheck(!_playerFullScreen && !_hasVideo, "player controls ignore inactive playback");
            await UiTestPlayerWindowPathsAsync(output);
            await UiNavigateAsync(MirrorNavigationItem, MirrorPage);
            await UiCaptureAsync(output, "completed-mirror-page");
        }
        catch (Exception error)
        {
            failure = error.ToString();
        }
        finally
        {
            try { if (!receiverStopped) receiverStopped = await StopReceiverAsync(); }
            catch (Exception error) { failure ??= error.ToString(); }
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                status = failure is null ? "pass" : "fail", failure, elapsedMilliseconds = elapsed.ElapsedMilliseconds,
                checkCount = _uiTestChecks.Count, checks = _uiTestChecks, layouts = _uiTestLayouts,
                screenshots = _uiTestScreenshots, captureErrors = _uiTestCaptureErrors, receiverStopped,
                screenshotMethod = "RenderTargetBitmap with a temporary opaque SolidBackgroundFillColorBaseBrush; excludes DWM/Mica and native video surfaces.",
                playerWindowMethod = "Actual UI event handlers and this test process's own HWND geometry/presenter/Escape hook, with synthetic frame dimensions; no video or audio pipeline exercised.",
                limitation = "No synthetic test replaces physical iPhone/iPad/Mac video, audio, rotation or full-screen playback acceptance."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }
}
#endif
