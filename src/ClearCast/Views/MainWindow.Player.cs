using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace ClearCast;

public sealed partial class MainWindow
{
    private const string PlayerRotationError = "无法旋转屏幕镜像画面，请重试。";
    private bool _playerFullScreen;
    private bool _restoreNavigationPane;
    private Visibility _restoreTitleBar;
    private PlayerWindowPlacement _restoreWindowPlacement;
    private RectInt32? _restoreVideoSlot;
    private RectInt32? _lastPresenterSlot;
    private long _playerPresenterChangedAt;
    private SizeInt32 _playerFrameSize;
    private int _playerRotation;
    private PointInt32? _lastPlayerPointer;
    private long _playerOverlayActivity;
    private bool _playerOverlayTargetVisible;
    private bool _playerControlsPointerOver;
    private bool _videoHostUpdateQueued;
#if CLEARCAST_UI_TESTS
    private int _uiVideoHostUpdateCallbacks;
#endif

    private void QueueVideoHostUpdate()
    {
        if (_videoHostUpdateQueued || _closed) return;
        _videoHostUpdateQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _videoHostUpdateQueued = false;
            if (_closed) return;
#if CLEARCAST_UI_TESTS
            _uiVideoHostUpdateCallbacks++;
#endif
            UpdateVideoHost();
        })) _videoHostUpdateQueued = false;
    }

    private NativeVideoHost CreatePlayerVideoHost()
    {
        var host = new NativeVideoHost(WindowNative.GetWindowHandle(this), DispatcherQueue);
        host.DoubleClicked += TogglePlayerFullScreen;
        host.EscapePressed += () => SetPlayerFullScreen(false);
        host.PlayPausePressed += () => _ = ToggleCurrentPlaybackAsync();
        host.DiagnosticsPressed += ToggleMediaDiagnostics;
        return host;
    }

    private void SetPlayerFrameSize(int width, int height)
    {
        if (_playerFrameSize.Width == width && _playerFrameSize.Height == height)
            return;
        _playerFrameSize = new SizeInt32(width, height);
        if (_hasVideo)
            QueueVideoHostUpdate();
    }

    private void PositionPlayer(RectInt32 slot, bool visible, double scale)
    {
        if (_videoHost is not { } host)
            return;
        var layout = GetPlayerVideoLayout(slot);
        PositionMediaDiagnostics(slot, layout.Picture, scale);
        if (!visible)
        {
            // Pre-size the hidden HWND before the decoder creates its swap chain.
            host.Update(slot, false);
            return;
        }

        host.Update(layout.Host, true, layout.Clip, GetPlayerOverlayBounds(scale),
            _playerFullScreen ? 0 : (int)Math.Round(MusicArtworkFrame.CornerRadius.TopLeft * scale));
        if (GetMediaDiagnosticsBounds(scale) is { } info)
        {
            QueueMediaDiagnosticsRaster(info);
            host.PositionDiagnostics(info, _diagnosticsRasterApplied is { } applied &&
                applied.Width == info.Width && applied.Height == info.Height);
        }
        else host.PositionDiagnostics(default, false);
    }

    private (RectInt32 Host, RectInt32? Clip, RectInt32 Picture) GetPlayerVideoLayout(RectInt32 slot)
    {
        if (_playerFrameSize.Width <= 0 || _playerFrameSize.Height <= 0)
            return (slot, null, slot);

        bool rotated = _playerRotation % 2 != 0;
        int sourceWidth = rotated ? _playerFrameSize.Height : _playerFrameSize.Width;
        int sourceHeight = rotated ? _playerFrameSize.Width : _playerFrameSize.Height;
        // Physical pixels are also used for 1:1 output. Anchor overlays to the
        // visible part of the picture when a native-size frame exceeds the slot.
        if (_preferences.OriginalSize && _playerFrameSize.Width > 0 && _playerFrameSize.Height > 0)
        {
            int width = sourceWidth, height = sourceHeight;
            int x = slot.X + (slot.Width - width) / 2;
            int y = slot.Y + (slot.Height - height) / 2;
            int left = Math.Max(slot.X, x), top = Math.Max(slot.Y, y);
            int right = Math.Min(slot.X + slot.Width, x + width), bottom = Math.Min(slot.Y + slot.Height, y + height);
            var picture = new RectInt32(left, top, right - left, bottom - top);
            RectInt32? clip = null;
            if (width > slot.Width || height > slot.Height)
                clip = new RectInt32(left - x, top - y, picture.Width, picture.Height);
            return (new RectInt32(x, y, width, height), clip, picture);
        }

        double fit = Math.Min((double)slot.Width / sourceWidth, (double)slot.Height / sourceHeight);
        int fitWidth = Math.Max(1, (int)Math.Round(sourceWidth * fit));
        int fitHeight = Math.Max(1, (int)Math.Round(sourceHeight * fit));
        var fitted = new RectInt32(slot.X + (slot.Width - fitWidth) / 2, slot.Y + (slot.Height - fitHeight) / 2, fitWidth, fitHeight);
        // The full-screen sink draws its own letterboxing inside the host.
        return (_playerFullScreen ? slot : fitted, null, fitted);
    }

    private RectInt32? GetPlayerOverlayBounds(double scale)
    {
        if (!_playerFullScreen || !PlayerControls.IsHitTestVisible || PlayerControls.ActualWidth <= 0 || PlayerControls.ActualHeight <= 0)
            return null;

        Rect bounds = PlayerControls.TransformToVisual(null).TransformBounds(
            new Rect(0, 0, PlayerControls.ActualWidth, PlayerControls.ActualHeight));
        int left = (int)Math.Floor(bounds.Left * scale);
        int top = (int)Math.Floor(bounds.Top * scale);
        int right = (int)Math.Ceiling(bounds.Right * scale);
        int bottom = (int)Math.Ceiling(bounds.Bottom * scale);
        return new RectInt32(left, top, right - left, bottom - top);
    }

    private void TrackPlayerPointer(DispatcherQueueTimer sender, object args)
    {
        if (!_hasVideo || !_playerFullScreen || _closed)
            return;

        // The video and its black bars can belong to a child HWND, so XAML pointer events alone are insufficient.
        if (GetCursorPos(out PointInt32 pointer) &&
            (_lastPlayerPointer is not { } last || last.X != pointer.X || last.Y != pointer.Y))
        {
            _lastPlayerPointer = pointer;
            if (IsPointerOverPlayer(pointer))
                ShowPlayerOverlay();
        }

        if (_playerOverlayTargetVisible && Stopwatch.GetElapsedTime(_playerOverlayActivity) >= TimeSpan.FromSeconds(3) &&
            !_playerControlsPointerOver && _playbackSeekSlider is null && !PlayerControlsHaveKeyboardFocus() &&
            !(_mediaState.IsDirectVideo && !_mediaState.Playing))
        {
            SetPlayerOverlayVisible(false);
        }
    }

    private bool IsPointerOverPlayer(PointInt32 pointer)
    {
        if (VideoHostSlot.XamlRoot is not { } root || GetVideoSlotBounds(root.RasterizationScale) is not { } slot)
            return false;

        nint window = WindowNative.GetWindowHandle(this);
        nint pointerWindow = WindowFromPoint(pointer);
        return (pointerWindow == window || IsChild(window, pointerWindow)) && ScreenToClient(window, ref pointer) &&
            pointer.X >= slot.X && pointer.X < slot.X + slot.Width && pointer.Y >= slot.Y && pointer.Y < slot.Y + slot.Height;
    }

    private bool PlayerControlsHaveKeyboardFocus()
    {
        if (WindowLayout.XamlRoot is not { } root ||
            FocusManager.GetFocusedElement(root) is not Control { FocusState: FocusState.Keyboard } focus) return false;
        for (DependencyObject? element = focus; element is not null; element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, PlayerControls)) return true;
        return false;
    }

    private void PlayerControlsGotFocus(object sender, RoutedEventArgs args)
    {
        if (_hasVideo) ShowPlayerOverlay();
    }

    private void PlayerControlsPointerEntered(object sender, PointerRoutedEventArgs args) => _playerControlsPointerOver = true;
    private void PlayerControlsPointerExited(object sender, PointerRoutedEventArgs args) => _playerControlsPointerOver = false;

    private void PlayerControlsSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_playerFullScreen) UpdateVideoHost();
    }

    private void ShowPlayerOverlay()
    {
        _playerOverlayActivity = Stopwatch.GetTimestamp();
        SetPlayerOverlayVisible(true);
    }

    private void SetPlayerOverlayVisible(bool visible)
    {
        visible = _hasVideo && (visible || !_playerFullScreen);
        _playerOverlayTargetVisible = visible;
        PlayerControls.Opacity = visible ? 1 : 0;
        if (PlayerControls.IsHitTestVisible == visible) return;
        PlayerControls.IsHitTestVisible = visible;
        UpdateVideoHost();
    }

    private void RotateLeftClicked(object sender, RoutedEventArgs args) => RotatePlayer(-1);

    private void RotateRightClicked(object sender, RoutedEventArgs args) => RotatePlayer(1);

    private void RotatePlayer(int quarterTurns)
    {
        if (!_hasVideo || _receiver is not { } receiver || _resettingPairings || _stopTask is not null || _closing)
            return;

        int rotation = (_playerRotation + quarterTurns + 4) % 4;
        try
        {
            receiver.SetVideoRotation(rotation);
            _playerRotation = rotation;
            if (_receiveErrorMessage == PlayerRotationError)
                ReceiverInfoBar.IsOpen = false;
            ShowPlayerOverlay();
            UpdateVideoHost();
        }
        catch (Exception error) when (error is Win32Exception or EntryPointNotFoundException)
        {
            ShowReceiveError(PlayerRotationError, ErrorCode(error));
        }
    }

    private void RefreshFullScreenLabel()
    {
        string label = _playerFullScreen ? Texts.ExitFullScreen : Texts.FullScreen;
        AutomationProperties.SetName(FullScreenButton, label);
        ToolTipService.SetToolTip(FullScreenButton, label);
    }

    private void PlayerFullScreenClicked(object sender, RoutedEventArgs args) => TogglePlayerFullScreen();

    private void PlayerFrameDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (!_hasVideo)
            return;
        args.Handled = true;
        TogglePlayerFullScreen();
    }

    private void TogglePlayerFullScreen()
    {
        if (_hasVideo && IsMirrorPageSelected && !IsMirrorPageEntering)
            SetPlayerFullScreen(!_playerFullScreen);
    }

    private void ApplyAutoFullScreenAtVideoStart()
    {
        // Call only when video becomes available. Later frames and preference
        // changes must respect a user's choice to leave full screen mid-session.
        if (!_preferences.AutoFullScreen || !_hasVideo || _closing || _closed ||
            _stopTask is not null || _resettingPairings)
            return;

        MainNavigation.SelectedItem = MirrorNavigationItem;
        SetPlayerFullScreen(true);
    }

    private void SetPlayerFullScreen(bool enabled)
    {
        if (_playerFullScreen == enabled)
            return;

        nint window = WindowNative.GetWindowHandle(this);
        if (enabled)
        {
            _restoreNavigationPane = MainNavigation.IsPaneVisible;
            _restoreTitleBar = AppTitleBar.Visibility;
            _restoreWindowPlacement.Length = Marshal.SizeOf<PlayerWindowPlacement>();
            if (!GetWindowPlacement(window, ref _restoreWindowPlacement))
                _restoreWindowPlacement = default;
        }

        // Keep the independently composed video HWND out of DWM's presenter transition.
        _videoHost?.SuspendPresentation();
        StopPageEntrance();
        if (enabled)
            _restoreVideoSlot = VideoHostSlot.XamlRoot is { } root ? GetVideoSlotBounds(root.RasterizationScale) : null;
        const uint transitionsForceDisabled = 3;
        bool transitionsDisabled = DwmSetWindowAttribute(window, transitionsForceDisabled, 1, sizeof(int)) >= 0;
        try
        {
            AppWindow.SetPresenter(enabled ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
            if (!enabled && _restoreWindowPlacement.Length != 0)
                SetWindowPlacement(window, in _restoreWindowPlacement);
            _playerFullScreen = enabled;
            AppTitleBar.Visibility = enabled ? Visibility.Collapsed : _restoreTitleBar;
            MainNavigation.IsPaneVisible = !enabled && _restoreNavigationPane;
            PlayerControls.Visibility = _hasVideo ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRow(PlayerControls, enabled ? 1 : 2);
            PlayerControls.VerticalAlignment = enabled ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
            PlayerControls.Margin = enabled ? new Thickness(0) : new Thickness(0, 12, 0, 0);
            PlayerControls.Padding = enabled ? new Thickness(24, 8, 24, 16) : new Thickness(16, 8, 16, 12);
            PlayerControls.CornerRadius = new CornerRadius(enabled ? 0 : 8);
            PlayerControls.RequestedTheme = enabled ? ElementTheme.Dark : ElementTheme.Default;
            FullScreenIcon.Symbol = enabled ? Symbol.BackToWindow : Symbol.FullScreen;
            RefreshFullScreenLabel();
            double gutter = MirrorPage.ActualWidth >= 640 ? 40 : 24;
            MirrorLayout.Margin = enabled ? new Thickness(0) : new Thickness(gutter, 16, gutter, 24);
            VideoHostSlot.Margin = new Thickness(0);
            PlayerFrame.CornerRadius = new CornerRadius(enabled ? 0 : 8);
            PlayerFrame.Background = new SolidColorBrush(enabled ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.Transparent);
            if (enabled && _hasVideo) _playerOverlayTimer.Start();
            else _playerOverlayTimer.Stop();
            UpdateMusicBackgroundState();
            if (_hasVideo)
                ShowPlayerOverlay();
        }
        finally
        {
            if (transitionsDisabled)
                DwmSetWindowAttribute(window, transitionsForceDisabled, 0, sizeof(int));

            CompositionTarget.Rendering -= OnPlayerPresenterRendering;
            if (_hasVideo && _videoHost is not null)
            {
                _playerPresenterChangedAt = Stopwatch.GetTimestamp();
                _lastPresenterSlot = null;
                CompositionTarget.Rendering += OnPlayerPresenterRendering;
            }
            else
            {
                _videoHost?.ResumePresentation();
            }
        }
    }

    private void OnPlayerPresenterRendering(object? sender, object args)
    {
        UpdateVideoHost();
        if (_hasVideo && VideoHostSlot.XamlRoot is { } root && GetVideoSlotBounds(root.RasterizationScale) is { } slot)
        {
            // Window placement can be restored while NavigationView still reports an intermediate slot.
            bool stable = _lastPresenterSlot.Equals(slot);
            _lastPresenterSlot = slot;
            bool expected = _playerFullScreen || _restoreVideoSlot is null || _restoreVideoSlot.Equals(slot);
            bool timedOut = Stopwatch.GetElapsedTime(_playerPresenterChangedAt) >= TimeSpan.FromMilliseconds(350);
            if (!stable || (!timedOut && (!expected || _videoHost is { HasCurrentSurfaceSize: false })))
                return;
        }

        CancelPlayerPresenterTransition();
        UpdateVideoHost();
    }

    private void CancelPlayerPresenterTransition()
    {
        CompositionTarget.Rendering -= OnPlayerPresenterRendering;
        _videoHost?.ResumePresentation();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PlayerWindowPlacement
    {
        public int Length;
        private int _flags;
        private int _showCommand;
        private PointInt32 _minimumPosition;
        private PointInt32 _maximumPosition;
        private RectInt32 _normalPosition; // Win32 RECT, passed back without interpretation.
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint window, ref PlayerWindowPlacement placement);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(nint window, in PlayerWindowPlacement placement);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, in int value, int size);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointInt32 point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint WindowFromPoint(PointInt32 point);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint window, ref PointInt32 point);
}
