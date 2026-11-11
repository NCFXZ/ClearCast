namespace ClearCast;

public sealed partial class MainWindow
{
    private string _receiverStatus = "正在启动接收器…";
    private string _pairingPrompt = "在设备上输入这四位数字。";
    private string _decoderFeedback = "";
    private string _pairingResetFeedback = "";
    private string _receiveErrorMessage = "";
    private string _copyErrorLabel = "复制详情";

    private void RefreshDynamicText()
    {
        SetReceiverStatus(_receiverStatus);
        PairingPrompt.Text = Texts.Translate(_pairingPrompt);
        DecoderFeedback.Text = Texts.Translate(_decoderFeedback);
        PairingResetFeedback.Text = Texts.Translate(_pairingResetFeedback);
        ReceiverInfoBar.Message = Texts.Translate(_receiveErrorMessage);
        CopyErrorButton.Content = Texts.Translate(_copyErrorLabel);
        RefreshReceiverErrorCloseText();
        RefreshFullScreenLabel();
        RefreshMusicText();
        UpdateMediaDiagnostics();
    }
}
