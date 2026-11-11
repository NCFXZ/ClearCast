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
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace ClearCast;

public sealed partial class MainWindow
{
    [DllImport("gdi32.dll", EntryPoint = "PtInRegion", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiPointInRegion(nint region, int x, int y);

    [DllImport("user32.dll", EntryPoint = "GetFocus", ExactSpelling = true)]
    private static extern nint UiKeyboardFocus();

    [DllImport("user32.dll", EntryPoint = "IsChild", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiIsChildWindow(nint parent, nint window);

    private delegate bool UiDiagnosticsChildCallback(nint window, nint data);
    [DllImport("user32.dll", EntryPoint = "EnumChildWindows", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiDiagnosticsChildren(nint parent, UiDiagnosticsChildCallback callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int UiDiagnosticsWindowText(nint window, System.Text.StringBuilder text, int capacity);
    [DllImport("user32.dll", EntryPoint = "GetParent", ExactSpelling = true)]
    private static extern nint UiDiagnosticsParent(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", ExactSpelling = true)]
    private static extern nint UiDiagnosticsWindowStyle(nint window, int index);

    private nint UiDiagnosticsOverlay()
    {
        nint found = 0;
        if (_videoHost is null) return found;
        UiDiagnosticsChildren(WindowNative.GetWindowHandle(this), (window, _) =>
        {
            var name = new System.Text.StringBuilder(64);
            UiDiagnosticsWindowText(window, name, name.Capacity);
            if (name.ToString() == "ClearCastMediaInfoOverlay" &&
                UiCompositorGetWindowThreadProcessId(window, out uint owner) != 0 && owner == Environment.ProcessId)
                found = window;
            return true;
        }, 0);
        return found;
    }

    private string UiDiagnosticsFailureState(string scenario, RectInt32 expected, UiCompositorPoint origin)
    {
        nint overlay = UiDiagnosticsOverlay();
        bool measured = overlay != 0 && UiGetWindowRect(overlay, out _);
        UiGetWindowRect(overlay, out var actual);
        object? requested = _diagnosticsRasterRequest is { } request ? new
            { request.Width, request.Height, request.Scale, TextLength = request.Text.Length } : null;
        object? applied = _diagnosticsRasterApplied is { } image ? new
            { image.Width, image.Height, image.Scale, TextLength = image.Text.Length } : null;
        RectInt32? current = GetMediaDiagnosticsBounds(VideoHostSlot.XamlRoot.RasterizationScale);
        var details = new
        {
            phase = "diagnostic overlay failure", scenario, renderError = _diagnosticsRenderError,
            rasterRunning = _diagnosticsRasterRunning, rasterGeneration = _diagnosticsRasterGeneration,
            requested, applied, pixelWidth = _uiDiagnosticsWidth, pixelHeight = _uiDiagnosticsHeight,
            pixelBytes = _uiDiagnosticsPixels?.Length ?? 0, scale = VideoHostSlot.XamlRoot.RasterizationScale,
            expected = new { expected.X, expected.Y, expected.Width, expected.Height },
            current = current is { } bounds ? new { bounds.X, bounds.Y, bounds.Width, bounds.Height } : null,
            clientScreenOrigin = new { origin.X, origin.Y },
            overlay = overlay.ToInt64(), visible = overlay != 0 && UiIsWindowVisible(overlay), measured,
            overlayBounds = new { actual.Left, actual.Top, actual.Width, actual.Height },
            parent = overlay != 0 ? UiDiagnosticsParent(overlay).ToInt64() : 0,
            root = WindowNative.GetWindowHandle(this).ToInt64(), hostVisible = UiIsWindowVisible(_videoHost!.Handle),
            _showMediaDiagnostics, _hasVideo, panelVisible = VideoDiagnosticsPanel.Visibility.ToString(),
            panelWidth = VideoDiagnosticsPanel.ActualWidth, panelHeight = VideoDiagnosticsPanel.ActualHeight,
            appVisible = AppWindow.IsVisible, hostVisibleToXaml = WindowLayout.XamlRoot.IsHostVisible
        };
        _uiTestLayouts.Add(details);
        return System.Text.Json.JsonSerializer.Serialize(details);
    }

    private async Task UiCheckDiagnosticsAlphaAsync()
    {
        await UiWaitUntil(() => _uiDiagnosticsPixels is not null,
            "diagnostic alpha image is applied: " + _diagnosticsRenderError);
        byte[] pixels = _uiDiagnosticsPixels!;
        UiCheck(_uiDiagnosticsWidth > 0 && _uiDiagnosticsHeight > 0 &&
            pixels.Length == _uiDiagnosticsWidth * _uiDiagnosticsHeight * 4,
            "the applied diagnostic bitmap has bounded BGRA dimensions");
        int transparent = 0, background = 0, translucent = 0;
        bool premultiplied = true;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            byte alpha = pixels[offset + 3];
            if (alpha == 0) ++transparent;
            if (alpha is > 0 and < 255) ++translucent;
            if (alpha is >= 178 and <= 180) ++background;
            premultiplied &= pixels[offset] <= alpha && pixels[offset + 1] <= alpha && pixels[offset + 2] <= alpha;
        }
        UiCheck(premultiplied && transparent > 0 && translucent > pixels.Length / 16 && background > pixels.Length / 24,
            "applied pixels retain premultiplied alpha, transparent rounded corners and the semi-transparent video background");
        _uiTestLayouts.Add(new { phase = "diagnostic alpha bitmap", width = _uiDiagnosticsWidth,
            height = _uiDiagnosticsHeight, transparentPixels = transparent, translucentPixels = translucent,
            backgroundAlphaPixels = background, premultiplied });
    }

    private RectInt32 UiVisibleVideoBounds()
    {
        double scale = VideoHostSlot.XamlRoot.RasterizationScale;
        RectInt32 slot = GetVideoSlotBounds(scale) ?? throw new InvalidOperationException("Missing diagnostic video viewport.");
        UiCheck(UiGetWindowRect(_videoHost!.Handle, out var host), "read owned diagnostics video geometry");
        var origin = new UiCompositorPoint();
        UiCheck(UiCompositorClientToScreen(WindowNative.GetWindowHandle(this), ref origin),
            "map owned diagnostic video geometry to client pixels");
        int sourceWidth = _playerRotation % 2 == 0 ? _playerFrameSize.Width : _playerFrameSize.Height;
        int sourceHeight = _playerRotation % 2 == 0 ? _playerFrameSize.Height : _playerFrameSize.Width;
        double fit = Math.Min((double)host.Width / sourceWidth, (double)host.Height / sourceHeight);
        int width = (int)Math.Round(sourceWidth * fit), height = (int)Math.Round(sourceHeight * fit);
        int left = Math.Max(slot.X, host.Left - origin.X + (host.Width - width) / 2);
        int top = Math.Max(slot.Y, host.Top - origin.Y + (host.Height - height) / 2);
        int right = Math.Min(slot.X + slot.Width, host.Left - origin.X + (host.Width + width) / 2);
        int bottom = Math.Min(slot.Y + slot.Height, host.Top - origin.Y + (host.Height + height) / 2);
        return new RectInt32(left, top, right - left, bottom - top);
    }

    private async Task UiCheckDiagnosticsPlacementAsync(string scenario)
    {
        double scale = VideoHostSlot.XamlRoot.RasterizationScale;
        RectInt32 video = UiVisibleVideoBounds();
        RectInt32 anchor = _playerFullScreen
            ? GetVideoSlotBounds(scale) ?? throw new InvalidOperationException("Missing full-screen diagnostic viewport.") : video;
        double inset = _playerFullScreen ? 16 * scale : 0;
        RectInt32 info = GetMediaDiagnosticsBounds(scale) ?? throw new InvalidOperationException("Missing video diagnostic panel.");
        string placement = System.Text.Json.JsonSerializer.Serialize(new
        {
            video = new { video.X, video.Y, video.Width, video.Height },
            anchor = new { anchor.X, anchor.Y, anchor.Width, anchor.Height },
            info = new { info.X, info.Y, info.Width, info.Height }, scale, inset,
            sourceWidth = _playerFrameSize.Width, sourceHeight = _playerFrameSize.Height,
            rotation = _playerRotation, originalSize = _preferences.OriginalSize, fullScreen = _playerFullScreen
        });
        UiCheck(info.X >= anchor.X && info.Y >= anchor.Y && info.X + info.Width <= anchor.X + anchor.Width + 1 &&
            info.Y + info.Height <= anchor.Y + anchor.Height + 1 &&
            Math.Abs(info.X - anchor.X - inset) <= 2 && Math.Abs(info.Y - anchor.Y - inset) <= 2,
            (_playerFullScreen ? "diagnostics use the screen upper-left inset: " :
                "diagnostics align flush with the visible video upper-left corner: ") + scenario + " " + placement);
        UiCheck(UiGetWindowRect(_videoHost!.Handle, out var host), "read native diagnostic region origin: " + scenario);
        var origin = new UiCompositorPoint();
        UiCheck(UiCompositorClientToScreen(WindowNative.GetWindowHandle(this), ref origin),
            "map native diagnostic region origin: " + scenario);
        int x = info.X + origin.X - host.Left, y = info.Y + origin.Y - host.Top;
        nint region = UiCreateRectRgn(0, 0, 0, 0);
        try
        {
            UiCheck(region != 0, "allocate owned video region probe: " + scenario);
            bool hasRegion = UiGetWindowRgn(_videoHost.Handle, region) > 0;
            int centerX = x + info.Width / 2, centerY = y + info.Height / 2;
            bool overVideo = centerX >= 0 && centerY >= 0 && centerX < host.Width && centerY < host.Height;
            UiCheck(!overVideo || !hasRegion || UiPointInRegion(region, centerX, centerY),
                "native video remains intact where the translucent information panel overlaps it: " + scenario);
        }
        finally { if (region != 0) UiDeleteObject(region); }
        try
        {
            await UiWaitUntil(() =>
            {
                nint overlay = UiDiagnosticsOverlay();
                return overlay != 0 && UiIsWindowVisible(overlay) && UiGetWindowRect(overlay, out var overlayBounds) &&
                    Math.Abs(overlayBounds.Left - info.X - origin.X) <= 2 && Math.Abs(overlayBounds.Top - info.Y - origin.Y) <= 2 &&
                    Math.Abs(overlayBounds.Width - info.Width) <= 2 && Math.Abs(overlayBounds.Height - info.Height) <= 2;
            }, "owned alpha overlay tracks diagnostic panel bounds: " + scenario);
        }
        catch (TimeoutException error)
        {
            throw new InvalidOperationException("Alpha overlay state at timeout: " +
                UiDiagnosticsFailureState(scenario, info, origin), error);
        }
        nint layered = UiDiagnosticsOverlay();
        const long alphaStyles = 0x00080000 | 0x00000020 | 0x08000000;
        UiCheck(UiDiagnosticsParent(layered) == WindowNative.GetWindowHandle(this) &&
            (UiDiagnosticsWindowStyle(layered, -20).ToInt64() & alphaStyles) == alphaStyles,
            "diagnostics use an owned layered, click-through, non-activating root child: " + scenario);
        _uiTestLayouts.Add(new { phase = "diagnostic video placement", scenario, video, anchor, info,
            sourceWidth = _playerFrameSize.Width, sourceHeight = _playerFrameSize.Height,
            rotation = _playerRotation, originalSize = _preferences.OriginalSize, fullScreen = _playerFullScreen });
    }

    private async Task UiTestDiagnosticsVideoPlacementAsync(string output, ReceiverMediaState video)
    {
        SizeInt32 size = AppWindow.Size;
        try
        {
            double scale = VideoHostSlot.XamlRoot.RasterizationScale;
            AppWindow.Resize(new SizeInt32((int)(640 * scale), (int)(920 * scale)));
            foreach (var geometry in new[] { (1920, 1080, 0, false, "landscape in tall viewport"),
                (720, 1280, 0, false, "portrait video"), (720, 1280, 1, false, "rotated portrait video"),
                (3840, 2160, 0, true, "original-size source clipped by viewport") })
            {
                _preferences = _preferences with { OriginalSize = geometry.Item4 };
                _playerRotation = geometry.Item3;
                ApplyMediaState(video with { Diagnostics = video.Diagnostics with { Width = geometry.Item1, Height = geometry.Item2 } });
                SetPlayerFrameSize(geometry.Item1, geometry.Item2);
                UpdateVideoHost();
                await UiWaitForStableLayout(VideoHostSlot, VideoDiagnosticsPanel);
                UpdateVideoHost();
                await UiCheckDiagnosticsPlacementAsync(geometry.Item5);
                await UiCaptureAsync(output, "diagnostics-placement-" + geometry.Item1 + "-" + geometry.Item3 + "-" + geometry.Item4);
            }
        }
        finally
        {
            _preferences = _preferences with { OriginalSize = false };
            _playerRotation = 0;
            ApplyMediaState(video);
            SetPlayerFrameSize(1920, 1080);
            AppWindow.Resize(size);
            UpdateVideoHost();
            await UiWaitForStableLayout(VideoHostSlot, VideoDiagnosticsPanel);
        }
    }

    private async Task UiTestSkipIconRastersAsync(string output)
    {
        double scale = WindowLayout.XamlRoot.RasterizationScale;
        foreach (Button button in new[] { VideoSkipBackwardButton, VideoSkipForwardButton })
        {
            var icon = button.Content as FrameworkElement ?? throw new InvalidOperationException("Missing skip icon content.");
            UiCheck(Math.Abs(icon.ActualWidth - 24) < 0.1 && Math.Abs(icon.ActualHeight - 24) < 0.1,
                "skip icon retains its complete 24-DIP source canvas: " + button.Name);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(icon);
            var buffer = await bitmap.GetPixelsAsync();
            byte[] pixels = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            UiCheck(Math.Abs(width - 24 * scale) <= 1 && Math.Abs(height - 24 * scale) <= 1 && pixels.Length == width * height * 4,
                "skip icon raster uses native DPI without a second scale: " + button.Name);
            int minX = width, minY = height, maxX = -1, maxY = -1, upperInk = 0, lowerInk = 0, edgeInk = 0;
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                {
                    if (pixels[(y * width + x) * 4 + 3] < 8) continue;
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                    if (y < height / 2) ++upperInk; else ++lowerInk;
                    if (x == 0 || y == 0 || x == width - 1 || y == height - 1) ++edgeInk;
                }
            UiCheck(upperInk > 0 && lowerInk > 0 && edgeInk == 0 && minX > 0 && minY > 0 && maxX < width - 1 && maxY < height - 1,
                "skip arrow and lower digits retain transparent padding instead of touching a clipped raster edge: " + button.Name);
            var directory = await StorageFolder.GetFolderFromPathAsync(output);
            var file = await directory.CreateFileAsync(button.Name + "-native-dpi.png", CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height,
                96 * scale, 96 * scale, pixels);
            await encoder.FlushAsync();
            _uiTestScreenshots.Add(file.Path);
            _uiTestLayouts.Add(new { phase = "skip icon source raster", button = button.Name, width, height, scale,
                inkBounds = new { minX, minY, maxX, maxY }, upperInk, lowerInk, edgeInk });
        }
    }

    private async Task UiTestFocusedSpaceGuardsAsync()
    {
        ReceiverMediaState saved = _mediaState;
        int rotation = _playerRotation;
        try
        {
            ApplyMediaState(saved with { Source = "mirror", Controls = MediaControls.None, DurationMs = 0, SeekEndMs = 0 });
            foreach (Button button in new[] { RotateLeftButton, RotateRightButton, FullScreenButton, PlayerStopButton })
            {
                bool enabled = button.IsEnabled;
                int clicks = 0;
                RoutedEventHandler observeClick = (_, _) => ++clicks;
                try
                {
                    // An enabled test focus target exposes default Button Space
                    // activation even though this fixture has no receiver.
                    button.IsEnabled = true;
                    button.Click += observeClick;
                    UiCheck(button.Focus(FocusState.Programmatic), "focus owned player action for Space routing: " + button.Name);
                    nint focused = UiKeyboardFocus(), root = WindowNative.GetWindowHandle(this);
                    UiCheck(focused != 0 && (focused == root || UiIsChildWindow(root, focused)),
                        "Space message target is owned by this test window: " + button.Name);
                    UiCheck(UiPostMessage(focused, 0x0100, 0x20, 1) &&
                        UiPostMessage(focused, 0x0100, 0x20, (nint)(1L << 30 | 1)) &&
                        UiPostMessage(focused, 0x0101, 0x20, unchecked((nint)0xC0000001)),
                        "queue Space down repeat and up to the owned XAML action: " + button.Name);
                    await Task.Delay(120);
                    UiCheck(clicks == 0 && _playerRotation == rotation && !_playerFullScreen && _hasVideo &&
                        _mediaState.Source == "mirror", "controlless mirroring consumes Space without activating focused action: " + button.Name);
                }
                finally { button.Click -= observeClick; button.IsEnabled = enabled; }
            }
        }
        finally { SetPlayerFullScreen(false); ApplyMediaState(saved); }
    }

    private async Task UiTestMediaDiagnosticsAsync(string output)
    {
        var preferences = _preferences;
        var state = _mediaState;
        bool english = Texts.IsEnglish;
        bool diagnostics = _showMediaDiagnostics;
        await UiNavigateAsync(MirrorNavigationItem, MirrorPage);
        UiCheck(_receiver is null && _videoHost is not null && !_hasVideo && !_hasMusic,
            "media diagnostics use a stopped receiver and owned synthetic video host");
        try
        {
            _preferences = _preferences with { AutoFullScreen = false, OriginalSize = false };
            _showMediaDiagnostics = false;
            UpdateMediaDiagnostics();
            ToggleMediaDiagnostics();
            UiCheck(!_showMediaDiagnostics && VideoDiagnosticsPanel.Visibility == Visibility.Collapsed &&
                MusicDiagnosticsPanel.Visibility == Visibility.Collapsed, "F12 diagnostics ignore idle playback");
            Texts.SetEnglish(true);
            RefreshDynamicText();
            var video = new ReceiverMediaState(600, ReceiverMediaKind.Video, true, "", "", "", 5_000, 60_000, 0, 0,
                MediaControls.None)
            {
                Connected = true, Source = "direct", Session = 600,
                Diagnostics = new() { VideoCodec = "H.264", Width = 1920, Height = 1080, FrameRate = 29.97002997,
                    VideoBitrate = 5_300_000, AudioCodec = "AAC", SampleRate = 48000, Channels = 2, AudioBitrate = 256_000 }
            };
            ApplyMediaState(video);
            SetPlayerFrameSize(1920, 1080);
            SetVideoAvailable(true);
            await UiWaitForStableLayout(VideoHostSlot, PlayerControls);
            await UiTestSkipIconRastersAsync(output);
            await UiTestFocusedSpaceGuardsAsync();
            UiCheck(VideoDiagnosticsPanel.Visibility == Visibility.Collapsed, "video diagnostics are hidden before F12");
            _videoHost!.RefreshInputHooks();
            UiCheck(UiPostMessage(_videoHost.Handle, 0x0100, 0x7B, 1), "queue F12 to the owned native video host");
            await UiWaitUntil(() => VideoDiagnosticsPanel.Visibility == Visibility.Visible, "native video F12 shows diagnostics");
            UiCheck(UiPostMessage(_videoHost.Handle, 0x0100, 0x7B, (nint)(1L << 30 | 1)),
                "queue held F12 repeat to the owned native video host");
            await Task.Delay(120);
            UiCheck(_showMediaDiagnostics, "held native video F12 does not repeatedly toggle diagnostics");
            UiCheck(VideoDiagnosticsText.Text == Texts.MediaDiagnosticsText(video) &&
                VideoDiagnosticsText.Text.Contains("1920 × 1080") && VideoDiagnosticsText.Text.Contains("29.97 fps") &&
                VideoDiagnosticsText.Text.Contains("5.3 Mbps") && MusicDiagnosticsPanel.Visibility == Visibility.Collapsed,
                "video diagnostics display actual source geometry frame rate codec and bitrate");
            UiCheck(VideoDiagnosticsText.FontSize == 11 && MusicDiagnosticsText.FontSize == 11,
                "video and music diagnostics use the smaller eleven-DIP font");
            long sampledAt = Stopwatch.GetTimestamp();
            SampleMediaDiagnosticsStats(new PlaybackStats(120, 2, 1920, 1080), sampledAt);
            UpdateMediaDiagnostics();
            UiCheck(double.IsNaN(_diagnosticsFrameRate) && VideoDiagnosticsText.Text == Texts.MediaDiagnosticsText(video),
                "first renderer sample leaves actual frame rate unknown instead of inventing a rate");
            SampleMediaDiagnosticsStats(new PlaybackStats(150, 3, 1920, 1080), sampledAt + Stopwatch.Frequency);
            UpdateMediaDiagnostics();
            UiCheck(Math.Abs(_diagnosticsFrameRate - 30) < 0.001 && _diagnosticsDropped == 3 &&
                VideoDiagnosticsText.Text.Contains(Texts.RenderedMediaDiagnostics(30, 3)) &&
                VideoDiagnosticsText.Text.Contains("29.97 fps"),
                "actual renderer frame delta reports 30 fps and cumulative dropped frames separately from source rate");
            SampleMediaDiagnosticsStats(new PlaybackStats(5, 0, 1920, 1080), sampledAt + 2 * Stopwatch.Frequency);
            UpdateMediaDiagnostics();
            UiCheck(double.IsNaN(_diagnosticsFrameRate) && VideoDiagnosticsText.Text == Texts.MediaDiagnosticsText(video),
                "reset renderer counters discard stale actual frame rate");
            SampleMediaDiagnosticsStats(new PlaybackStats(35, 1, 1920, 1080), sampledAt + 3 * Stopwatch.Frequency);
            UpdateMediaDiagnostics();
            UiCheck(Math.Abs(_diagnosticsFrameRate - 30) < 0.001,
                "renderer sampling resumes from the new counter baseline");
            ApplyMediaState(video with { Session = 599, Revision = 599 });
            UiCheck(double.IsNaN(_diagnosticsFrameRate) && _diagnosticsStatsAt == 0 && _diagnosticsDropped == 0 &&
                VideoDiagnosticsText.Text == Texts.MediaDiagnosticsText(video),
                "new media source resets actual renderer rate and dropped-frame history");
            ApplyMediaState(video);
            ResetMediaDiagnosticsStats();
            UpdateMediaDiagnostics();
            await UiWaitForStableLayout(VideoDiagnosticsPanel);
            await UiCheckDiagnosticsPlacementAsync("windowed video");
            await UiCheckDiagnosticsAlphaAsync();
            string pausedDiagnostics = VideoDiagnosticsText.Text;
            int rasterAttempts = 0;
            _uiBeforeDiagnosticsRasterAsync = () =>
            {
                if (++rasterAttempts == 1) throw new COMException("Synthetic transient raster failure.");
                return Task.CompletedTask;
            };
            try
            {
                ToggleMediaDiagnostics();
                ToggleMediaDiagnostics();
                await UiWaitUntil(() => _diagnosticsRenderError.Length > 0 && !_diagnosticsRasterRunning,
                    "transient diagnostics capture failure is observed");
                for (int i = 0; i < 100; i++) UpdateVideoHost();
                UiCheck(rasterAttempts == 1 && _diagnosticsRasterApplied is null,
                    "failed static diagnostics do not create an immediate retry storm");
                await Task.Delay(1100);
                UpdateVideoHost();
                await UiWaitUntil(() => _diagnosticsRasterApplied == _diagnosticsRasterRequest &&
                    _diagnosticsRenderError.Length == 0 && !_diagnosticsRasterRunning,
                    "unchanged diagnostics retry after the bounded delay");
                UiCheck(rasterAttempts == 2 && VideoDiagnosticsText.Text == pausedDiagnostics && UiIsWindowVisible(UiDiagnosticsOverlay()),
                    "static diagnostics recover from one capture failure without changing text size or F12 state");
            }
            finally { _uiBeforeDiagnosticsRasterAsync = null; }
            ToggleMediaDiagnostics();
            UiCheck(!UiIsWindowVisible(UiDiagnosticsOverlay()), "F12 hides the native alpha information surface");
            ToggleMediaDiagnostics();
            await UiCheckDiagnosticsPlacementAsync("windowed video after F12 reopens");
            await UiCaptureAsync(output, "diagnostics-video-windowed");
            await UiTestDiagnosticsVideoPlacementAsync(output, video);

            ApplyMediaState(video with { Diagnostics = video.Diagnostics with { Width = 1440, Height = 1080 } });
            SetPlayerFrameSize(1440, 1080);
            SetPlayerFullScreen(true);
            await UiWaitForStableLayout(VideoHostSlot, VideoDiagnosticsPanel, PlayerControls);
            await UiWaitUntil(() => UiIsWindowVisible(_videoHost.Handle), "diagnostics full-screen video host is visible");
            ShowPlayerOverlay();
            UpdateVideoHost();
            await UiCheckDiagnosticsPlacementAsync("full-screen pillarboxed video");
            double scale = VideoHostSlot.XamlRoot.RasterizationScale;
            RectInt32 videoSlot = GetVideoSlotBounds(scale) ?? throw new InvalidOperationException("Missing diagnostics video slot.");
            RectInt32 information = GetMediaDiagnosticsBounds(scale) ?? throw new InvalidOperationException("Missing diagnostics bounds.");
            RectInt32 controls = GetPlayerOverlayBounds(scale) ?? throw new InvalidOperationException("Missing control bounds.");
            nint region = UiCreateRectRgn(0, 0, 0, 0);
            try
            {
                UiCheck(region != 0 && UiGetWindowRgn(_videoHost.Handle, region) > 0 &&
                    UiPointInRegion(region, information.X - videoSlot.X + information.Width / 2,
                        information.Y - videoSlot.Y + information.Height / 2) &&
                    !UiPointInRegion(region, controls.X - videoSlot.X + controls.Width / 2,
                        controls.Y - videoSlot.Y + controls.Height / 2) &&
                    UiPointInRegion(region, videoSlot.Width / 2, videoSlot.Height / 2),
                    "full-screen transport is excluded while video remains beneath alpha diagnostics");
                SetPlayerOverlayVisible(false);
                UiCheck(VideoDiagnosticsPanel.Visibility == Visibility.Visible && UiIsWindowVisible(UiDiagnosticsOverlay()) &&
                    (UiGetWindowRgn(_videoHost.Handle, region) == 0 ||
                        UiPointInRegion(region, information.X - videoSlot.X + information.Width / 2,
                            information.Y - videoSlot.Y + information.Height / 2) &&
                        UiPointInRegion(region, controls.X - videoSlot.X + controls.Width / 2,
                            controls.Y - videoSlot.Y + controls.Height / 2)),
                    "hiding full-screen transport keeps alpha diagnostics visible over the intact native video");
            }
            finally { if (region != 0) UiDeleteObject(region); }
            await UiCaptureAsync(output, "diagnostics-video-fullscreen");
            _preferences = _preferences with { OriginalSize = true };
            ApplyMediaState(video with { Diagnostics = video.Diagnostics with { Width = 640, Height = 360 } });
            SetPlayerFrameSize(640, 360);
            UpdateVideoHost();
            await UiWaitForStableLayout(VideoHostSlot, VideoDiagnosticsPanel);
            await UiCheckDiagnosticsPlacementAsync("full-screen original-size video smaller than the screen");
            UiCheck(UiIsWindowVisible(UiDiagnosticsOverlay()) &&
                UiGetWindowRect(_videoHost.Handle, out var smallHost) && smallHost.Width == 640 && smallHost.Height == 360,
                "full-screen diagnostics remain visible at the screen edge outside a smaller original-size video host");
            await UiCaptureAsync(output, "diagnostics-video-fullscreen-original-small");
            _preferences = _preferences with { OriginalSize = false };
            SetPlayerFullScreen(false);
            Texts.SetEnglish(false);
            RefreshDynamicText();
            UiCheck(VideoDiagnosticsText.Text.Contains("分辨率") && !VideoDiagnosticsText.Text.Contains("Resolution") &&
                Texts.Disconnect == "断开连接", "diagnostics and disconnect label update to Chinese");
            ApplyMediaState(video with { Session = 601, Revision = 601, Diagnostics = default });
            UiCheck(!VideoDiagnosticsText.Text.Contains("1920") && !VideoDiagnosticsText.Text.Contains("H.264") &&
                !VideoDiagnosticsText.Text.Contains("Mbps"), "replacement source cannot retain old video diagnostics");

            SetVideoAvailable(false);
            var music = new ReceiverMediaState(602, ReceiverMediaKind.Audio, true, "Diagnostics track", "Test artist", "", 0,
                180_000, 0, 0, MediaControls.None)
            {
                Connected = true, Source = "audio", Session = 602,
                Diagnostics = new() { AudioCodec = "ALAC", SampleRate = 44100, Channels = 2, AudioBitrate = 780_000 }
            };
            ApplyMediaState(music);
            UiCheck(!UiIsWindowVisible(UiDiagnosticsOverlay()), "music hides the native video information overlay");
            await UiWaitForStableLayout(MusicDiagnosticsPanel, MusicArtworkFrame);
            UiBounds audioInfo = UiMeasure(MusicDiagnosticsPanel), artwork = UiMeasure(MusicArtworkFrame);
            UiCheck(MusicDiagnosticsPanel.Visibility == Visibility.Visible && VideoDiagnosticsPanel.Visibility == Visibility.Collapsed &&
                MusicDiagnosticsText.Text.Contains("ALAC") && MusicDiagnosticsText.Text.Contains("44.1 kHz") &&
                !MusicDiagnosticsText.Text.Contains("fps") && !MusicDiagnosticsText.Text.Contains("分辨率"),
                "music diagnostics show audio properties without video statistics");
            UiCheck(audioInfo.Y + audioInfo.Height <= artwork.Y &&
                Math.Abs(audioInfo.X + audioInfo.Width / 2 - artwork.X - artwork.Width / 2) <= 1,
                "music diagnostics are centered directly above the album cover");
            await UiCaptureAsync(output, "diagnostics-music-zh");
            bool disconnectEnabled = MusicDisconnectButton.IsEnabled;
            try
            {
                // The receiver is deliberately stopped, so enable this real XAML
                // button only as an input focus target. Never invoke its action.
                MusicDisconnectButton.IsEnabled = true;
                UiCheck(MusicDisconnectButton.Focus(FocusState.Programmatic), "focus the owned music button for routed F12");
                nint focused = UiKeyboardFocus(), window = WindowNative.GetWindowHandle(this);
                UiCheck(focused != 0 && (focused == window || UiIsChildWindow(window, focused)),
                    "routed F12 target belongs to the test window");
                UiCheck(UiPostMessage(focused, 0x0100, 0x7B, 1), "queue F12 to the owned focused music window");
                await UiWaitUntil(() => !_showMediaDiagnostics, "routed music F12 hides diagnostics");
                UiCheck(UiPostMessage(focused, 0x0100, 0x7B, (nint)(1L << 30 | 1)),
                    "queue held F12 repeat to the owned music window");
                await Task.Delay(120);
                UiCheck(!_showMediaDiagnostics && MusicDiagnosticsPanel.Visibility == Visibility.Collapsed,
                    "held routed F12 does not repeatedly toggle music diagnostics");
            }
            finally { MusicDisconnectButton.IsEnabled = disconnectEnabled; }
            ToggleMediaDiagnostics();
            ApplyMediaState(ReceiverMediaState.Empty);
            UiCheck(MusicDiagnosticsPanel.Visibility == Visibility.Collapsed && VideoDiagnosticsPanel.Visibility == Visibility.Collapsed &&
                MusicDiagnosticsText.Text.Length == 0 && VideoDiagnosticsText.Text.Length == 0,
                "ending playback hides both diagnostic panels and clears their text");
        }
        finally
        {
            SetVideoAvailable(false);
            SetPlayerFullScreen(false);
            _preferences = preferences;
            _showMediaDiagnostics = diagnostics;
            Texts.SetEnglish(english);
            ApplyMediaState(state);
            RefreshDynamicText();
        }
    }

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
                await UiCheckPlaybackEnglishAsync(PlayerControls, "video controls " + fullScreen);
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
            await UiCheckPlaybackEnglishAsync(MusicPanel, "music controls");
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

    private async Task UiTestPairingReadRecoveryAsync()
    {
        UiCheck(UiTestsAreConfigured() && _receiver is null && !_hasVideo,
            "pairing recovery fixture uses marked disposable state and a stopped receiver");
        string path = Path.Combine(_root, ".clearcast-state", "clients.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bool existed = File.Exists(path);
        byte[]? original = existed ? await File.ReadAllBytesAsync(path) : null;
        string feedback = _pairingResetFeedback;
        try
        {
            await using (var oversized = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                string key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
                await oversized.WriteAsync(System.Text.Encoding.UTF8.GetBytes(key + ",date,id,Fixture\n"));
                oversized.SetLength(16 * 1024 * 1024 + 1);
            }
            RefreshSavedPairings();
            UiCheck(_pairingLoadFailed && _pairingResetFeedback == PairingReadError &&
                PairingResetFeedback.Visibility == Visibility.Visible && PairingDeviceList.Items.Count == 0 &&
                PairingListFooter.Text.Length == 0 && RepairPairingButton.IsEnabled,
                "oversized pairing registry shows its read error without partial rows and keeps explicit clear-all recovery enabled");
            UpdateSavedPairingButtons(true);
            UiCheck(!RepairPairingButton.IsEnabled, "pairing read recovery retains the existing busy-operation guard");
            UpdateSavedPairingButtons(false);
            await File.WriteAllBytesAsync(path, Array.Empty<byte>());
            RefreshSavedPairings();
            UiCheck(!_pairingLoadFailed && _pairingResetFeedback.Length == 0 && PairingDeviceList.Items.Count == 0 &&
                !RepairPairingButton.IsEnabled,
                "a valid empty pairing registry clears the read error and restores the ordinary disabled clear-all action");
        }
        finally
        {
            if (existed) await File.WriteAllBytesAsync(path, original!);
            else File.Delete(path);
            RefreshSavedPairings();
            ShowPairingResetFeedback(feedback);
        }
    }

    private async Task UiTestRenderingBackpressureAsync()
    {
        await UiWaitUntil(() => !_videoHostUpdateQueued, "prior video layout request settles");
        int callbacks = _uiVideoHostUpdateCallbacks;
        for (int i = 0; i < 5000; i++) QueueVideoHostUpdate();
        UiCheck(_videoHostUpdateQueued && _uiVideoHostUpdateCallbacks == callbacks,
            "video geometry burst queues one deferred callback");
        await UiWaitUntil(() => !_videoHostUpdateQueued, "coalesced video geometry callback completes");
        UiCheck(_uiVideoHostUpdateCallbacks == callbacks + 1,
            "five thousand video geometry requests produce one current-state update");

        using (var retiredHost = new NativeVideoHost(WindowNative.GetWindowHandle(this), DispatcherQueue))
        {
            int retiredCalls = 0;
            retiredHost.UiQueueInput(() => retiredCalls++);
            retiredHost.Dispose();
            await Task.Delay(40);
            UiCheck(retiredCalls == 0, "input queued by a disposed video host cannot affect its replacement");
        }

        var svgGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var svg = new SvgFrameAnimation())
        {
            svg.UiBeforeFrameDecodeAsync = () => svgGate.Task;
            try
            {
                svg.UiRequestRasterization(64, 40);
                for (int i = 0; i < 5000; i++) svg.UiRequestRasterization(65 + i % 2, 40);
                UiCheck(svg.UiDecodeGenerationStarts == 1 && svg.UiFrameDecodeActive == 1 && svg.UiRasterizationPending,
                    "SVG resize burst retains one active decoder generation and one latest size");
                svgGate.TrySetResult(true);
                await UiWaitUntil(() => svg.LoadedFrameCount == SvgFrameAnimation.FrameCount && !svg.UiRasterizationPending &&
                    svg.UiFrameDecodeActive == 0, "latest SVG size finishes after blocked decoder", 15000);
                UiCheck(svg.UiDecodeGenerationStarts == 2 && svg.UiMaxFrameDecodeActive <= 4 &&
                    svg.DecodedPixelWidth == 66 && svg.DecodedPixelHeight == 40 && svg.LoadError is null,
                    "SVG decode work stays bounded across generations and applies only the final requested size");

                var disposeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                svg.UiBeforeFrameDecodeAsync = () => disposeGate.Task;
                svg.UiRequestRasterization(67, 40);
                svg.UiRequestRasterization(68, 40);
                svg.Dispose();
                disposeGate.TrySetResult(true);
                await UiWaitUntil(() => svg.UiFrameDecodeActive == 0, "disposed SVG worker exits");
                int starts = svg.UiDecodeGenerationStarts;
                svg.UiRequestRasterization(69, 40);
                UiCheck(!svg.UiRasterizationPending && svg.UiDecodeGenerationStarts == starts && svg.TargetPixelWidth == 68,
                    "disposing SVG revokes its pending size and rejects later decode requests");
            }
            finally { svgGate.TrySetResult(true); }
        }

        var backdrop = new MusicBackdrop { Width = 1, Height = 1, Opacity = 0 };
        var imageGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeImageGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        WindowLayout.Children.Add(backdrop);
        try
        {
            await UiWaitUntil(() => backdrop.IsLoaded, "isolated backdrop control loads");
            byte[] Frame(byte red, byte blue)
            {
                var frame = new byte[ReceiverVideoFrame.ByteCount];
                for (int i = 0; i < frame.Length; i += 4)
                { frame[i] = blue; frame[i + 1] = 50; frame[i + 2] = red; frame[i + 3] = 255; }
                return frame;
            }
            byte[] firstFrame = Frame(210, 25), latestFrame = Frame(25, 210);
            backdrop.UiBeforeImageDecodeAsync = () => imageGate.Task;
            Task first = backdrop.SetVideoFrameAsync(firstFrame, false, false), latest = first;
            bool replacedComplete = true;
            for (int i = 0; i < 5000; i++)
            {
                Task previous = latest;
                latest = backdrop.SetVideoFrameAsync(latestFrame, false, false);
                if (i > 0) replacedComplete &= previous.IsCompleted;
            }
            UiCheck(replacedComplete && backdrop.UiImageDecodeStarts == 1 && backdrop.UiImageDecodeActive == 1 && backdrop.UiImagePending,
                "backdrop burst keeps one decoder and one latest request while completing superseded callers");
            imageGate.TrySetResult(true);
            await Task.WhenAll(first, latest).WaitAsync(TimeSpan.FromSeconds(15));
            UiCheck(backdrop.UiImageDecodeStarts == 2 && backdrop.UiMaxImageDecodeActive == 1 && !backdrop.UiImagePending &&
                backdrop.UiImageDecodeActive == 0 && backdrop.TextureFrameCount == 1 && backdrop.Palette == MusicPalette.FromBgra(latestFrame, false),
                "backdrop decoder and upload stay single-flight and apply the latest frame");

            backdrop.UiBeforeImageDecodeAsync = () => clearGate.Task;
            Task beforeClear = backdrop.SetVideoFrameAsync(firstFrame, false, false);
            Task superseded = backdrop.SetVideoFrameAsync(latestFrame, false, false);
            await backdrop.SetArtworkAsync(null, false);
            UiCheck(!backdrop.HasArtworkFlow && !backdrop.UiImagePending && superseded.IsCompleted && !beforeClear.IsCompleted,
                "clearing backdrop removes visible and pending imagery immediately behind a stalled decoder");
            clearGate.TrySetResult(true);
            await beforeClear.WaitAsync(TimeSpan.FromSeconds(10));
            UiCheck(!backdrop.HasArtworkFlow && backdrop.UiImageDecodeStarts == 3,
                "cleared stale backdrop work cannot recreate the old image");

            backdrop.UiBeforeImageDecodeAsync = () => disposeImageGate.Task;
            Task beforeDispose = backdrop.SetVideoFrameAsync(firstFrame, false, false);
            Task pendingDispose = backdrop.SetVideoFrameAsync(latestFrame, false, false);
            backdrop.Dispose();
            UiCheck(pendingDispose.IsCompleted && !backdrop.UiImagePending && !backdrop.HasArtworkFlow,
                "disposing backdrop releases its pending request without waiting for the active decoder");
            disposeImageGate.TrySetResult(true);
            await beforeDispose.WaitAsync(TimeSpan.FromSeconds(10));
            UiCheck(backdrop.UiImageDecodeActive == 0 && !backdrop.HasArtworkFlow,
                "disposed backdrop rejects stale upload completion");
        }
        finally
        {
            imageGate.TrySetResult(true);
            clearGate.TrySetResult(true);
            disposeImageGate.TrySetResult(true);
            backdrop.Dispose();
            WindowLayout.Children.Remove(backdrop);
        }
        _uiTestLayouts.Add(new { kind = "rendering-backpressure", burst = 5000, svgWorkerLimit = 4,
            backdropWorkerLimit = 1, pendingRequestLimit = 1 });
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
