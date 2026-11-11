using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace ClearCast;

public sealed partial class MainWindow
{
    internal UiText Texts { get; } = new();
    private Button? _receiverErrorCloseButton;
    private readonly HashSet<FrameworkElement> _pendingScrollBarLocalization = [];

    private void ApplyAppLanguage(bool english)
    {
        string language = english ? "en-US" : "zh-CN";
        // This Windows App SDK API supports unpackaged apps and changes only
        // this process/app. Set it before this window is initialized.
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        Texts.SetEnglish(english);
        if (WindowLayout is not null)
            WindowLayout.Language = language;
        RefreshReceiverErrorCloseText();
    }

    private void ReceiverErrorCloseLoaded(object sender, RoutedEventArgs args)
    {
        // InfoBar.OnApplyTemplate supplies a framework-localized tooltip even
        // for our custom template. Loaded runs after that initialization.
        _receiverErrorCloseButton = (Button)sender;
        RefreshReceiverErrorCloseText();
    }

    private void RefreshReceiverErrorCloseText()
    {
        if (_receiverErrorCloseButton is not { } close) return;
        string label = Texts.DismissReceiverError;
        AutomationProperties.SetName(close, label);
        if (ToolTipService.GetToolTip(close) is ToolTip tooltip)
            tooltip.Content = label;
        else
            ToolTipService.SetToolTip(close, new ToolTip { Content = label });
    }

    private void FrameworkScrollViewerLoaded(object sender, RoutedEventArgs args) =>
        RefreshFrameworkScrollBarNames((FrameworkElement)sender);

    private void FrameworkScrollViewerThemeChanged(FrameworkElement sender, object args)
    {
        // Theme changes may replace template parts after this event. Coalesce a
        // single refresh after that work; never scan on layout or playback ticks.
        if (!sender.IsLoaded || !_pendingScrollBarLocalization.Add(sender)) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _pendingScrollBarLocalization.Remove(sender);
            if (!_closed && sender.IsLoaded) RefreshFrameworkScrollBarNames(sender);
        }))
            _pendingScrollBarLocalization.Remove(sender);
    }

    private void RefreshFrameworkScrollBarNames(DependencyObject root)
    {
        var pending = new Stack<(DependencyObject Element, bool InScrollBar)>();
        pending.Push((root, false));
        while (pending.TryPop(out var item))
        {
            bool inScrollBar = item.InScrollBar || item.Element is ScrollBar;
            if (item.Element is ScrollBar scrollBar) scrollBar.ApplyTemplate();
            if (inScrollBar && item.Element is FrameworkElement part)
            {
                // ScrollBar.OnApplyTemplate writes these names once. Bind only
                // its known parts so existing labels also follow live language
                // changes, without changing native styles or scrolling behavior.
                string? property = part.Name switch
                {
                    "VerticalSmallDecrease" => nameof(UiText.ScrollUp),
                    "VerticalSmallIncrease" => nameof(UiText.ScrollDown),
                    "VerticalLargeDecrease" => nameof(UiText.PageUp),
                    "VerticalLargeIncrease" => nameof(UiText.PageDown),
                    "VerticalThumb" => nameof(UiText.VerticalScrollThumb),
                    "HorizontalSmallDecrease" => nameof(UiText.ScrollLeft),
                    "HorizontalSmallIncrease" => nameof(UiText.ScrollRight),
                    "HorizontalLargeDecrease" => nameof(UiText.PageLeft),
                    "HorizontalLargeIncrease" => nameof(UiText.PageRight),
                    "HorizontalThumb" => nameof(UiText.HorizontalScrollThumb),
                    _ => null
                };
                if (property is not null)
                    part.SetBinding(AutomationProperties.NameProperty,
                        new Binding { Source = Texts, Path = new PropertyPath(property), Mode = BindingMode.OneWay });
            }
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(item.Element); ++index)
                pending.Push((VisualTreeHelper.GetChild(item.Element, index), inScrollBar));
        }
    }
}
