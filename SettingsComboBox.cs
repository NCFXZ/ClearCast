using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace ClearCast;

public sealed partial class SettingsComboBox : ComboBox
{
    private FrameworkElement? _popupBorder;

    public SettingsComboBox()
    {
        SizeChanged += (_, args) => MatchPopupWidth(args.NewSize.Width);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // A separate popup window can replace a stationary pointer with a resize cursor.
        if (GetTemplateChild("Popup") is Popup popup)
            popup.ShouldConstrainToRootBounds = true;

        _popupBorder = GetTemplateChild("PopupBorder") as FrameworkElement;
        MatchPopupWidth(ActualWidth);
    }

    private void MatchPopupWidth(double width)
    {
        if (_popupBorder is not null && width > 0)
            _popupBorder.Width = width;
    }
}
