using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClearCast;

internal sealed record PairingDeviceRow(string Id, string Name, string RemoveText, string RemoveAutomationName);

public sealed partial class MainWindow
{
    private const string PairingReadError = "无法读取已保存的配对记录。";
    private bool _resettingPairings;
    private bool _pairingLoadFailed;

    private void RefreshSavedPairings()
    {
        IReadOnlyList<SavedPairing> pairings;
        bool loadFailed = false;
        try
        {
            pairings = AirPlayReceiver.GetSavedPairings(_root);
            if (_pairingResetFeedback == PairingReadError)
                ShowPairingResetFeedback("");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            pairings = [];
            loadFailed = true;
            ShowPairingResetFeedback(PairingReadError);
        }
        _pairingLoadFailed = loadFailed;

        var items = new List<PairingDeviceRow>(pairings.Count);
        foreach (var group in pairings.GroupBy(pairing => pairing.Name ?? Texts.UnnamedPairing))
        {
            bool duplicateName = group.Skip(1).Any();
            int index = 0;
            foreach (var pairing in group)
            {
                string label = duplicateName ? $"{group.Key} ({++index})" : group.Key;
                items.Add(new PairingDeviceRow(pairing.Id, label, Texts.RemovePairing,
                    Texts.Get($"删除 {label} 的配对记录", $"Remove pairing for {label}")));
            }
        }

        PairingListFooter.Text = loadFailed ? "" : items.Count == 0 ? Texts.NoSavedPairings : Texts.AllSavedPairings;
        PairingDeviceList.ItemsSource = items;
        UpdateReceiverButtons();
    }

    private void UpdateSavedPairingButtons(bool busy)
    {
        bool canRemove = !busy && !_hasVideo;
        PairingDeviceList.IsEnabled = canRemove;
        RepairPairingButton.IsEnabled = canRemove && (PairingDeviceList.Items.Count > 0 || _pairingLoadFailed);
    }

    private async void RepairPairingClicked(object sender, RoutedEventArgs e) => await RemoveSavedPairingsAsync(null, null);

    private async void RemoveDevicePairingClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PairingDeviceRow row })
            await RemoveSavedPairingsAsync(row.Id, row.Name);
    }

    private async Task RemoveSavedPairingsAsync(string? id, string? name)
    {
        if (_resettingPairings || _startingReceiver || _hasVideo || _closing || _closed || _stopTask is not null)
            return;

        _resettingPairings = true;
        UpdateReceiverButtons();
        bool restartReceiver = false;
        try
        {
            var dialog = new ContentDialog
            {
                Title = id is null ? Texts.ClearPairings : Texts.RemoveDevicePairing,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = id is null
                        ? Texts.Get("删除后，所有设备下次屏幕镜像时都需要重新验证。",
                            "All saved devices will need a pairing code the next time they connect.")
                        : Texts.Get($"删除「{name}」的配对记录后，该设备下次屏幕镜像时需要重新验证。其他已保存设备也可能需要重新验证。",
                            $"“{name}” will need a pairing code the next time it connects. Other saved devices may also need to pair again.")
                },
                PrimaryButtonText = Texts.RemovePairing,
                CloseButtonText = Texts.Get("取消", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = SettingsPage.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _hasVideo || _closing || _closed)
                return;
            if (_receiver is not null && !await StopReceiverAsync())
                return;
            if (_closing || _closed)
                return;

            restartReceiver = true;
            if (id is null)
            {
                AirPlayReceiver.ClearPairings(_root);
                ShowPairingResetFeedback("已删除全部配对记录。设备下次屏幕镜像时需要重新验证。");
            }
            else
            {
                AirPlayReceiver.RemovePairing(_root, id);
                ShowPairingResetFeedback("已删除该设备的配对记录。该设备下次屏幕镜像时需要重新验证，其他已保存设备也可能需要重新验证。");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowPairingResetFeedback("无法删除配对记录，请重试。");
        }
        finally
        {
            if (restartReceiver && !_closing && !_closed)
            {
                RefreshSavedPairings();
                StartAirPlay();
            }
            _resettingPairings = false;
            if (!_closed)
                UpdateReceiverButtons();
        }
    }

    private void ShowPairingResetFeedback(string message)
    {
        _pairingResetFeedback = message;
        PairingResetFeedback.Text = Texts.Translate(message);
        PairingResetFeedback.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
