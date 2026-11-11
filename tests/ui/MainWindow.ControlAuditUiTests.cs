#if CLEARCAST_UI_TESTS
using Microsoft.UI.Xaml.Shapes;
using WinRT.Interop;

namespace ClearCast;

public sealed partial class MainWindow
{
    private async Task UiTestControlAuditAsync()
    {
        var seek = new PlaybackSeekBar { Style = MusicSeekSlider.Style, Width = 240, Height = 32, Maximum = 100, Value = 50 };
        WindowLayout.Children.Add(seek);
        try
        {
            await UiWaitForStableLayout(seek);
            Rectangle fill = UiDescendants<Rectangle>(seek).Single(element => element.Name == "ProgressFill");
            double half = fill.Width;
            UiCheck(half > 0, "seek control fixture has a measured half-filled track");
            seek.Maximum = 200;
            UiCheck(seek.Value == 50 && Math.Abs(fill.Width - half / 2) <= 0.1,
                "a duration change repositions a paused seek thumb without a value change");
            seek.Minimum = -100;
            UiCheck(seek.Value == 50 && Math.Abs(fill.Width - half) <= 0.1,
                "a seek range minimum change refreshes the existing track position");
        }
        finally { WindowLayout.Children.Remove(seek); }

        using var host = new NativeVideoHost(WindowNative.GetWindowHandle(this), DispatcherQueue);
        int actions = 0;
        for (int index = 0; index < 5000; index++) host.UiQueueInput(() => actions++);
        UiCheck(host.UiQueuedInputCount == NativeVideoHost.MaximumQueuedInput,
            "native input flooding retains a bounded number of actions behind one UI drain");
        await UiWaitUntil(() => host.UiQueuedInputCount == 0, "bounded native input drain completes");
        UiCheck(actions == NativeVideoHost.MaximumQueuedInput,
            "native input overload discards excess actions instead of growing the dispatcher backlog");
        host.UiQueueInput(() => actions++);
        Action delayedInput = host.UiCaptureInputCallback(() => actions++);
        host.Hide();
        delayedInput();
        UiCheck(host.UiQueuedInputCount == 0, "hiding a native video host revokes its queued input");
        await Task.Delay(40);
        UiCheck(actions == NativeVideoHost.MaximumQueuedInput,
            "revoked native input cannot control playback after the host has hidden");
    }
}
#endif
