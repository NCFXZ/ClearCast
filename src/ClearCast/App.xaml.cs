using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using System.Text;

namespace ClearCast;

public partial class App : Application
{
    private MainWindow? window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => WriteUiError(args.Exception + Environment.NewLine + args.Message +
            Environment.NewLine + LayoutErrorDetails());
    }

    /// <summary>Keeps the latest bounded UI failure detail for diagnosis.</summary>
    internal static void WriteUiError(string message)
    {
        try
        {
            string state = System.IO.Path.Combine(MainWindow.FindRoot(), ".clearcast-state");
            System.IO.Directory.CreateDirectory(state);
            string detail = DateTime.UtcNow.ToString("O") + Environment.NewLine + message;
            System.IO.File.WriteAllText(System.IO.Path.Combine(state, "ui-error.log"), detail[..Math.Min(detail.Length, 8192)]);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            // Diagnostics must not replace the original UI exception.
        }
    }

    private static string LayoutErrorDetails()
    {
        var details = new StringBuilder("Layout exception element:" + Environment.NewLine);
        try
        {
            DependencyObject? element = LayoutInformation.GetLayoutExceptionElement(null);
            for (int depth = 0; element is not null && depth < 8; ++depth)
            {
                details.Append(element.GetType().Name);
                if (element is FrameworkElement frame)
                    details.Append($" {frame.Name} available={LayoutInformation.GetAvailableSize(frame)}" +
                        $" desired={frame.DesiredSize} actual={frame.ActualWidth},{frame.ActualHeight} height={frame.Height} slot={LayoutInformation.GetLayoutSlot(frame)}");
                if (element is RangeBase range)
                    details.Append($" range={range.Minimum},{range.Maximum} value={range.Value} small={range.SmallChange} large={range.LargeChange} children={VisualTreeHelper.GetChildrenCount(range)}");
                details.AppendLine();
                element = VisualTreeHelper.GetParent(element);
            }
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException)
        {
            details.Append("Layout diagnostics unavailable: ").Append(error.HResult);
        }
        return details.ToString();
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
