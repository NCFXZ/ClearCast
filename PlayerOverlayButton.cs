using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ClearCast;

public sealed partial class PlayerOverlayButton : Button
{
    protected override AutomationPeer OnCreateAutomationPeer() => new OverlayButtonAutomationPeer(this);

    private sealed partial class OverlayButtonAutomationPeer(PlayerOverlayButton owner) : ButtonAutomationPeer(owner)
    {
        protected override void SetFocusCore()
        {
            // Explicit UI Automation focus should reveal the overlay even after mouse input.
            if (IsKeyboardFocusable())
                owner.Focus(FocusState.Keyboard);
        }
    }
}
