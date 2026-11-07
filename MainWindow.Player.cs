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
    private double _playerOverlayFadeFrom;
    private long _playerOverlayFadeStarted;
    private DispatcherQueueTimer? _playerOverlayFadeTimer;

    private NativeVideoHost CreatePlayerVideoHost()
    {
        var host = new NativeVideoHost(WindowNative.GetWindowHandle(this), DispatcherQueue);
        host.DoubleClicked += TogglePlayerFullScreen;
        host.EscapePressed += () => SetPlayerFullScreen(false);
        return host;
    }

    private void SetPlayerFrameSize(int width, int height)
    {
        if (_playerFrameSize.Width == width && _playerFrameSize.Height == height)
            return;
        _playerFrameSize = new SizeInt32(width, height);
        if (_hasVideo)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, UpdateVideoHost);
    }

    private void PositionPlayer(RectInt32 slot, bool visible, double scale)
    {
        if (_videoHost is not { } host)
            return;
        if (!visible)
        {
            // Pre-size the hidden HWND before the decoder creates its swap chain.
            host.Update(slot, false);
            return;
        }

        RectInt32? overlay = GetPlayerOverlayBounds(scale);
        // Use physical pixels for 1:1 output. Clip to the slot if the frame exceeds it.
        if (_preferences.OriginalSize && _playerFrameSize.Width > 0 && _playerFrameSize.Height > 0)
        {
            bool swapDimensions = _playerRotation % 2 != 0;
            int width = swapDimensions ? _playerFrameSize.Height : _playerFrameSize.Width;
            int height = swapDimensions ? _playerFrameSize.Width : _playerFrameSize.Height;
            int x = slot.X + (slot.Width - width) / 2;
            int y = slot.Y + (slot.Height - height) / 2;
            RectInt32? clip = null;
            if (width > slot.Width || height > slot.Height)
            {
                int left = Math.Max(slot.X - x, 0);
                int top = Math.Max(slot.Y - y, 0);
                clip = new RectInt32(left, top, Math.Min(width - left, slot.Width),
                    Math.Min(height - top, slot.Height));
            }
            if (host.Update(new RectInt32(x, y, width, height), true, clip, overlay))
                return;
        }

        host.Update(slot, true, exclusion: overlay);
    }

    private RectInt32? GetPlayerOverlayBounds(double scale)
    {
        if (!PlayerOverlay.IsHitTestVisible || PlayerOverlay.ActualWidth <= 0 || PlayerOverlay.ActualHeight <= 0)
            return null;

        Rect bounds = PlayerOverlay.TransformToVisual(null).TransformBounds(
            new Rect(0, 0, PlayerOverlay.ActualWidth, PlayerOverlay.ActualHeight));
        int left = (int)Math.Floor(bounds.Left * scale);
        int top = (int)Math.Floor(bounds.Top * scale);
        int right = (int)Math.Ceiling(bounds.Right * scale);
        int bottom = (int)Math.Ceiling(bounds.Bottom * scale);
        return new RectInt32(left, top, right - left, bottom - top);
    }

    private void TrackPlayerPointer(DispatcherQueueTimer sender, object args)
    {
        if (!_hasVideo || _closed)
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
            RotateLeftButton.FocusState != FocusState.Keyboard && RotateRightButton.FocusState != FocusState.Keyboard &&
            FullScreenButton.FocusState != FocusState.Keyboard)
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

    private void PlayerOverlayGotFocus(object sender, RoutedEventArgs args)
    {
        if (_hasVideo && args.OriginalSource is Control { FocusState: FocusState.Keyboard })
            ShowPlayerOverlay();
    }

    private void ShowPlayerOverlay()
    {
        _playerOverlayActivity = Stopwatch.GetTimestamp();
        SetPlayerOverlayVisible(true);
    }

    private void SetPlayerOverlayVisible(bool visible, bool animate = true)
    {
        if (animate && _playerOverlayTargetVisible == visible)
            return;

        _playerOverlayTargetVisible = visible;
        double targetOpacity = visible ? 1 : 0;
        if (!animate || !_motionSettings.AnimationsEnabled || !IsMirrorPageSelected || PlayerOverlay.Opacity == targetOpacity)
        {
            StopPlayerOverlayFade();
            ApplyPlayerOverlayOpacity(targetOpacity);
            return;
        }

        _playerOverlayFadeFrom = PlayerOverlay.Opacity;
        _playerOverlayFadeStarted = Stopwatch.GetTimestamp();
        if (_playerOverlayFadeTimer is null)
        {
            // The XAML compositor can be idle while the independent video HWND is playing.
            _playerOverlayFadeTimer = DispatcherQueue.CreateTimer();
            _playerOverlayFadeTimer.Interval = TimeSpan.FromMilliseconds(16);
            _playerOverlayFadeTimer.Tick += AdvancePlayerOverlayFade;
        }
        if (!_playerOverlayFadeTimer.IsRunning)
            _playerOverlayFadeTimer.Start();
    }

    private void AdvancePlayerOverlayFade(DispatcherQueueTimer sender, object args)
    {
        if (!sender.IsRunning)
            return;

        const double durationMilliseconds = 150;
        double targetOpacity = _playerOverlayTargetVisible ? 1 : 0;
        double progress = Stopwatch.GetElapsedTime(_playerOverlayFadeStarted).TotalMilliseconds / durationMilliseconds;
        if (progress >= 1)
        {
            StopPlayerOverlayFade();
            ApplyPlayerOverlayOpacity(targetOpacity);
            return;
        }

        double remaining = 1 - progress;
        double easedProgress = 1 - remaining * remaining * remaining;
        ApplyPlayerOverlayOpacity(_playerOverlayFadeFrom + (targetOpacity - _playerOverlayFadeFrom) * easedProgress);
    }

    private void StopPlayerOverlayFade() => _playerOverlayFadeTimer?.Stop();

    private void ApplyPlayerOverlayOpacity(double opacity)
    {
        bool visible = opacity > 0;
        if (!visible)
            PlayerOverlay.Opacity = 0;

        // The native video overlaps XAML. Reserve the hole before showing the first frame and remove it after the last.
        if (PlayerOverlay.IsHitTestVisible != visible)
        {
            PlayerOverlay.IsHitTestVisible = visible;
            UpdateVideoHost();
        }

        if (visible)
            PlayerOverlay.Opacity = opacity;
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
            PlayerControls.Visibility = _hasVideo && !enabled ? Visibility.Visible : Visibility.Collapsed;
            FullScreenIcon.Symbol = enabled ? Symbol.BackToWindow : Symbol.FullScreen;
            RefreshFullScreenLabel();
            double gutter = MirrorPage.ActualWidth >= 640 ? 40 : 24;
            MirrorLayout.Margin = enabled ? new Thickness(0) : new Thickness(gutter, 16, gutter, 24);
            VideoHostSlot.Margin = new Thickness(enabled ? 0 : 4);
            PlayerFrame.CornerRadius = new CornerRadius(enabled ? 0 : 8);
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
