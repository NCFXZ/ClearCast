using Microsoft.UI.Windowing;

namespace ClearCast;

public sealed partial class MainWindow
{
    private void UpdateLandingAnimationState()
    {
        LandingAnimation.IsPlaying = !_closed && !_closing && IsMirrorPageSelected && !_hasVideo &&
            AppWindow.IsVisible && WindowLayout.XamlRoot?.IsHostVisible == true &&
            AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
    }
}
