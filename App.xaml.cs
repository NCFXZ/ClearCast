using Microsoft.UI.Xaml;

namespace ClearCast;

public partial class App : Application
{
    private MainWindow? window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if CLEARCAST_UI_TESTS
        if (!MainWindow.UiTestsAreConfigured())
        {
            Environment.ExitCode = 2;
            Exit();
            return;
        }
#endif
        window = new MainWindow();
        window.Activate();
#if CLEARCAST_UI_TESTS
        window.StartUiTests();
#endif
    }
}
