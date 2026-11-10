#if CLEARCAST_UI_TESTS
// Compiled only by -p:EnableUiTests=true; the release has no test entry point.
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
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
    private readonly List<double> _uiTestHeartbeatTimesMilliseconds = [];
    private DispatcherQueueTimer? _uiTestHeartbeatTimer;

    internal static bool UiTestsAreConfigured()
    {
        string? root = Environment.GetEnvironmentVariable("CLEARCAST_HOME");
        return Environment.GetEnvironmentVariable("CLEARCAST_UI_TEST_RUN") == "1" &&
            !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) &&
            File.Exists(Path.Combine(root, "clearcast-ui-test-state.marker"));
    }

    internal void StartUiTests()
    {
        _uiTestHeartbeatTimer = DispatcherQueue.CreateTimer();
        _uiTestHeartbeatTimer.Interval = TimeSpan.FromMilliseconds(20);
        _uiTestHeartbeatTimer.Tick += (timer, _) =>
        {
            double time = LandingAnimation.StartupElapsedMilliseconds;
            _uiTestHeartbeatTimesMilliseconds.Add(time);
            if (time >= 10000 || _uiTestHeartbeatTimesMilliseconds.Count >= 500)
                timer.Stop();
        };
        _uiTestHeartbeatTimer.Start();
        DispatcherQueue.TryEnqueue(async () => await RunUiTestsAsync());
    }

    private void UiCheck(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _uiTestChecks.Add(name);
        File.AppendAllText(Path.Combine(_root, "ui-check-progress.log"), name + Environment.NewLine);
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

    private void UiCheckEnglishVisibleText(FrameworkElement root, string context)
    {
        foreach (var text in UiDescendants<TextBlock>(root))
        {
            // Receiver/device names are user content, and language names retain
            // their own language so people can find the language selector.
            if (text.Text == Texts.ChineseLanguage || ReferenceEquals(text, LandingDescription) ||
                text.DataContext is PairingDeviceRow)
                continue;
            bool visible = true;
            for (DependencyObject? element = text; element is not null; element = VisualTreeHelper.GetParent(element))
            {
                if (element is UIElement { Visibility: Visibility.Collapsed })
                {
                    visible = false;
                    break;
                }
                if (ReferenceEquals(element, root))
                    break;
            }
            if (visible)
                UiCheck(!Regex.IsMatch(text.Text ?? "", "[\\p{IsCJKUnifiedIdeographs}]"),
                    "English visible text " + context + " " + text.Text);
        }
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

    private void UiCheckSettingsCardGeometry(string prefix)
    {
        foreach (var selector in new[] { ThemeButton, LanguageButton, DecoderButton, DisplaySizeButton })
        {
            var grid = (Grid)selector.Parent;
            var card = (Border)grid.Parent;
            var text = grid.Children.OfType<StackPanel>().Single();
            UiBounds cardBounds = UiMeasure(card), gridBounds = UiMeasure(grid), textBounds = UiMeasure(text),
                selectorBounds = UiMeasure(selector);
            double top = gridBounds.Y - cardBounds.Y;
            double bottom = cardBounds.Y + cardBounds.Height - gridBounds.Y - gridBounds.Height;
            _uiTestLayouts.Add(new
            {
                prefix, phase = "settings card geometry", selector = selector.Name,
                row = Grid.GetRow(selector), cardBounds, gridBounds, textBounds, selectorBounds,
                contentTop = top, contentBottom = bottom
            });
            UiCheck(Math.Abs(top - bottom) <= 1, "settings content has equal vertical space " + selector.Name + " " + prefix);
            UiCheck(selector.ActualWidth >= 130 && selector.ActualHeight >= 32,
                "settings selector meets minimum action size " + selector.Name + " " + prefix);
            if (Grid.GetRow(selector) == 0)
                UiCheck(Math.Abs(textBounds.CenterY - selectorBounds.CenterY) <= 1,
                    "settings text and selector centered " + selector.Name + " " + prefix);
        }

        var header = UiDescendants<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(PairingExpander)
            .Single(element => element.Name == "ExpanderHeader");
        var headerGrid = (Grid)PairingExpander.Header;
        var headerText = headerGrid.Children.OfType<StackPanel>().Single();
        UiBounds headerBounds = UiMeasure(header), headerGridBounds = UiMeasure(headerGrid), headerTextBounds = UiMeasure(headerText);
        double headerTop = headerGridBounds.Y - headerBounds.Y;
        double headerBottom = headerBounds.Y + headerBounds.Height - headerGridBounds.Y - headerGridBounds.Height;
        _uiTestLayouts.Add(new
        {
            prefix, phase = "pairing header geometry", headerBounds, headerGridBounds, headerTextBounds,
            contentTop = headerTop, contentBottom = headerBottom
        });
        UiCheck(Math.Abs(headerTop - headerBottom) <= 1, "pairing header has equal vertical space " + prefix);
    }

    private async Task UiTestPairingFeedbackAsync()
    {
        string language = Texts.IsEnglish ? "en" : "zh";
        UiCheck(_receiver is null && !_hasVideo, "pairing feedback uses stopped receiver " + language);
        ReceiverInfoBar.IsOpen = false;
        HidePairingCode();
        try
        {
            HandlePairingEvent(new(ReceiverEventKind.PairingAuthenticationRequired, 0, 0, 0, 470));
            UiCheck(!ReceiverInfoBar.IsOpen && WaitingPairing.Visibility == Visibility.Collapsed,
                "unknown identity probe is neutral " + language);
            HandlePairingEvent(new(ReceiverEventKind.PairingRequired, 1234, 41, 0, 0));
            string normalPrompt = Texts.Translate("在设备上输入这四位数字。");
            UiCheck(PinText.Text == "1234" && PairingPrompt.Text == normalPrompt && !ReceiverInfoBar.IsOpen,
                "unknown identity probe followed by PIN keeps normal instruction " + language);
            await UiWaitForStableLayout(PinText, PairingCloseButton);
            UiBounds pinBounds = UiMeasure(PinText), dismissBounds = UiMeasure(PairingCloseButton);
            _uiTestLayouts.Add(new { phase = "localized pairing code geometry", language, pinBounds, dismissBounds });
            UiCheck(Math.Abs(pinBounds.CenterY - dismissBounds.CenterY) <= 1,
                "localized pairing digits and dismiss action align " + language);
            UiCheck(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(PairingCloseButton) == Texts.DismissPairingCode &&
                (string?)ToolTipService.GetToolTip(PairingCloseButton) == Texts.DismissPairingCode,
                "pairing dismiss accessibility and tooltip translated " + language);
            HandlePairingEvent(new(ReceiverEventKind.PairingAuthenticationRequired, 0, 0, 0, 470));
            UiCheck(PairingPrompt.Text == normalPrompt && PinText.Text == "1234" && !ReceiverInfoBar.IsOpen,
                "unassociated identity probe preserves active PIN " + language);

            HandlePairingEvent(new(ReceiverEventKind.PairingRequired, 5678, 42, 0, 0));
            HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 41, 0, (int)PairingRejectionReason.ProofRejected));
            HandlePairingEvent(new(ReceiverEventKind.PairingCompleted, 0, 41, 0, 0));
            UiCheck(PairingPrompt.Text == normalPrompt && PinText.Text == "5678" &&
                WaitingPairing.Visibility == Visibility.Visible && _pairingTimer.IsRunning && !ReceiverInfoBar.IsOpen,
                "old challenge rejection and completion preserve newer PIN " + language);
            HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 0, 0, (int)PairingRejectionReason.ProofRejected));
            HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 0, 0, (int)PairingRejectionReason.VerifyRejected));
            HandlePairingEvent(new(ReceiverEventKind.PairingCompleted, 0, 0, 0, 0));
            UiCheck(PairingPrompt.Text == normalPrompt && PinText.Text == "5678" &&
                WaitingPairing.Visibility == Visibility.Visible && !ReceiverInfoBar.IsOpen,
                "unassociated rejection and completion preserve correlated PIN " + language);
            HandlePairingEvent(new(ReceiverEventKind.ConnectionRejected, 0, 0, 0, 470));
            HandlePairingEvent(new(ReceiverEventKind.ConnectionRejected, 0, 41, 0, 400));
            UiCheck(PairingPrompt.Text == normalPrompt && PinText.Text == "5678" && !ReceiverInfoBar.IsOpen,
                "unassociated and older connection errors preserve active PIN " + language);
            HandlePairingEvent(new(ReceiverEventKind.ConnectionRejected, 0, 42, 0, 400));
            UiCheck(ReceiverInfoBar.IsOpen && _receiveErrorCode == 400,
                "current connection error remains visible during pairing " + language);
            ReceiverInfoBar.IsOpen = false;

            HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 42, 0, (int)PairingRejectionReason.ProofRejected));
            UiCheck(PairingPrompt.Text == normalPrompt && _pairingProofRejections == 1 &&
                PinText.Text == "5678" && _pairingTimer.IsRunning && !ReceiverInfoBar.IsOpen,
                "first current proof rejection preserves neutral PIN entry guidance " + language);
            string retryPrompt = Texts.Translate("配对验证未通过，请核对验证码后重试。");
            for (int rejection = 0; rejection < 2; rejection++)
            {
                HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 42, 0, (int)PairingRejectionReason.ProofRejected));
                UiCheck(PairingPrompt.Text == retryPrompt && _pairingProofRejections == 2 &&
                    PinText.Text == "5678" && _pairingTimer.IsRunning && !ReceiverInfoBar.IsOpen,
                    "repeated current proof rejections offer code checking without blaming the person " + language + " " + rejection);
            }
            HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 42, 0, (int)PairingRejectionReason.InvalidSetupData));
            UiCheck(PairingPrompt.Text == Texts.Translate("配对请求未完成，请在设备上重试。") && !ReceiverInfoBar.IsOpen,
                "current malformed pairing request explains retry " + language);
            foreach (var (reason, message) in new[]
            {
                (PairingRejectionReason.RateLimited, "配对尝试较多，请稍后在设备上重试。"),
                (PairingRejectionReason.PinUnavailableOrExpired, "验证码已失效，请在设备上重新选择 ClearCast 后重试。"),
                (PairingRejectionReason.VerifyRejected, "设备验证未通过，请在设备上重新选择 ClearCast 后重试。")
            })
            {
                HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 42, 0, (int)reason));
                string terminalPrompt = Texts.Translate(message);
                UiCheck(PairingPrompt.Text == terminalPrompt && _pairingTerminalFeedback && !ReceiverInfoBar.IsOpen,
                    "current terminal pairing feedback explains its action " + language + " " + reason);
                HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 42, 0, (int)PairingRejectionReason.ProofRejected));
                HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 42, 0, (int)PairingRejectionReason.InvalidSetupData));
                UiCheck(PairingPrompt.Text == terminalPrompt && _pairingTerminalFeedback,
                    "late retryable failures cannot overwrite terminal pairing feedback " + language + " " + reason);
            }
            HandlePairingEvent(new(ReceiverEventKind.PairingRequired, 9012, 43, 0, 0));
            UiCheck(PairingPrompt.Text == normalPrompt && PinText.Text == "9012" &&
                _pairingProofRejections == 0 && !_pairingTerminalFeedback && _pairingTimer.IsRunning,
                "a new challenge resets proof retries and terminal feedback " + language);
            HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 42, 0, (int)PairingRejectionReason.RateLimited));
            HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 43, 0, (int)PairingRejectionReason.ProofRejected));
            UiCheck(PairingPrompt.Text == normalPrompt && _pairingProofRejections == 1 && !_pairingTerminalFeedback,
                "a stale terminal event cannot consume the new challenge's neutral first retry " + language);
            HandlePairingEvent(new(ReceiverEventKind.PairingCompleted, 0, 43, 0, 0));
            UiCheck(WaitingPairing.Visibility == Visibility.Collapsed && PinText.Text.Length == 0 &&
                _pairingGeneration == 0 && _pairingProofRejections == 0 && !_pairingTerminalFeedback &&
                !_pairingTimer.IsRunning && !ReceiverInfoBar.IsOpen,
                "current challenge completion clears feedback state and stops timer " + language);

            HandlePairingEvent(new(ReceiverEventKind.ConnectionRejected, 0, 41, 0, 400));
            UiCheck(!ReceiverInfoBar.IsOpen,
                "old connection error stays ignored after current pairing completes " + language);
            HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 43, 0, (int)PairingRejectionReason.VerifyRejected));
            UiCheck(ReceiverInfoBar.IsOpen &&
                ReceiverInfoBar.Message == Texts.Translate("设备验证未通过，请在设备上重新选择 ClearCast 后重试。"),
                "actual signature verification rejection remains visible " + language);
            ReceiverInfoBar.IsOpen = false;
            HandlePairingEvent(new(ReceiverEventKind.ConnectionRejected, 0, 0, 0, 470));
            UiCheck(ReceiverInfoBar.IsOpen && _receiveErrorCode == 470,
                "connection authentication error remains visible without active PIN " + language);
            HandlePairingEvent(new(ReceiverEventKind.PairingRequired, 3456, 44, 0, 0));
            HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 44, 0, (int)PairingRejectionReason.ProofRejected));
            HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 44, 0, (int)PairingRejectionReason.RateLimited));
            HidePairingCode();
            UiCheck(_pairingGeneration == 0 && _pairingProofRejections == 0 && !_pairingTerminalFeedback && !_pairingTimer.IsRunning,
                "dismissing a challenge clears retry and terminal state " + language);
            HandlePairingEvent(new(ReceiverEventKind.PairingRequired, 7890, 45, 0, 0));
            HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 45, 0, (int)PairingRejectionReason.ProofRejected));
            HandlePairingEvent(new(ReceiverEventKind.PairingRejected, 0, 45, 0, (int)PairingRejectionReason.VerifyRejected));
            SetRunning(false);
            UiCheck(WaitingPairing.Visibility == Visibility.Collapsed && _pairingGeneration == 0 &&
                _pairingProofRejections == 0 && !_pairingTerminalFeedback && !_pairingTimer.IsRunning,
                "stopping the receiver clears challenge retry and terminal state " + language);
        }
        finally
        {
            ReceiverInfoBar.IsOpen = false;
            HidePairingCode();
        }
    }

    private async Task UiTestLandingAnimationAsync(string output)
    {
        await UiNavigateAsync(MirrorNavigationItem, MirrorPage);
        await UiWaitUntil(() => LandingAnimation.LoadedFrameCount == SvgFrameAnimation.FrameCount ||
            LandingAnimation.LoadError is not null, "native SVG animation frames load", 15000);
        if (LandingAnimation.LoadError is { } loadError)
            throw new InvalidOperationException($"Native SVG preload failed after {LandingAnimation.LoadedFrameCount} frames.", loadError);
        UiCheck(LandingAnimation.LoadError is null && LandingAnimation.LoadedFrameCount == SvgFrameAnimation.FrameCount,
            "all supplied SVG frames decode through native SvgImageSource");
        await UiWaitUntil(() => LandingAnimation.IsAnimating, "landing SVG animation starts");
        await UiWaitUntil(() => LandingAnimation.LoadedFrameCountAtFirstAdvance > 0,
            "progressive SVG animation presents its first advancing frame");
        UiCheck(LandingAnimation.LoadedFrameCountAtPlaybackStart is > 0 and < SvgFrameAnimation.FrameCount,
            "SVG animation starts after a small decoded buffer before full preload");
        UiCheck(LandingAnimation.LoadedFrameCountAtFirstAdvance < SvgFrameAnimation.FrameCount ||
            LandingAnimation.AllFramesLoadedMilliseconds - LandingAnimation.PlaybackStartMilliseconds <= 1000.0 / SvgFrameAnimation.FramesPerSecond + 0.1,
            "SVG animation advances before full preload unless all frames decode within one frame interval");
        var imageSource = UiDescendants<Image>(LandingAnimation).Single().Source as SvgImageSource;
        UiCheck(imageSource is not null,
            "landing animation presents a native SVG image source");
        SvgImageSource displayedSource = imageSource!;
        await UiWaitForStableLayout(LandingAnimation);
        UiBounds renderedAnimation = UiMeasure(LandingAnimation);
        double animationScale = LandingAnimation.XamlRoot.RasterizationScale;
        double expectedPixelWidth = Math.Ceiling(renderedAnimation.Width * animationScale);
        double expectedPixelHeight = Math.Ceiling(renderedAnimation.Height * animationScale);
        _uiTestLayouts.Add(new
        {
            phase = "progressive native SVG startup and resolution", renderedAnimation, animationScale,
            expectedPixelWidth, expectedPixelHeight,
            decodedPixelWidth = LandingAnimation.DecodedPixelWidth,
            decodedPixelHeight = LandingAnimation.DecodedPixelHeight,
            targetPixelWidth = LandingAnimation.TargetPixelWidth,
            targetPixelHeight = LandingAnimation.TargetPixelHeight,
            actualSourcePixelWidth = displayedSource.RasterizePixelWidth,
            actualSourcePixelHeight = displayedSource.RasterizePixelHeight,
            loadedFramesAtPlaybackStart = LandingAnimation.LoadedFrameCountAtPlaybackStart,
            loadedFramesAtFirstAdvance = LandingAnimation.LoadedFrameCountAtFirstAdvance,
            firstFrameMilliseconds = LandingAnimation.FirstFrameMilliseconds,
            playbackStartMilliseconds = LandingAnimation.PlaybackStartMilliseconds,
            firstAdvanceMilliseconds = LandingAnimation.FirstAdvanceMilliseconds,
            allFramesLoadedMilliseconds = LandingAnimation.AllFramesLoadedMilliseconds,
            bufferStarvationCount = LandingAnimation.BufferStarvationCount
        });
        // TransformToVisual uses float matrices, so allow one physical pixel at
        // fractional DPI boundaries while comparing to the actual visible size.
        UiCheck(LandingAnimation.DecodedPixelWidth + 1 >= expectedPixelWidth &&
            LandingAnimation.DecodedPixelHeight + 1 >= expectedPixelHeight,
            "decoded SVG cache covers its physical display pixels at the current DPI");
        UiCheck(displayedSource.RasterizePixelWidth + 1 >= expectedPixelWidth &&
            displayedSource.RasterizePixelHeight + 1 >= expectedPixelHeight,
            "presented native SVG source covers its physical display pixels");

        await UiWaitUntil(() => LandingAnimation.StartupPresentationTimesMilliseconds.Count > 1 &&
            LandingAnimation.StartupPresentationTimesMilliseconds[
                LandingAnimation.StartupPresentationTimesMilliseconds.Count - 1] >=
                LandingAnimation.PlaybackStartMilliseconds + 3000,
            "observe actual SVG frame presentations through the first three seconds", 7000);
        double[] startupTimes = LandingAnimation.StartupPresentationTimesMilliseconds.ToArray();
        int[] startupIndices = LandingAnimation.StartupPresentationFrameIndices.ToArray();
        UiCheck(startupTimes.Length == startupIndices.Length,
            "startup SVG timestamps correspond to actual presented frame indices");
        double playbackStartedAt = LandingAnimation.PlaybackStartMilliseconds;
        double preloadCompletedAt = LandingAnimation.AllFramesLoadedMilliseconds;
        var startupIntervals = new List<(double From, double To, double Gap, int Frame, int Advance)>();
        double previousTime = playbackStartedAt;
        int previousIndex = 0;
        for (int index = 0; index < startupTimes.Length; index++)
        {
            double time = startupTimes[index];
            if (time < playbackStartedAt)
            {
                previousIndex = startupIndices[index];
                continue;
            }
            // Include the first presentation beyond three seconds so a gap
            // crossing the end of the startup window cannot disappear.
            int advance = (startupIndices[index] - previousIndex + SvgFrameAnimation.FrameCount) % SvgFrameAnimation.FrameCount;
            startupIntervals.Add((previousTime, time, time - previousTime, startupIndices[index], advance));
            previousTime = time;
            previousIndex = startupIndices[index];
            if (time >= playbackStartedAt + 3000)
                break;
        }

        static object StartupGapStatistics(IEnumerable<double> gaps)
        {
            double[] ordered = gaps.Order().ToArray();
            double? Percentile(double fraction) => ordered.Length == 0 ? null
                : ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * fraction) - 1, 0, ordered.Length - 1)];
            return new { intervalCount = ordered.Length, p50Milliseconds = Percentile(0.5),
                p95Milliseconds = Percentile(0.95), maxMilliseconds = Percentile(1) };
        }

        static (double From, double To, double Gap)[] StartupWindowIntervals(IEnumerable<double> times, double start)
        {
            var intervals = new List<(double From, double To, double Gap)>();
            double previous = start;
            foreach (double time in times)
            {
                if (time < start)
                    continue;
                intervals.Add((previous, time, time - previous));
                previous = time;
                if (time >= start + 3000)
                    break;
            }
            return intervals.ToArray();
        }

        var heartbeatIntervals = StartupWindowIntervals(_uiTestHeartbeatTimesMilliseconds, playbackStartedAt);
        var renderingCallbackIntervals = StartupWindowIntervals(
            LandingAnimation.StartupRenderingCallbackTimesMilliseconds, playbackStartedAt);
        double[] startupGaps = startupIntervals.Select(interval => interval.Gap).Order().ToArray();
        UiCheck(startupGaps.Length > 0, "startup SVG timing contains observed presentation intervals");
        double startupP95 = startupGaps[Math.Clamp((int)Math.Ceiling(startupGaps.Length * 0.95) - 1, 0, startupGaps.Length - 1)];
        double startupMaximum = startupGaps[^1];
        _uiTestLayouts.Add(new
        {
            phase = "native SVG first-three-second frame continuity", playbackStartedAt,
            startupReceiverPolls = _uiStartupPollCount,
            maximumStartupHookMilliseconds = _uiStartupPollHooksMaxMilliseconds,
            maximumStartupEventReadMilliseconds = _uiStartupPollReadEventMaxMilliseconds,
            maximumStartupSourceAssignmentMilliseconds = LandingAnimation.MaximumSourceAssignmentMilliseconds,
            dispatcherHeartbeat = new
            {
                requestedIntervalMilliseconds = 20,
                statistics = StartupGapStatistics(heartbeatIntervals.Select(interval => interval.Gap)),
                recordedThroughMilliseconds = heartbeatIntervals.Length == 0 ? (double?)null : heartbeatIntervals[^1].To,
                intervals = heartbeatIntervals.Select(interval => new
                {
                    fromMilliseconds = interval.From, toMilliseconds = interval.To,
                    gapMilliseconds = interval.Gap
                }).ToArray()
            },
            renderingCallbacks = new
            {
                statistics = StartupGapStatistics(renderingCallbackIntervals.Select(interval => interval.Gap)),
                recordedThroughMilliseconds = renderingCallbackIntervals.Length == 0 ? (double?)null : renderingCallbackIntervals[^1].To,
                intervals = renderingCallbackIntervals.Select(interval => new
                {
                    fromMilliseconds = interval.From, toMilliseconds = interval.To,
                    gapMilliseconds = interval.Gap
                }).ToArray()
            },
            firstFrameMilliseconds = LandingAnimation.FirstFrameMilliseconds,
            firstAdvanceMilliseconds = LandingAnimation.FirstAdvanceMilliseconds,
            preloadCompletedAt,
            loadedFramesAtPlaybackStart = LandingAnimation.LoadedFrameCountAtPlaybackStart,
            loadedFramesAtFirstAdvance = LandingAnimation.LoadedFrameCountAtFirstAdvance,
            overall = StartupGapStatistics(startupGaps),
            duringPreload = StartupGapStatistics(startupIntervals.Where(interval => interval.To <= preloadCompletedAt)
                .Select(interval => interval.Gap)),
            afterPreload = StartupGapStatistics(startupIntervals.Where(interval => interval.From >= preloadCompletedAt)
                .Select(interval => interval.Gap)),
            crossingPreloadCompletion = StartupGapStatistics(startupIntervals.Where(interval =>
                interval.From < preloadCompletedAt && interval.To > preloadCompletedAt).Select(interval => interval.Gap)),
            skippedFrames = startupIntervals.Sum(interval => Math.Max(0, interval.Advance - 1)),
            intervals = startupIntervals.Select(interval => new
            {
                fromMilliseconds = interval.From, toMilliseconds = interval.To,
                gapMilliseconds = interval.Gap, frameIndex = interval.Frame,
                advancedFrames = interval.Advance
            }).ToArray()
        });
        UiCheck(startupMaximum <= 150, "startup SVG presentations have no gap above 150 milliseconds");
        UiCheck(startupP95 <= 80, "startup SVG presentation gap p95 stays within 80 milliseconds");

        long presentedBefore = LandingAnimation.PresentedFrameCount;
        var cadenceWatch = Stopwatch.StartNew();
        await Task.Delay(1050);
        double sampleSeconds = cadenceWatch.Elapsed.TotalSeconds;
        long framesPresented = LandingAnimation.PresentedFrameCount - presentedBefore;
        double observedFramesPerSecond = framesPresented / sampleSeconds;
        _uiTestLayouts.Add(new
        {
            phase = "native SVG animation cadence", requestedFramesPerSecond = SvgFrameAnimation.FramesPerSecond,
            loadedFrames = LandingAnimation.LoadedFrameCount, sampleSeconds, framesPresented,
            observedFramesPerSecond, withinTypicalCadence = observedFramesPerSecond is >= 20 and <= 35,
            frameIndex = LandingAnimation.CurrentFrameIndex
        });
        // Scheduler and display refresh limits can lower the observed cadence.
        // Record it rather than treating a loaded host as a guaranteed 30-Hz display.
        UiCheck(framesPresented > 0 && observedFramesPerSecond <= 35,
            "native SVG animation advances without exceeding its requested cadence");

        long loopsBefore = LandingAnimation.CompletedLoops;
        long presentedBeforeWrap = LandingAnimation.PresentedFrameCount;
        int previousFrame = LandingAnimation.CurrentFrameIndex;
        int frameBeforeWrap = previousFrame;
        bool observedWrap = false;
        await UiWaitUntil(() =>
        {
            int frame = LandingAnimation.CurrentFrameIndex;
            observedWrap |= frame < previousFrame;
            previousFrame = frame;
            return observedWrap && LandingAnimation.CompletedLoops > loopsBefore &&
                LandingAnimation.PresentedFrameCount > presentedBeforeWrap;
        }, "native SVG animation visibly wraps to its first frames", (int)(1000.0 * SvgFrameAnimation.FrameCount / SvgFrameAnimation.FramesPerSecond + 1000));
        UiCheck(LandingAnimation.CurrentFrameIndex is >= 0 and < SvgFrameAnimation.FrameCount,
            "native SVG animation stays within its supplied frame sequence");
        _uiTestLayouts.Add(new
        {
            phase = "native SVG animation loop", frameBeforeWrap,
            frameAfterWrap = LandingAnimation.CurrentFrameIndex, loopsBefore,
            loopsAfter = LandingAnimation.CompletedLoops, observedWrap
        });
        int capturesBefore = _uiTestScreenshots.Count;
        await UiCaptureAsync(output, "native-svg-landing-animation-sharp");
        UiCheck(_uiTestScreenshots.Count == capturesBefore + 1,
            "native SVG landing animation captured through the actual XAML visual tree");

        await UiNavigateAsync(SettingsNavigationItem, SettingsPage);
        await UiWaitUntil(() => !LandingAnimation.IsAnimating, "leaving mirroring pauses SVG animation");
        long pausedPresentations = LandingAnimation.PresentedFrameCount;
        int pausedFrame = LandingAnimation.CurrentFrameIndex;
        await Task.Delay(150);
        UiCheck(LandingAnimation.PresentedFrameCount == pausedPresentations &&
            LandingAnimation.CurrentFrameIndex == pausedFrame,
            "off-page SVG animation retains its frame without rendering work");
        await UiNavigateAsync(MirrorNavigationItem, MirrorPage);
        await UiWaitUntil(() => LandingAnimation.IsAnimating &&
            LandingAnimation.PresentedFrameCount > pausedPresentations,
            "returning to mirroring resumes SVG frames");
        UiCheck(LandingAnimation.LoadedFrameCount == SvgFrameAnimation.FrameCount && LandingAnimation.LoadError is null,
            "navigation reuses the decoded SVG frames");

        UiCheck(AppWindow.Presenter is OverlappedPresenter, "animation window supports minimize and restore");
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        long minimizedPresentations = 0;
        int minimizedFrame = -1;
        try
        {
            presenter.Minimize();
            await UiWaitUntil(() => presenter.State == OverlappedPresenterState.Minimized &&
                !LandingAnimation.IsAnimating, "minimizing own window pauses SVG animation");
            minimizedPresentations = LandingAnimation.PresentedFrameCount;
            minimizedFrame = LandingAnimation.CurrentFrameIndex;
            await Task.Delay(150);
            UiCheck(LandingAnimation.PresentedFrameCount == minimizedPresentations &&
                LandingAnimation.CurrentFrameIndex == minimizedFrame,
                "minimized SVG animation does not present frames");
        }
        finally
        {
            presenter.Restore();
        }
        await UiWaitUntil(() => presenter.State != OverlappedPresenterState.Minimized &&
            LandingAnimation.IsAnimating && LandingAnimation.PresentedFrameCount > minimizedPresentations,
            "restoring own window resumes SVG animation");
        _uiTestLayouts.Add(new
        {
            phase = "native SVG animation pause and resume", pausedFrame, pausedPresentations,
            minimizedFrame, minimizedPresentations, resumedFrame = LandingAnimation.CurrentFrameIndex,
            resumedPresentations = LandingAnimation.PresentedFrameCount
        });
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
            UiCheck(!LandingAnimation.IsAnimating, "active video pauses the landing SVG animation");
            await UiWaitForStableLayout(VideoHostSlot, PlayerControls);
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
            UiCheck(actualFit.Width <= fitSlot.Width && actualFit.Height <= fitSlot.Height &&
                Math.Abs((double)actualFit.Width / actualFit.Height - 320.0 / 180) < 0.01,
                "fit host preserves the full frame aspect inside the UI video slot");

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
        long resumedPresentations = LandingAnimation.PresentedFrameCount;
        await UiWaitUntil(() => LandingAnimation.IsAnimating &&
            LandingAnimation.PresentedFrameCount > resumedPresentations,
            "ending video resumes the landing SVG animation");
    }

    private sealed record UiAnimationResourceSample(double Seconds, long PrivateBytes, int Handles,
        double CpuMilliseconds, long Frames, long Loops, int LoadedFrames, int FrameIndex);

    private async Task UiTestAnimationSoakAsync(string output, int seconds)
    {
        await UiNavigateAsync(MirrorNavigationItem, MirrorPage);
        UiCheck(_receiver is null && LandingAnimation.LoadedFrameCount == SvgFrameAnimation.FrameCount &&
            LandingAnimation.IsAnimating, "animation soak starts with the complete cache and stopped receiver");
        using var process = Process.GetCurrentProcess();
        var watch = Stopwatch.StartNew();
        var samples = new List<UiAnimationResourceSample>();
        UiAnimationResourceSample Sample()
        {
            process.Refresh();
            return new(watch.Elapsed.TotalSeconds, process.PrivateMemorySize64, process.HandleCount,
                process.TotalProcessorTime.TotalMilliseconds, LandingAnimation.PresentedFrameCount,
                LandingAnimation.CompletedLoops, LandingAnimation.LoadedFrameCount, LandingAnimation.CurrentFrameIndex);
        }
        samples.Add(Sample());
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.01, Math.Min(5, seconds - watch.Elapsed.TotalSeconds))));
            samples.Add(Sample());
            await File.WriteAllTextAsync(Path.Combine(output, "animation-soak-progress.json"),
                JsonSerializer.Serialize(new { status = "running", requestedSeconds = seconds, samples },
                    new JsonSerializerOptions { WriteIndented = true }));
        }

        // Exclude the initial warm-up before comparing process resources;
        // do not force a GC or assume a fixed animation frame count.
        double warmupSeconds = Math.Min(15, seconds / 4.0);
        double windowSeconds = Math.Min(25, seconds / 3.0);
        var early = samples.Where(sample => sample.Seconds >= warmupSeconds &&
            sample.Seconds <= warmupSeconds + windowSeconds).ToArray();
        var late = samples.Where(sample => sample.Seconds >= seconds - windowSeconds).ToArray();
        static double Median(IEnumerable<double> values)
        {
            double[] ordered = values.Order().ToArray();
            return ordered.Length % 2 == 0 ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2
                : ordered[ordered.Length / 2];
        }
        double privateGrowth = Median(late.Select(sample => (double)sample.PrivateBytes)) -
            Median(early.Select(sample => (double)sample.PrivateBytes));
        double handleGrowth = Median(late.Select(sample => (double)sample.Handles)) -
            Median(early.Select(sample => (double)sample.Handles));
        long frameBytes = (long)LandingAnimation.DecodedPixelWidth * LandingAnimation.DecodedPixelHeight * 4;
        long memoryGrowthBudget = Math.Max(16L * 1024 * 1024, 8 * frameBytes);
        const int handleGrowthBudget = 8;
        double actualSeconds = samples[^1].Seconds - samples[0].Seconds;
        double presentedFps = (samples[^1].Frames - samples[0].Frames) / actualSeconds;
        double cpuCores = (samples[^1].CpuMilliseconds - samples[0].CpuMilliseconds) / (actualSeconds * 1000);
        var intervals = samples.Zip(samples.Skip(1), (before, after) => new
        {
            seconds = after.Seconds - before.Seconds,
            frames = after.Frames - before.Frames,
            fps = (after.Frames - before.Frames) / (after.Seconds - before.Seconds),
            cpuCores = (after.CpuMilliseconds - before.CpuMilliseconds) / ((after.Seconds - before.Seconds) * 1000)
        }).ToArray();
        var resourceReport = new
        {
            phase = "native SVG animation resource soak", requestedSeconds = seconds, actualSeconds,
            warmupSeconds, windowSeconds, frameBytes, memoryGrowthBudget, handleGrowthBudget,
            privateGrowthBytes = privateGrowth, handleGrowth, presentedFps, averageCpuCores = cpuCores,
            averageCpuPercentOfMachine = cpuCores * 100 / Environment.ProcessorCount,
            completedLoops = samples[^1].Loops - samples[0].Loops, samples, intervals,
            thresholdRationale = "Compare median windows after the initial warm-up without forced GC. " +
                "Allow at most 16 MiB or eight RGBA frames of private-memory drift and eight handles; " +
                "a repeated " + SvgFrameAnimation.FrameCount + "-frame cache or a handle retained on each loop exceeds these budgets. " +
                "Presentation throughput must retain at least five sixths of the supplied frame rate."
        };
        _uiTestLayouts.Add(resourceReport);
        await File.WriteAllTextAsync(Path.Combine(output, "animation-soak-resources.json"),
            JsonSerializer.Serialize(resourceReport, new JsonSerializerOptions { WriteIndented = true }));
        UiCheck(samples.All(sample => sample.LoadedFrames == SvgFrameAnimation.FrameCount &&
            sample.FrameIndex >= 0 && sample.FrameIndex < SvgFrameAnimation.FrameCount),
            "animation soak keeps exactly the complete " + SvgFrameAnimation.FrameCount + "-frame cache");
        UiCheck(intervals.All(interval => interval.frames > 0) && presentedFps >= SvgFrameAnimation.FramesPerSecond * 5.0 / 6 &&
            presentedFps <= SvgFrameAnimation.FramesPerSecond * 1.05,
            "animation soak keeps advancing near the supplied frame rate throughout the sampling window");
        UiCheck(privateGrowth <= memoryGrowthBudget, "animation soak private memory stays within its bounded drift budget");
        UiCheck(handleGrowth <= handleGrowthBudget, "animation soak does not retain a handle on each loop");

        async Task CheckPausedPhase(string phase, Func<Task> enter, Action exit)
        {
            await enter();
            UiAnimationResourceSample before = Sample();
            await Task.Delay(2000);
            UiAnimationResourceSample after = Sample();
            _uiTestLayouts.Add(new { phase = "animation soak " + phase, before, after });
            UiCheck(!LandingAnimation.IsAnimating && after.Frames == before.Frames &&
                after.FrameIndex == before.FrameIndex, "animation soak pauses all frame work while " + phase);
            exit();
            await UiWaitUntil(() => LandingAnimation.IsAnimating &&
                LandingAnimation.PresentedFrameCount > after.Frames, "animation soak resumes after " + phase);
        }
        await CheckPausedPhase("off page",
            async () => await UiNavigateAsync(SettingsNavigationItem, SettingsPage),
            () => MainNavigation.SelectedItem = MirrorNavigationItem);
        await UiWaitForStableLayout(MirrorPage);
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        try
        {
            await CheckPausedPhase("minimized", async () =>
            {
                presenter.Minimize();
                await UiWaitUntil(() => presenter.State == OverlappedPresenterState.Minimized &&
                    !LandingAnimation.IsAnimating, "animation soak minimize");
            }, () => presenter.Restore());
        }
        finally
        {
            if (presenter.State == OverlappedPresenterState.Minimized)
                presenter.Restore();
        }
        await UiWaitForStableLayout(MirrorPage);
        await UiCaptureAsync(output, "native-svg-after-two-minute-soak");
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
            if (Environment.GetEnvironmentVariable("CLEARCAST_UI_BACKDROP_ONLY") == "1")
            {
                UiCheck(_receiver is null, "backdrop benchmark never starts a receiver or occupies its ports");
                UiCheck(WindowLayout.KeyboardAcceleratorPlacementMode == Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden,
                    "global Space shortcut does not create a misleading tooltip on disconnect");
                _preferences = _preferences with { AutoFullScreen = false };
                await UiWaitUntil(() => LandingAnimation.LoadedFrameCount == SvgFrameAnimation.FrameCount,
                    "SVG decode settles before backdrop measurements", 15000);
                ApplyMediaState(new ReceiverMediaState(1, ReceiverMediaKind.Video, true, "", "", "", 5_000, 60_000, 0, 0,
                    MediaControls.PlayPause | MediaControls.Seek) { Source = "direct", ControlSession = 1, ContentRevision = 1, SeekEndMs = 60_000 });
                SetPlayerFrameSize(1280, 720);
                SetVideoAvailable(true);
                await UiTestVideoBackdropAsync(output);
                receiverStopped = true;
                return;
            }
            await UiWaitUntil(() => _receiverReady || ReceiverInfoBar.IsOpen, "receiver startup", 30000);
            UiCheck(_receiverReady, "receiver starts successfully");
            if (Environment.GetEnvironmentVariable("CLEARCAST_UI_STORE_ONLY") == "1")
            {
                await UiTestStoreMusicScreenshotsAsync(output);
                return;
            }
            if (Environment.GetEnvironmentVariable("CLEARCAST_UI_PLAYBACK_ONLY") == "1")
            {
                await UiTestLivePlaybackControlsAsync(output);
                return;
            }
            UiCheck(await StopReceiverAsync(), "receiver stops without blocking the window");
            receiverStopped = true;

            await UiTestLandingAnimationAsync(output);
            // Keep artwork decode and intentional landing-view pauses outside
            // the independently measured first-three-second SVG startup window.
            receiverStopped = false;
            StartAirPlay();
            await UiWaitUntil(() => _receiverReady || ReceiverInfoBar.IsOpen, "receiver restarts for music activity polling", 30000);
            UiCheck(_receiverReady, "receiver restarts successfully for fast music polling");
            await UiTestLivePlaybackControlsAsync(output);
            await UiTestMusicFastPollingAsync();
            await UiTestStoreMusicScreenshotsAsync(output);
            UiCheck(await StopReceiverAsync(), "receiver stops cleanly after fast music polling validation");
            receiverStopped = true;
            if (Environment.GetEnvironmentVariable("CLEARCAST_UI_SOAK_SECONDS") is { Length: > 0 } soakSetting)
            {
                if (!int.TryParse(soakSetting, out int soakSeconds) || soakSeconds is < 30 or > 600)
                    throw new InvalidOperationException("CLEARCAST_UI_SOAK_SECONDS must be between 30 and 600.");
                await UiTestAnimationSoakAsync(output, soakSeconds);
            }
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
                await UiTestPairingFeedbackAsync();
                await UiTestConnectionStatusAsync();
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
                        UiCheckSettingsCardGeometry(prefix);
                        UiCheckAutoFullScreenCardGeometry(prefix);
                        if (english)
                            UiCheckEnglishVisibleText(SettingsPage, "settings " + prefix);
                        await UiCaptureAsync(output, prefix + "-settings");

                        await UiNavigateAsync(AboutNavigationItem, AboutPage);
                        UiCheck(AboutVersionLabel.Text == Texts.AboutVersion, "about version " + prefix);
                        if (english)
                            UiCheckEnglishVisibleText(AboutPage, "about " + prefix);
                        await UiCaptureAsync(output, prefix + "-about");
                        await UiNavigateAsync(MirrorNavigationItem, MirrorPage);
                        ShowReceiveError("设备验证未通过，请在设备上重新选择 ClearCast 后重试。", 470);
                        await Task.Delay(80);
                        var icon = UiDescendants<Grid>(ReceiverInfoBar).Single(element => element.Name == "ErrorIcon");
                        var close = UiDescendants<Button>(ReceiverInfoBar).Single(element => element.Name == "CloseButton");
                        await UiWaitForStableLayout(ReceiverInfoBar, icon, close, CopyErrorButton);
                        if (english)
                            UiCheckEnglishVisibleText(MirrorPage, "mirroring error " + prefix);
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
                        UiCheck(CopyErrorButton.ActualWidth >= 130 && CopyErrorButton.ActualHeight >= 32 &&
                            RetryReceiveButton.ActualWidth >= 130 && RetryReceiveButton.ActualHeight >= 32,
                            "error text actions meet minimum size " + prefix);
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

            ShowPairingCode("8838");
            UiCheck(PinText.Text == "8838" && WaitingPairing.Visibility == Visibility.Visible, "pairing challenge shown");
            UiCheck(PairingPrompt.Text == "Enter these four digits on your device.", "pairing challenge translated");
            await UiWaitForStableLayout(PinText, PairingCloseButton);
            _uiTestLayouts.Add(new { phase = "pairing close button alignment", pinBounds = UiMeasure(PinText), closeBounds = UiMeasure(PairingCloseButton) });
            UiCheck(Math.Abs(UiMeasure(PinText).CenterY - UiMeasure(PairingCloseButton).CenterY) <= 1,
                "pairing close button aligns with the visible digits");
            UiCheck(PairingCloseButton.ActualWidth == 32 && PairingCloseButton.ActualHeight == 32,
                "pairing close remains a square icon action");
            VisualStateManager.GoToState(PairingCloseButton, "PointerOver", false);
            await UiCaptureAsync(output, "pairing-code-close-button");
            VisualStateManager.GoToState(PairingCloseButton, "Normal", false);
            UiInvoke(PairingCloseButton);
            UiCheck(WaitingPairing.Visibility == Visibility.Collapsed && PinText.Text.Length == 0 &&
                _pairingGeneration == 0 && !_pairingTimer.IsRunning, "pairing challenge dismissal clears visible code and timer");
            for (int index = 0; index < 100; ++index)
                MainNavigation.SelectedItem = (index % 3) switch { 0 => SettingsNavigationItem, 1 => AboutNavigationItem, _ => MirrorNavigationItem };
            await Task.Delay(500);
            UiCheck(_pageHosts.Count <= 3 && NavigationFrame.BackStack.Count == 0, "100 navigations keep bounded page history");
            RotatePlayer(1);
            TogglePlayerFullScreen();
            UiCheck(!_playerFullScreen && !_hasVideo, "player controls ignore inactive playback");
            await UiTestPlayerWindowPathsAsync(output);
            await UiTestMediaFeaturesAsync(output);
            await UiNavigateAsync(MirrorNavigationItem, MirrorPage);
            await UiCaptureAsync(output, "completed-mirror-page");
        }
        catch (Exception error)
        {
            failure = error.ToString();
        }
        finally
        {
            _uiTestHeartbeatTimer?.Stop();
            try { if (!receiverStopped) receiverStopped = await StopReceiverAsync(); }
            catch (Exception error) { failure ??= error.ToString(); }
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                status = failure is null ? "pass" : "fail", failure, elapsedMilliseconds = elapsed.ElapsedMilliseconds,
                checkCount = _uiTestChecks.Count, checks = _uiTestChecks, layouts = _uiTestLayouts,
                screenshots = _uiTestScreenshots, captureErrors = _uiTestCaptureErrors, receiverStopped,
                startupHeartbeatTimesMilliseconds = _uiTestHeartbeatTimesMilliseconds,
                screenshotMethod = "RenderTargetBitmap with a temporary opaque SolidBackgroundFillColorBaseBrush; excludes DWM/Mica and native video surfaces.",
                playerWindowMethod = "Actual UI event handlers and this test process's own HWND geometry/presenter/Escape hook, with synthetic frame dimensions; no video or audio pipeline exercised.",
                limitation = "No synthetic test replaces physical iPhone/iPad/Mac video, audio, rotation or full-screen playback acceptance."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }
}
#endif
