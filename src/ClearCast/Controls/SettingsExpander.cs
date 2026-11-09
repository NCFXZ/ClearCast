using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ClearCast;

public sealed partial class SettingsExpander : Expander
{
    public static readonly DependencyProperty HeaderBackgroundProperty = DependencyProperty.Register(
        nameof(HeaderBackground), typeof(Brush), typeof(SettingsExpander), new PropertyMetadata(null, OnHeaderBrushChanged));

    public static readonly DependencyProperty HeaderPointerOverBackgroundProperty = DependencyProperty.Register(
        nameof(HeaderPointerOverBackground), typeof(Brush), typeof(SettingsExpander), new PropertyMetadata(null, OnHeaderBrushChanged));

    private ToggleButton? _header;
    private long _pointerOverToken;

    public Brush? HeaderBackground
    {
        get => (Brush?)GetValue(HeaderBackgroundProperty);
        set => SetValue(HeaderBackgroundProperty, value);
    }

    public Brush? HeaderPointerOverBackground
    {
        get => (Brush?)GetValue(HeaderPointerOverBackgroundProperty);
        set => SetValue(HeaderPointerOverBackgroundProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        _header?.UnregisterPropertyChangedCallback(ButtonBase.IsPointerOverProperty, _pointerOverToken);
        base.OnApplyTemplate();
        _header = GetTemplateChild("ExpanderHeader") as ToggleButton;
        if (_header is not null)
        {
            _pointerOverToken = _header.RegisterPropertyChangedCallback(ButtonBase.IsPointerOverProperty,
                (_, _) => UpdateHeaderBackground());
            UpdateHeaderBackground();
        }
    }

    private static void OnHeaderBrushChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((SettingsExpander)sender).UpdateHeaderBackground();

    private void UpdateHeaderBackground()
    {
        if (_header is not null)
            _header.Background = _header.IsPointerOver ? HeaderPointerOverBackground : HeaderBackground;
    }
}
