using Microsoft.UI.Dispatching;
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
    private DispatcherQueueTimer? _pageEntranceTimer;

    private bool IsMirrorPageSelected => ReferenceEquals(_selectedPage, MirrorPage);
    private bool IsMirrorPageEntering => _pageEntranceTimer?.IsRunning == true;

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

        StopPageEntrance();
        _selectedPage = page;
        bool animate = _settingsReady && _motionSettings.AnimationsEnabled;
        if (IsMirrorPageSelected && animate)
        {
            _pageEntranceTimer ??= CreatePageEntranceTimer();
            _pageEntranceTimer.Start();
        }
        else if (!IsMirrorPageSelected)
        {
            _videoHost?.Hide();
        }

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
        if (IsMirrorPageSelected && !animate)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, UpdateVideoHost);
    }

    private DispatcherQueueTimer CreatePageEntranceTimer()
    {
        // WinUI's page refresh is 150 ms out followed by a 300 ms entrance. The video HWND
        // lives outside XAML composition, so only show it when the incoming slot has settled.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(450);
        timer.IsRepeating = false;
        timer.Tick += (sender, _) =>
        {
            sender.Stop();
            UpdateVideoHost();
        };
        return timer;
    }

    private void StopPageEntrance() => _pageEntranceTimer?.Stop();
}
