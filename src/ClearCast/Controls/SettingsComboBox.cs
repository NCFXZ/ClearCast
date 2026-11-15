using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace ClearCast;

public sealed partial class SettingsComboBox : ComboBox
{
    private FrameworkElement? _popupBorder;

    public SettingsComboBox()
    {
        SizeChanged += (_, args) => EnsurePopupMinWidth(args.NewSize.Width);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // A separate popup window can replace a stationary pointer with a resize cursor.
        if (GetTemplateChild("Popup") is Popup popup)
            popup.ShouldConstrainToRootBounds = true;

        _popupBorder = GetTemplateChild("PopupBorder") as FrameworkElement;
        EnsurePopupMinWidth(ActualWidth);
    }

    private void EnsurePopupMinWidth(double width)
    {
        if (_popupBorder is not null && width > 0)
            _popupBorder.MinWidth = width;
    }
}
