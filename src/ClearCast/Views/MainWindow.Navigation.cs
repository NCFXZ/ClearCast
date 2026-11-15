using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace ClearCast;

public sealed partial class MainWindow
{
    private readonly UISettings _motionSettings = new();
    private readonly Dictionary<FrameworkElement, NavigationContentPage> _pageHosts = new();
    private FrameworkElement? _selectedPage;

    private bool IsMirrorPageSelected => ReferenceEquals(_selectedPage, MirrorPage);

    private void InitializeNavigation()
    {
        NavigationContent.Children.Remove(MirrorPage);
        NavigationContent.Children.Remove(SettingsPage);
        NavigationContent.Children.Remove(AboutPage);
        MainNavigation.SelectionChanged += NavigationChanged;
        MainNavigation.SelectedItem = MirrorNavigationItem;
        NavigateToPage(MirrorPage);
    }

    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        FrameworkElement? page = (args.SelectedItem as NavigationViewItem)?.Tag switch
        {
            "mirror" => MirrorPage,
            "settings" => SettingsPage,
            "about" => AboutPage,
            _ => null
        };
        if (page is not null)
            NavigateToPage(page);
    }

    private void NavigateToPage(FrameworkElement page)
    {
        if (ReferenceEquals(page, _selectedPage))
            return;

        _selectedPage = page;
        bool restoreVideo = IsMirrorPageSelected && _hasVideo;
        bool animate = _settingsReady && _motionSettings.AnimationsEnabled;
        if (!IsMirrorPageSelected)
            _videoHost?.Hide();

        if (ReferenceEquals(page, SettingsPage))
            RefreshSavedPairings();

        // A previous host can still own an off-screen tab even after its visual parent is gone.
        if (_pageHosts.TryGetValue(page, out var previousHost))
            previousHost.Content = null;

        NavigationFrame.Navigate(typeof(NavigationContentPage), null, animate
            ? new EntranceNavigationTransitionInfo()
            : new SuppressNavigationTransitionInfo());
        var host = (NavigationContentPage)NavigationFrame.Content;
        host.Content = page;
        _pageHosts[page] = host;
        UpdateLandingAnimationState();
        if (restoreVideo)
        {
            // Reattach and arrange the slot before showing its retained surface.
            // This also handles a resize while the AirPlay tab was off-screen.
            NavigationFrame.UpdateLayout();
            UpdateVideoHost();
        }
        else if (IsMirrorPageSelected && !animate)
            QueueVideoHostUpdate();
    }
}
