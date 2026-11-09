using Microsoft.UI.Windowing;

namespace ClearCast;

public sealed partial class MainWindow
{
    private void UpdateLandingAnimationState()
    {
        LandingAnimation.IsPlaying = !_closed && !_closing && IsMirrorPageSelected && !_hasVideo && !_hasMusic &&
            AppWindow.IsVisible && WindowLayout.XamlRoot?.IsHostVisible == true &&
            AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        UpdateMusicBackgroundState();
    }
}
