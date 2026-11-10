#if CLEARCAST_UI_TESTS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClearCast;

public sealed partial class MainWindow
{
    private async Task UiTestConnectionStatusAsync()
    {
        string language = Texts.IsEnglish ? "en" : "zh";
        UiCheck(_receiver is null && !_hasVideo && !_hasMusic && !_playerFullScreen &&
            !_startingReceiver && _stopTask is null && WaitingPairing.Visibility != Visibility.Visible &&
            !_pairingTimer.IsRunning, "connection status uses a stopped idle receiver " + language);

        var previousMedia = _mediaState;
        long previousSnapshotAt = _mediaSnapshotAt;
        var previousPreferences = _preferences;
        bool previousEnglish = Texts.IsEnglish;
        bool previousReady = _receiverReady;
        bool previousStarting = _startingReceiver;
        bool previousPolling = _receiverTimer.IsRunning;
        bool previousSettingsReady = _settingsReady;
        object? previousLanguageItem = LanguageButton.SelectedItem;
        var previousNavigationItem = MainNavigation.SelectedItem as NavigationViewItem;
        var previousPage = _selectedPage;
        string previousStatus = _receiverStatus;
        var previousRetryVisibility = RetryReceiveButton.Visibility;
        var previousSaveHintVisibility = SettingsSaveHint.Visibility;
        bool previousErrorOpen = ReceiverInfoBar.IsOpen;
        string previousError = _receiveErrorMessage;
        int previousErrorCode = _receiveErrorCode;
        string previousCopyLabel = _copyErrorLabel;
        string previousPairingPrompt = _pairingPrompt;
        string previousPin = PinText.Text;
        var previousPinVisibility = WaitingPairing.Visibility;
        int previousPairingGeneration = _pairingGeneration;
        int previousLatestGeneration = _latestPairingGeneration;
        int previousProofRejections = _pairingProofRejections;
        bool previousTerminalFeedback = _pairingTerminalFeedback;

        void CheckStatus(string canonical, string check)
        {
            string translated = Texts.Translate(canonical);
            UiCheck(_receiverStatus == canonical && LandingTitle.Text == translated,
                check + " " + language);
        }

        try
        {
            _receiverTimer.Stop();
            _receiverReady = true;
            ReceiverInfoBar.IsOpen = false;
            HidePairingCode();
            _preferences = _preferences with { AutoFullScreen = false };
            await UiNavigateAsync(MirrorNavigationItem, MirrorPage);

            var disconnected = ReceiverMediaState.Empty with { Revision = 71 };
            var connected = disconnected with { Connected = true };
            SetReceiverStatus("等待 AirPlay 连接");
            ApplyMediaState(disconnected);
            CheckStatus("等待 AirPlay 连接", "idle without an authenticated connection waits");

            HandlePairingEvent(new(ReceiverEventKind.PairingAuthenticationRequired, 0, 0, 0, 470));
            HandlePairingEvent(new(ReceiverEventKind.PairingCompleted, 0, 0, 0, 0));
            UiCheck(!_mediaState.Connected && !ReceiverInfoBar.IsOpen,
                "an unauthenticated probe and unassociated pairing completion do not establish connection " + language);
            CheckStatus("等待 AirPlay 连接", "pairing events alone leave the receiver waiting");

            ApplyMediaState(connected);
            CheckStatus("AirPlay 已连接", "connection changes refresh idle text without a revision change");
            ApplyMediaState(connected with { DeviceName = "Test iPhone" });
            UiCheck(LandingTitle.Text == Texts.Get("Test iPhone已连接", "Test iPhone connected"),
                "an authenticated sender name appears in the connected label " + language);
            ApplyMediaState(connected);
            WaitForMirror();
            WaitForMirror();
            CheckStatus("AirPlay 已连接", "waiting for media preserves an established AirPlay connection");
            ApplyMediaState(disconnected);
            CheckStatus("等待 AirPlay 连接", "a same-revision disconnect clears the connected label");

            SetReceiverStatus("正在打开屏幕镜像画面…");
            ApplyMediaState(disconnected);
            CheckStatus("正在打开屏幕镜像画面…", "an unfinished opening status is preserved without a connection");
            ApplyMediaState(connected);
            CheckStatus("AirPlay 已连接", "authenticated idle state replaces an obsolete opening status");

            _receiverReady = false;
            SetReceiverStatus("等待 AirPlay 连接");
            ApplyMediaState(connected);
            CheckStatus("等待 AirPlay 连接", "a receiver that is not ready suppresses idle refresh");
            SetReceiverStatus("正在启动接收器…");
            ApplyMediaState(connected);
            CheckStatus("正在启动接收器…", "connection polling does not overwrite receiver startup");
            _receiverReady = true;
            _startingReceiver = true;
            SetReceiverStatus("等待 AirPlay 连接");
            ApplyMediaState(connected);
            CheckStatus("等待 AirPlay 连接", "an in-progress receiver start suppresses idle refresh");
            _startingReceiver = false;
            RefreshIdleConnectionStatus();
            CheckStatus("AirPlay 已连接", "idle refresh resumes after receiver startup");

            ShowReceiveError("设备验证未通过，请在设备上重新选择 ClearCast 后重试。", 470);
            string errorMessage = ReceiverInfoBar.Message;
            ApplyMediaState(connected);
            UiCheck(ReceiverInfoBar.IsOpen && ReceiverInfoBar.Message == errorMessage && _receiveErrorCode == 470,
                "an authenticated snapshot does not dismiss an existing receiver error " + language);
            CheckStatus("暂时无法接收屏幕镜像", "idle refresh preserves the failed receiver status");
            SetReceiverStatus("等待 AirPlay 连接");
            ApplyMediaState(connected);
            CheckStatus("等待 AirPlay 连接", "a visible connection error takes priority over ordinary idle refresh");
            ReceiverInfoBar.IsOpen = false;
            SetReceiverStatus("暂时无法接收屏幕镜像");
            ApplyMediaState(connected);
            CheckStatus("暂时无法接收屏幕镜像", "dismissing an error does not silently replace its failure status");

            ApplyMediaState(disconnected);
            SetReceiverStatus("等待 AirPlay 连接");
            int generation = previousLatestGeneration is >= 0 and < int.MaxValue ? previousLatestGeneration + 1 : 1;
            HandlePairingEvent(new(ReceiverEventKind.PairingRequired, 2468, generation, 0, 0));
            HandlePairingEvent(new(ReceiverEventKind.PairingCompleted, 0, generation, 0, 0));
            UiCheck(!_mediaState.Connected && WaitingPairing.Visibility == Visibility.Collapsed,
                "matching pairing completion hides its PIN without inventing a live connection " + language);
            CheckStatus("等待 AirPlay 连接", "completed pairing still waits for an authenticated snapshot");
            ShowPairingCode("2468", generation);
            string pinPrompt = PairingPrompt.Text;
            ApplyMediaState(connected);
            UiCheck(WaitingPairing.Visibility == Visibility.Visible && PinText.Text == "2468" &&
                PairingPrompt.Text == pinPrompt && _pairingTimer.IsRunning,
                "idle connection refresh preserves the visible pairing challenge " + language);
            CheckStatus("等待 AirPlay 连接", "a visible PIN takes priority over idle connection text");
            HidePairingCode();
            ApplyMediaState(connected);
            CheckStatus("AirPlay 已连接", "the next snapshot refreshes idle text after PIN dismissal");

            LanguageButton.SelectedItem = previousEnglish ? ChineseLanguageItem : EnglishLanguageItem;
            await Task.Delay(40);
            UiCheck(Texts.IsEnglish != previousEnglish && _receiverStatus == "AirPlay 已连接" &&
                LandingTitle.Text == (Texts.IsEnglish ? "AirPlay connected" : "AirPlay 已连接"),
                "language selection immediately translates the canonical connected status " + language);
            LanguageButton.SelectedItem = previousLanguageItem;
            await Task.Delay(40);
            UiCheck(Texts.IsEnglish == previousEnglish &&
                LandingTitle.Text == (previousEnglish ? "AirPlay connected" : "AirPlay 已连接"),
                "restoring the language translates the connected status back " + language);

            await UiNavigateAsync(SettingsNavigationItem, SettingsPage);
            CheckStatus("AirPlay 已连接", "settings navigation preserves the connection status");
            await UiNavigateAsync(AboutNavigationItem, AboutPage);
            CheckStatus("AirPlay 已连接", "about navigation preserves the connection status");
            await UiNavigateAsync(MirrorNavigationItem, MirrorPage);
            CheckStatus("AirPlay 已连接", "returning to AirPlay preserves the connection status");

            var audio = new ReceiverMediaState(72, ReceiverMediaKind.Audio, true, "Connection test", "Synthetic artist", "",
                1_000, 30_000, 0, 0, MediaControls.None) { Connected = true, Session = 17 };
            ApplyMediaState(audio);
            UiCheck(_hasMusic && _mediaState.Connected && _receiverStatus == Texts.MusicPlaying,
                "connected music keeps its playback status " + language);
            ApplyMediaState(audio with { Playing = false });
            UiCheck(_hasMusic && _mediaState.Connected && _receiverStatus == Texts.MusicPaused,
                "pausing music preserves its live connection and paused status " + language);
            ApplyMediaState(connected);
            UiCheck(!_hasMusic && VideoPlaceholder.Visibility == Visibility.Visible,
                "ending music returns to the landing page " + language);
            CheckStatus("AirPlay 已连接", "audio ending does not disconnect an authenticated session");

            ApplyMediaState(connected with { Kind = ReceiverMediaKind.Video, Playing = true });
            SetReceiverStatus("AirPlay 已连接");
            SetVideoAvailable(true);
            WaitForMirror();
            ApplyMediaState(connected);
            UiCheck(!_hasVideo && !_hasMusic && !_playerFullScreen && VideoPlaceholder.Visibility == Visibility.Visible,
                "video ending restores the landing page without forcing full screen " + language);
            CheckStatus("AirPlay 已连接", "video ending preserves a still-live AirPlay connection");
            ApplyMediaState(disconnected);
            CheckStatus("等待 AirPlay 连接", "the final authenticated disconnect returns to waiting");
            ApplyMediaState(connected);
            SetRunning(false);
            UiCheck(!_mediaState.Connected && _mediaState.Kind == ReceiverMediaKind.None && !_receiverReady &&
                !_receiverTimer.IsRunning && WaitingPairing.Visibility == Visibility.Collapsed,
                "stopping clears connection media polling and pairing state " + language);
            CheckStatus("等待 AirPlay 连接", "stopping does not leave a stale connected label");
        }
        finally
        {
            _receiverTimer.Stop();
            HidePairingCode();
            SetVideoAvailable(false);
            _receiverReady = false;
            _startingReceiver = previousStarting;
            ApplyMediaState(previousMedia);
            _mediaSnapshotAt = previousSnapshotAt;
            _settingsReady = false;
            try
            {
                _preferences = previousPreferences;
                LanguageButton.SelectedItem = previousLanguageItem;
            }
            finally { _settingsReady = previousSettingsReady; }
            Texts.SetEnglish(previousEnglish);
            WindowLayout.Language = previousEnglish ? "en-US" : "zh-CN";
            RefreshComboBoxLabels();
            _receiveErrorMessage = previousError;
            _receiveErrorCode = previousErrorCode;
            _copyErrorLabel = previousCopyLabel;
            _pairingPrompt = previousPairingPrompt;
            _pairingGeneration = previousPairingGeneration;
            _latestPairingGeneration = previousLatestGeneration;
            _pairingProofRejections = previousProofRejections;
            _pairingTerminalFeedback = previousTerminalFeedback;
            PinText.Text = previousPin;
            WaitingPairing.Visibility = previousPinVisibility;
            RefreshDynamicText();
            SetReceiverStatus(previousStatus);
            ReceiverInfoBar.IsOpen = previousErrorOpen;
            RetryReceiveButton.Visibility = previousRetryVisibility;
            SettingsSaveHint.Visibility = previousSaveHintVisibility;
            _receiverReady = previousReady;
            if (previousPolling) _receiverTimer.Start();
            UpdateReceiverButtons();
            _preferences.Save(_root);
            if (previousNavigationItem is not null && previousPage is not null)
                await UiNavigateAsync(previousNavigationItem, previousPage);
        }
    }
}
#endif
