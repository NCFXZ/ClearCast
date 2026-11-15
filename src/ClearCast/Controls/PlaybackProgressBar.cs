using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Shapes;

namespace ClearCast;

public sealed partial class PlaybackProgressBar : ProgressBar
{
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // WinUI's track radius uses a framework-scoped StaticResource, unlike
        // its indicator. Match both while retaining native indeterminate motion.
        if (GetTemplateChild("ProgressBarTrack") is Rectangle track)
        {
            track.RadiusX = CornerRadius.TopLeft;
            track.RadiusY = CornerRadius.BottomRight;
        }
        if (GetTemplateChild("ProgressBarRoot") is Border root)
        {
            // Keep the rail visible underneath the indeterminate indicators and
            // clip moving segments at rounded ends rather than square edges.
            root.VerticalAlignment = VerticalAlignment.Center;
            root.SetBinding(HeightProperty, new Binding { Source = this, Path = new PropertyPath(nameof(MinHeight)) });
            root.SetBinding(Border.BackgroundProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Background)) });
        }
        if (GetTemplateChild("LayoutRoot") is FrameworkElement layout)
            foreach (var group in VisualStateManager.GetVisualStateGroups(layout))
                for (int index = group.Transitions.Count - 1; index >= 0; --index)
                    // The player already animates layout. A simultaneous native
                    // reposition can move the updating fill outside its clip.
                    if (group.Transitions[index] is { From: "Updating", To: "Determinate" })
                        group.Transitions.RemoveAt(index);
    }
}
