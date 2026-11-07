using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Color = Windows.UI.Color;
using Colors = Microsoft.UI.Colors;

namespace ClearCast;

public sealed partial class MainWindow : Window
{
    private const int MinimumWindowWidth = 640;
    private const int MinimumWindowHeight = 480;
    private const double WindowWorkAreaRatio = 0.90;
    private readonly string _root = FindRoot();
    private readonly DispatcherQueueTimer _receiverTimer;
    private readonly DispatcherQueueTimer _pairingTimer;
    private readonly DispatcherQueueTimer _playerOverlayTimer;
    private ThemeSettings? _captionThemeSettings;
    private NativeVideoHost? _videoHost;
    private AirPlayReceiver? _receiver;
    private ReceiverPreferences _preferences;
    private Task<bool>? _stopTask;
    private bool _hasVideo;
    private bool _closing;
    private bool _closed;
    private bool _startingReceiver;
    private bool _receiverReady;
#if CLEARCAST_UI_TESTS
    private double _uiStartupPollHooksMaxMilliseconds;
    private double _uiStartupPollReadEventMaxMilliseconds;
    private int _uiStartupPollCount;
#endif
    private bool _settingsReady;
    private bool _updatingSettingsSelection;
    private DecoderMode? _pendingDecoder;
    private int _receiveErrorCode;
    private int _pairingGeneration;
    private int _latestPairingGeneration;
    private int _streamWidth;
    private int _streamHeight;
    private long _sampledAt;
    private ulong _previousRendered;
    private ulong _previousDropped;
    private ulong _earlierDropped;

    public MainWindow()
    {
        _preferences = ReceiverPreferences.Load(_root);
        Texts.SetEnglish(_preferences.Language == AppLanguage.English);
        InitializeComponent();
        ApplyAppTheme();
        WindowLayout.Language = Texts.IsEnglish ? "en-US" : "zh-CN";
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "ClearCast.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            _captionThemeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
            _captionThemeSettings.Changed += CaptionSystemThemeChanged;
            WindowLayout.ActualThemeChanged += (_, _) => UpdateCaptionColors();
            UpdateCaptionColors();
        }
        else
        {
            AppTitleBar.Visibility = Visibility.Collapsed;
        }
        VideoHostSlot.Loaded += OnVideoSlotLoaded;
        VideoHostSlot.LayoutUpdated += (_, _) => UpdateVideoHost();
        MainNavigation.DisplayModeChanged += (_, args) => UpdateNavigationInsets(args.DisplayMode);
        UpdateNavigationInsets(MainNavigation.DisplayMode);
        InitializeNavigation();
        UpdateDecoderSelection();
        DisplaySizeButton.SelectedItem = _preferences.OriginalSize ? OriginalDisplayItem : FitDisplayItem;
        ThemeButton.SelectedItem = _preferences.Theme switch
        {
            AppTheme.Light => LightThemeItem,
            AppTheme.Dark => DarkThemeItem,
            _ => SystemThemeItem
        };
        LanguageButton.SelectedItem = Texts.IsEnglish ? EnglishLanguageItem : ChineseLanguageItem;
        _settingsReady = true;

        _receiverTimer = DispatcherQueue.CreateTimer();
        _receiverTimer.Interval = TimeSpan.FromMilliseconds(150);
        _receiverTimer.Tick += PollReceiver;
        _pairingTimer = DispatcherQueue.CreateTimer();
        _pairingTimer.Interval = TimeSpan.FromMinutes(1);
        _pairingTimer.IsRepeating = false;
        _pairingTimer.Tick += (_, _) => HidePairingCode();
        _playerOverlayTimer = DispatcherQueue.CreateTimer();
        _playerOverlayTimer.Interval = TimeSpan.FromMilliseconds(50);
        _playerOverlayTimer.Tick += TrackPlayerPointer;
        RefreshDynamicText();
        AppWindow.Closing += WindowClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            AppWindow.Changed -= WindowChanged;
            CancelPlayerPresenterTransition();
            StopPageEntrance();
            if (_captionThemeSettings is not null)
                _captionThemeSettings.Changed -= CaptionSystemThemeChanged;
            _receiverTimer.Stop();
            _pairingTimer.Stop();
            _playerOverlayTimer.Stop();
            StopPlayerOverlayFade();
            LandingAnimation.Dispose();
            _videoHost?.Dispose();
        };
    }

    private async void WindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_receiver is null)
            return;

        args.Cancel = true;
        if (_closing)
            return;

        _closing = true;
        UpdateLandingAnimationState();
        try
        {
            if (await StopReceiverAsync())
                Close();
        }
        finally
        {
            _closing = false;
            if (!_closed)
            {
                UpdateReceiverButtons();
                UpdateLandingAnimationState();
            }
        }
    }

    private void OnVideoSlotLoaded(object sender, RoutedEventArgs e)
    {
        VideoHostSlot.Loaded -= OnVideoSlotLoaded;
        var root = VideoHostSlot.XamlRoot;
        double scale = root.RasterizationScale;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        UpdateMinimumWindowSize(scale, display.WorkArea);
        AppWindow.Resize(new SizeInt32(
            Math.Min((int)Math.Round(1120 * scale), (int)(display.WorkArea.Width * WindowWorkAreaRatio)),
            Math.Min((int)Math.Round(760 * scale), (int)(display.WorkArea.Height * WindowWorkAreaRatio))));
        AppWindow.Changed += WindowChanged;
        root.Changed += (_, _) =>
        {
            if (scale != root.RasterizationScale)
            {
                scale = root.RasterizationScale;
                UpdateMinimumWindowSize(scale, DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea);
            }
            UpdateVideoHost();
            UpdateLandingAnimationState();
        };
        UpdateLandingAnimationState();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, StartAirPlay);
    }

    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        UpdateLandingAnimationState();
        if ((args.DidPositionChange || args.DidPresenterChange) && WindowLayout.XamlRoot is { } root)
            UpdateMinimumWindowSize(root.RasterizationScale, DisplayArea.GetFromWindowId(sender.Id, DisplayAreaFallback.Nearest).WorkArea);
    }

    private void UpdateMinimumWindowSize(double scale, RectInt32 workArea)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;

        // The presenter expects physical pixels. Keep the minimum within the startup work area cap.
        int width = Math.Min((int)Math.Round(MinimumWindowWidth * scale), (int)(workArea.Width * WindowWorkAreaRatio));
        int height = Math.Min((int)Math.Round(MinimumWindowHeight * scale), (int)(workArea.Height * WindowWorkAreaRatio));
        if (presenter.PreferredMinimumWidth != width)
            presenter.PreferredMinimumWidth = width;
        if (presenter.PreferredMinimumHeight != height)
            presenter.PreferredMinimumHeight = height;
    }

    private void UpdateNavigationInsets(NavigationViewDisplayMode mode)
    {
        double inset = mode == NavigationViewDisplayMode.Expanded ? 12 : 0;
        MirrorNavigationItem.Margin = new Thickness(inset, 8, inset, 0);
        SettingsNavigationItem.Margin = new Thickness(inset, 0, inset, 0);
        AboutNavigationItem.Margin = new Thickness(inset, 0, inset, 8);
    }

    private void PageSizeChanged(object sender, SizeChangedEventArgs args)
    {
        double gutter = args.NewSize.Width >= 640 ? 40 : 24;
        FrameworkElement layout = ReferenceEquals(sender, SettingsPage) ? SettingsLayout
            : ReferenceEquals(sender, AboutPage) ? AboutLayout
            : MirrorLayout;
        layout.Margin = ReferenceEquals(sender, MirrorPage) && _playerFullScreen
            ? new Thickness(0) : new Thickness(gutter, 16, gutter, 24);
        if (ReferenceEquals(sender, SettingsPage) && args.NewSize.Width != args.PreviousSize.Width)
            UpdateSettingsCardLayout(args.NewSize.Width - 2 * gutter);
    }

    private void UpdateSettingsCardLayout(double width)
    {
        bool stackControls = width < 440;
        // Account for the two card borders, horizontal padding, and the leading icon column and gap.
        double availableControlWidth = Math.Max(0, width - (2 + 32 + 24 + 16));
        foreach (var control in new[] { ThemeButton, LanguageButton, DecoderButton, DisplaySizeButton })
        {
            Grid.SetRow(control, stackControls ? 1 : 0);
            Grid.SetColumn(control, stackControls ? 1 : 2);
            Grid.SetColumnSpan(control, stackControls ? 2 : 1);
            control.HorizontalAlignment = stackControls ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            control.Margin = stackControls ? new Thickness(0, 8, 0, 0) : new Thickness(0);
            control.MaxWidth = stackControls ? availableControlWidth : double.PositiveInfinity;
        }
    }

    private void LandingViewportChanged(object sender, SizeChangedEventArgs args)
    {
        LandingLayout.MinHeight = args.NewSize.Height;
    }

    private void SettingsChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || _updatingSettingsSelection || e.AddedItems.Count == 0)
            return;

        object selected = e.AddedItems[0];
        if (ReferenceEquals(sender, DecoderButton))
        {
            var requested = ReferenceEquals(selected, HardwareDecoderItem) ? DecoderMode.Hardware
                : ReferenceEquals(selected, SoftwareDecoderItem) ? DecoderMode.Software : DecoderMode.Automatic;
            ChangeDecoder(requested);
            UpdateDecoderSelection();
            return;
        }
        if (ReferenceEquals(sender, DisplaySizeButton))
        {
            _preferences = _preferences with { OriginalSize = ReferenceEquals(selected, OriginalDisplayItem) };
            UpdateVideoHost();
        }
        else if (ReferenceEquals(sender, ThemeButton))
        {
            var theme = ReferenceEquals(selected, LightThemeItem) ? AppTheme.Light
                : ReferenceEquals(selected, DarkThemeItem) ? AppTheme.Dark : AppTheme.System;
            _preferences = _preferences with { Theme = theme };
            ApplyAppTheme();
        }
        else
        {
            bool english = ReferenceEquals(selected, EnglishLanguageItem);
            _preferences = _preferences with { Language = english ? AppLanguage.English : AppLanguage.SimplifiedChinese };
            Texts.SetEnglish(english);
            WindowLayout.Language = english ? "en-US" : "zh-CN";
            RefreshComboBoxLabels();
            RefreshDynamicText();
            RefreshSavedPairings();
        }
        SavePreferences();
    }

    private void ApplyAppTheme()
    {
        WindowLayout.RequestedTheme = _preferences.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void CaptionSystemThemeChanged(ThemeSettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateCaptionColors);

    private void UpdateCaptionColors()
    {
        if (_closed || _captionThemeSettings is null)
            return;

        var titleBar = AppWindow.TitleBar;
        bool custom = !_captionThemeSettings.HighContrast;
        bool dark = WindowLayout.ActualTheme == ElementTheme.Dark;
        byte ink = dark ? (byte)255 : (byte)0;
        byte inactiveInk = dark ? (byte)153 : (byte)102;
        Color? foreground = custom ? Color.FromArgb(255, ink, ink, ink) : null;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = custom ? Color.FromArgb(255, inactiveInk, inactiveInk, inactiveInk) : null;
        titleBar.ButtonBackgroundColor = custom ? Colors.Transparent : null;
        titleBar.ButtonInactiveBackgroundColor = custom ? Colors.Transparent : null;
        titleBar.ButtonHoverBackgroundColor = custom ? Color.FromArgb(24, ink, ink, ink) : null;
        titleBar.ButtonPressedBackgroundColor = custom ? Color.FromArgb(40, ink, ink, ink) : null;
    }

    private void RefreshComboBoxLabels()
    {
        // The native ComboBox caches the selected item's text in the collapsed view.
        _updatingSettingsSelection = true;
        try
        {
            foreach (var box in new[] { ThemeButton, DecoderButton, DisplaySizeButton })
            {
                int selectedIndex = box.SelectedIndex;
                box.SelectedIndex = -1;
                box.SelectedIndex = selectedIndex;
            }
        }
        finally
        {
            _updatingSettingsSelection = false;
        }
    }

    private void ChangeDecoder(DecoderMode requested)
    {
        if (_pendingDecoder is not null || requested == _preferences.Decoder)
            return;
        if (_receiver is null)
        {
            FinishDecoderChange(requested, true);
            return;
        }

        try
        {
            _receiver.RequestDecoder(requested);
            _pendingDecoder = requested;
            ShowDecoderFeedback("正在切换解码方式…");
            UpdateReceiverButtons();
        }
        catch (Exception error) when (error is Win32Exception or EntryPointNotFoundException)
        {
            FinishDecoderChange(requested, false, ErrorCode(error));
        }
    }

    private void FinishDecoderChange(DecoderMode requested, bool succeeded, int errorCode = 0)
    {
        _pendingDecoder = null;
        if (succeeded)
        {
            _preferences = _preferences with { Decoder = requested };
            SavePreferences();
        }
        UpdateDecoderSelection();
        ShowDecoderFeedback(succeeded ? "" : errorCode switch
        {
            50 when requested == DecoderMode.Hardware => "当前设备或画面不支持硬件解码，已保留原解码方式。",
            1460 => "画面没有及时更新。请在设备上滑动或切换画面后重试。",
            _ => "暂时无法切换，继续使用当前解码方式。"
        });
        UpdateReceiverButtons();
    }

    private void UpdateDecoderSelection()
    {
        _updatingSettingsSelection = true;
        try
        {
            DecoderButton.SelectedItem = _preferences.Decoder switch
            {
                DecoderMode.Software => SoftwareDecoderItem,
                DecoderMode.Hardware => HardwareDecoderItem,
                _ => AutoDecoderItem
            };
        }
        finally
        {
            _updatingSettingsSelection = false;
        }
    }

    private void ShowDecoderFeedback(string message)
    {
        _decoderFeedback = message;
        DecoderFeedback.Text = Texts.Translate(message);
        DecoderFeedback.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SavePreferences()
    {
        try
        {
            _preferences.Save(_root);
            SettingsSaveHint.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SettingsSaveHint.Visibility = Visibility.Visible;
        }
    }

    private void SetVideoAvailable(bool available)
    {
        if (_hasVideo == available)
            return;

        _hasVideo = available;
        VideoPlaceholder.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        UpdateLandingAnimationState();
        PlayerFrame.Visibility = PlayerControls.Visibility = PlayerOverlay.Visibility =
            available ? Visibility.Visible : Visibility.Collapsed;
        UpdateReceiverButtons();
        if (available)
        {
            ShowPlayerOverlay();
            _playerOverlayTimer.Start();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, UpdateVideoHost);
        }
        else
        {
            _playerOverlayTimer.Stop();
            _lastPlayerPointer = null;
            SetPlayerOverlayVisible(false, animate: false);
            SetPlayerFullScreen(false);
            _playerFrameSize = default;
            _playerRotation = 0;
            _videoHost?.Hide();
        }
    }

    private void UpdateVideoHost()
    {
        if (_videoHost is null || VideoHostSlot.XamlRoot is not { } root)
            return;

        double scale = root.RasterizationScale;
        if (GetVideoSlotBounds(scale) is not { } slot)
        {
            _videoHost.Hide();
            return;
        }
        PositionPlayer(slot, _hasVideo && !IsMirrorPageEntering, scale);
    }

    private RectInt32? GetVideoSlotBounds(double scale)
    {
        if (!IsMirrorPageSelected || VideoHostSlot.ActualWidth <= 0 || VideoHostSlot.ActualHeight <= 0)
            return null;

        Rect slot = VideoHostSlot.TransformToVisual(null).TransformBounds(
            new Rect(0, 0, VideoHostSlot.ActualWidth, VideoHostSlot.ActualHeight));
        int left = (int)Math.Ceiling(slot.Left * scale);
        int top = (int)Math.Ceiling(slot.Top * scale);
        int right = (int)Math.Floor(slot.Right * scale);
        int bottom = (int)Math.Floor(slot.Bottom * scale);
        return right > left && bottom > top ? new RectInt32(left, top, right - left, bottom - top) : null;
    }

    private void RetryReceiveClicked(object sender, RoutedEventArgs e)
    {
        if (!_resettingPairings && _stopTask is null)
            StartAirPlay();
    }

    private async void DisconnectClicked(object sender, RoutedEventArgs e)
    {
        if (!_hasVideo || _resettingPairings || _closing || _stopTask is not null)
            return;
        if (await StopReceiverAsync())
            StartAirPlay();
    }

    private async void StartAirPlay()
    {
        if (_receiver is not null || _startingReceiver || _closing || _closed)
            return;
        _startingReceiver = true;
        UpdateReceiverButtons();
        ReceiverInfoBar.IsOpen = false;
        RetryReceiveButton.Visibility = Visibility.Collapsed;
        try
        {
            // Loading the native module and enumerating adapters can be slow on
            // a cold launch. Let the first frame and landing animation render.
            var startup = await Task.Run(() => (
                Adapter: ReceiverNetwork.FindPreferredAdapter(),
                Name: NativeReceiver.AdvertisedName));
            if (_closing || _closed)
                return;

            _videoHost ??= CreatePlayerVideoHost();
            if (IsMirrorPageSelected && VideoHostSlot.ActualWidth > 0 && VideoHostSlot.ActualHeight > 0)
                UpdateVideoHost();
            else
                _videoHost.Update(new RectInt32(0, 0, Math.Max(320, AppWindow.Size.Width), Math.Max(240, AppWindow.Size.Height)), false);
            Texts.SetReceiverName(startup.Name);
            _receiver = AirPlayReceiver.Start(_root, _preferences.Decoder, _videoHost.Handle, startup.Adapter);
            SetRunning(true);
        }
        catch (Exception error)
        {
            if (_closing || _closed)
                return;
            SetRunning(false);
            string message = error is FileNotFoundException or InvalidOperationException or Win32Exception
                ? error.Message
                : "无法启动接收，请重试。";
            ShowReceiveError(message, ErrorCode(error));
        }
        finally
        {
            _startingReceiver = false;
            if (!_closed)
                UpdateReceiverButtons();
        }
    }

    private async Task<bool> StopReceiverAsync()
    {
        if (_receiver is not { } receiver)
            return true;

        Task<bool> stopping = _stopTask ??= StopReceiverCoreAsync(receiver);
        try
        {
            return await stopping;
        }
        finally
        {
            if (ReferenceEquals(_stopTask, stopping))
                _stopTask = null;
            if (!_closed)
                UpdateReceiverButtons();
        }
    }

    private async Task<bool> StopReceiverCoreAsync(AirPlayReceiver receiver)
    {
        _receiverTimer.Stop();
        PlayerStopButton.IsEnabled = DecoderButton.IsEnabled = false;
        HidePairingCode();
        try
        {
            await receiver.StopAsync();
            _receiver = null;
            SetRunning(false);
            return true;
        }
        catch (Exception error)
        {
            _receiverTimer.Start();
            MainNavigation.SelectedItem = MirrorNavigationItem;
            ShowReceiveError("无法停止接收，请重试。", ErrorCode(error));
            return false;
        }
    }

    private void SetRunning(bool running)
    {
        HidePairingCode();
        _receiverReady = false;
        if (running)
        {
            SetReceiverStatus("正在启动接收器…");
            _receiverTimer.Start();
        }
        else
        {
            _receiverTimer.Stop();
            _pendingDecoder = null;
            UpdateDecoderSelection();
            ShowDecoderFeedback("");
            ResetStreamStats();
            SetVideoAvailable(false);
        }
        UpdateReceiverButtons();
    }

    private void UpdateReceiverButtons()
    {
        bool busy = _resettingPairings || _startingReceiver || _closing || _stopTask is not null;
        PlayerStopButton.IsEnabled = !busy && _receiver is not null;
        RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled = !busy && _hasVideo && _receiver is not null;
        RetryReceiveButton.IsEnabled = !busy;
        DecoderButton.IsEnabled = !busy && _pendingDecoder is null && (_receiver is null || _receiverReady);
        UpdateSavedPairingButtons(busy);
    }

    private void ShowPairingCode(string pin, int generation = 0)
    {
        _pairingGeneration = generation;
        if (generation > 0)
            _latestPairingGeneration = generation;
        SetPairingPrompt("在设备上输入这四位数字。");
        PinText.Text = pin;
        WaitingPairing.Visibility = Visibility.Visible;
        _pairingTimer.Stop();
        _pairingTimer.Start();
    }

    private void PairingCloseClicked(object sender, RoutedEventArgs e) => HidePairingCode();

    private void HidePairingCode()
    {
        _pairingTimer.Stop();
        _pairingGeneration = 0;
        WaitingPairing.Visibility = Visibility.Collapsed;
        PinText.Text = "";
    }

    private void HandlePairingEvent(ReceiverEvent update)
    {
        if (_hasVideo)
            return;

        switch (update.Kind)
        {
            case ReceiverEventKind.PairingAuthenticationRequired:
                // An unknown saved identity can be probed before the device asks
                // for a PIN. It does not establish a failed pairing attempt.
                break;
            case ReceiverEventKind.PairingRequired:
                if (update.Width is >= 1 and <= 9999 && update.Height > 0)
                {
                    ReceiverInfoBar.IsOpen = false;
                    ShowPairingCode(update.Width.ToString("0000", System.Globalization.CultureInfo.InvariantCulture),
                        update.Height);
                }
                break;
            case ReceiverEventKind.PairingRejected:
                bool codeVisible = WaitingPairing.Visibility == Visibility.Visible;
                if (codeVisible)
                {
                    // A different connection or an older challenge must not
                    // replace the instruction for the code currently displayed.
                    if (update.Height != _pairingGeneration)
                        break;
                    SetPairingPrompt((PairingRejectionReason)update.ErrorCode switch
                    {
                        PairingRejectionReason.InvalidSetupData => "配对请求未完成，请在设备上重试。",
                        PairingRejectionReason.PinUnavailableOrExpired => "验证码已失效，请在设备上重新选择 ClearCast 后重试。",
                        PairingRejectionReason.RateLimited => "配对尝试较多，请稍后在设备上重试。",
                        PairingRejectionReason.VerifyRejected => "设备验证未通过，请在设备上重新选择 ClearCast 后重试。",
                        _ => "配对验证未通过，请在设备上重试。"
                    });
                }
                else if (update.ErrorCode == (int)PairingRejectionReason.VerifyRejected &&
                    (update.Height == 0 || update.Height == _latestPairingGeneration))
                {
                    ShowReceiveError("设备验证未通过，请在设备上重新选择 ClearCast 后重试。", 470);
                }
                break;
            case ReceiverEventKind.PairingCompleted:
                if (WaitingPairing.Visibility == Visibility.Visible && update.Height == _pairingGeneration)
                {
                    ReceiverInfoBar.IsOpen = false;
                    HidePairingCode();
                }
                break;
            case ReceiverEventKind.ConnectionRejected:
                if (update.Height > 0 && _latestPairingGeneration > 0 && update.Height != _latestPairingGeneration)
                    break;
                if (WaitingPairing.Visibility == Visibility.Visible && _pairingGeneration > 0 &&
                    update.Height != _pairingGeneration)
                    break;
                string message = update.ErrorCode is 401 or 470
                    ? "设备验证未通过，请在设备上重新选择 ClearCast 后重试。"
                    : "设备连接未完成，请在设备上重试。可复制详情查看连接记录。";
                ShowReceiveError(message, update.ErrorCode);
                break;
        }
    }

    private void SetReceiverStatus(string status)
    {
        _receiverStatus = status;
        StatusText.Text = LandingTitle.Text = Texts.Translate(status);
    }

    private void SetPairingPrompt(string message)
    {
        _pairingPrompt = message;
        PairingPrompt.Text = Texts.Translate(message);
    }

    private void WaitForMirror()
    {
        SetReceiverStatus("等待屏幕镜像连接");
        ResetStreamStats();
        SetVideoAvailable(false);
    }

    private async void PollReceiver(DispatcherQueueTimer sender, object args)
    {
        if (_stopTask is not null || _receiver is not { } receiver)
            return;
#if CLEARCAST_UI_TESTS
        bool startupPoll = !_receiverReady;
        long hooksStarted = Stopwatch.GetTimestamp();
#endif
        if (_hasVideo)
            _videoHost?.RefreshInputHooks();
#if CLEARCAST_UI_TESTS
        if (startupPoll)
        {
            _uiStartupPollCount++;
            _uiStartupPollHooksMaxMilliseconds = Math.Max(_uiStartupPollHooksMaxMilliseconds,
                Stopwatch.GetElapsedTime(hooksStarted).TotalMilliseconds);
        }
#endif

        for (int processed = 0; processed < 160; processed++)
        {
#if CLEARCAST_UI_TESTS
            long readStarted = Stopwatch.GetTimestamp();
#endif
            bool hasEvent = receiver.TryReadEvent(out var update);
#if CLEARCAST_UI_TESTS
            if (startupPoll)
                _uiStartupPollReadEventMaxMilliseconds = Math.Max(_uiStartupPollReadEventMaxMilliseconds,
                    Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds);
#endif
            if (!hasEvent)
                break;
            switch (update.Kind)
            {
                case ReceiverEventKind.Ready:
                    _receiverReady = true;
                    UpdateReceiverButtons();
                    if (!_hasVideo)
                        WaitForMirror();
                    break;
                case ReceiverEventKind.Connecting:
                    ReceiverInfoBar.IsOpen = false;
                    HidePairingCode();
                    if (!_hasVideo)
                        SetReceiverStatus("正在打开屏幕镜像画面…");
                    break;
                case ReceiverEventKind.Video:
                    HidePairingCode();
                    if (update.Width > 0 && update.Height > 0)
                    {
                        _streamWidth = update.Width;
                        _streamHeight = update.Height;
                        SetPlayerFrameSize(update.Width, update.Height);
                        if (!_hasVideo)
                            SetStreamStatus();
                    }
                    SetReceiverStatus("已连接");
                    if (!_hasVideo && ReferenceEquals(_selectedPage, SettingsPage))
                        RefreshSavedPairings();
                    SetVideoAvailable(true);
                    break;
                case ReceiverEventKind.MirrorEnded:
                    ReceiverInfoBar.IsOpen = false;
                    HidePairingCode();
                    WaitForMirror();
                    break;
                case ReceiverEventKind.Failed:
                    if (await StopReceiverAsync())
                        ShowReceiveError(ReceiverNetwork.GetFailureMessage(update.ErrorCode, _preferences.Decoder), update.ErrorCode);
                    return;
                case ReceiverEventKind.PairingRequired:
                case ReceiverEventKind.PairingRejected:
                case ReceiverEventKind.PairingCompleted:
                case ReceiverEventKind.PairingAuthenticationRequired:
                case ReceiverEventKind.ConnectionRejected:
                    HandlePairingEvent(update);
                    break;
                case ReceiverEventKind.DecoderChanged:
                case ReceiverEventKind.DecoderChangeFailed:
                    if (_pendingDecoder is DecoderMode requested && update.Width == (int)requested)
                        FinishDecoderChange(requested, update.Kind == ReceiverEventKind.DecoderChanged, update.ErrorCode);
                    break;
            }
        }
        if (_hasVideo)
            UpdateStreamStats(receiver);
    }

    private void UpdateStreamStats(AirPlayReceiver receiver)
    {
        long now = Stopwatch.GetTimestamp();
        if (_sampledAt != 0 && Stopwatch.GetElapsedTime(_sampledAt, now).TotalSeconds < 1)
            return;
        if (!receiver.TryReadStats(out var stats))
            return;

        if (stats.Width > 0 && stats.Height > 0)
        {
            _streamWidth = stats.Width;
            _streamHeight = stats.Height;
            SetPlayerFrameSize(stats.Width, stats.Height);
        }

        int? fps = _sampledAt != 0 && stats.Rendered >= _previousRendered
            ? (int)Math.Round((stats.Rendered - _previousRendered) / Stopwatch.GetElapsedTime(_sampledAt, now).TotalSeconds)
            : null;
        if (stats.Dropped < _previousDropped)
            _earlierDropped += _previousDropped;
        _sampledAt = now;
        _previousRendered = stats.Rendered;
        _previousDropped = stats.Dropped;
        SetStreamStatus(fps, _earlierDropped + stats.Dropped);
    }

    private void SetStreamStatus(int? fps = null, ulong? dropped = null)
    {
        _streamFps = fps;
        _streamDropped = dropped;
        string dimensions = _streamWidth > 0 && _streamHeight > 0 ? $"{_streamWidth} × {_streamHeight}" : "";
        StreamText.Text = dimensions.Length > 0 && fps is int rate
            ? Texts.Get($"{dimensions} · {rate} 帧", $"{dimensions} · {rate} fps")
            : dimensions;
        string? detail = dropped is ulong count
            ? Texts.Get($"播放时跳过了 {count} 帧。", $"{count} {(count == 1 ? "frame" : "frames")} skipped during playback.")
            : null;
        if (!Equals(ToolTipService.GetToolTip(StreamText), detail))
            ToolTipService.SetToolTip(StreamText, detail);
    }

    private void ResetStreamStats()
    {
        _streamWidth = _streamHeight = 0;
        _sampledAt = 0;
        _previousRendered = _previousDropped = _earlierDropped = 0;
        SetStreamStatus();
    }

    private static int ErrorCode(Exception error) => error is Win32Exception native ? native.NativeErrorCode : error.HResult;

    private void ShowReceiveError(string message, int errorCode)
    {
        _receiveErrorCode = errorCode;
        _receiveErrorMessage = message;
        ReceiverInfoBar.Message = Texts.Translate(message);
        SetCopyErrorLabel("复制详情");
        ReceiverInfoBar.IsOpen = true;
        if (_receiver is null)
        {
            SetReceiverStatus("暂时无法接收屏幕镜像");
            RetryReceiveButton.Visibility = Visibility.Visible;
            if (_resettingPairings)
                ShowPairingResetFeedback(message);
        }
    }

    private void CopyErrorClicked(object sender, RoutedEventArgs e)
    {
        string errorCodeLabel = Texts.Get("错误代码", "Error code");
        string diagnostics = AirPlayReceiver.ReadDiagnostics(_root);
        bool copied = CopyToClipboard($"ClearCast\r\n{ReceiverInfoBar.Message}\r\n{errorCodeLabel} 0x{_receiveErrorCode:X8}\r\n{diagnostics}");
        SetCopyErrorLabel(copied ? "已复制" : "重试复制");
    }

    private void SetCopyErrorLabel(string message)
    {
        _copyErrorLabel = message;
        CopyErrorButton.Content = Texts.Translate(message);
    }

    private static bool CopyToClipboard(string text)
    {
        try
        {
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetText(text);
            return Clipboard.SetContentWithOptions(data, new ClipboardContentOptions
            {
                IsAllowedInHistory = false,
                IsRoamable = false
            });
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            // A busy clipboard should leave a retry action, rather than escape the UI event handler.
            return false;
        }
    }

    private static string FindRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("CLEARCAST_HOME");
        if (Directory.Exists(configured))
            return Path.GetFullPath(configured);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ClearCast.csproj")))
                return dir.FullName;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearCast");
    }
}
