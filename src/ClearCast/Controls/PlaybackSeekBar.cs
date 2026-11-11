using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace ClearCast;

// Keep playback input independent of the native Slider's failing first-layout path.
public sealed partial class PlaybackSeekBar : RangeBase
{
    private Grid? _track;
    private Rectangle? _fill;
    private FrameworkElement? _thumb;
    private TranslateTransform? _thumbOffset;
    private bool _dragging;
    internal event EventHandler? SeekStarted;
    internal event EventHandler? SeekCompleted;
    internal event EventHandler? SeekCanceled;

    public PlaybackSeekBar()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        ValueChanged += (_, args) =>
        {
            UpdatePosition();
            if (FrameworkElementAutomationPeer.FromElement(this) is SeekAutomationPeer peer)
                peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, args.OldValue, args.NewValue);
        };
        RegisterPropertyChangedCallback(MinimumProperty, (_, _) => UpdatePosition());
        RegisterPropertyChangedCallback(MaximumProperty, (_, _) => UpdatePosition());
        SizeChanged += (_, _) => UpdatePosition();
        PointerPressed += SeekPressed;
        PointerMoved += SeekMoved;
        PointerReleased += SeekReleased;
        PointerCanceled += (_, _) => { CancelDrag(); SeekCanceled?.Invoke(this, EventArgs.Empty); };
        PointerCaptureLost += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false;
            SeekCanceled?.Invoke(this, EventArgs.Empty);
        };
        LostFocus += (_, _) => { CancelDrag(); SeekCanceled?.Invoke(this, EventArgs.Empty); };
        IsEnabledChanged += (_, _) => UpdateVisualState();
        KeyDown += SeekKeyDown;
        KeyUp += (_, args) =>
        {
            if (IsSeekKey(args.Key)) SeekCompleted?.Invoke(this, EventArgs.Empty);
        };
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _track = GetTemplateChild("Track") as Grid;
        _fill = GetTemplateChild("ProgressFill") as Rectangle;
        _thumb = GetTemplateChild("Thumb") as FrameworkElement;
        _thumbOffset = _thumb?.RenderTransform as TranslateTransform;
        UpdatePosition();
        UpdateVisualState();
    }

    private void UpdateVisualState() => VisualStateManager.GoToState(this, IsEnabled ? "Normal" : "Disabled", false);

    private double TrackLength => Math.Max(0, (_track?.ActualWidth ?? 0) - (_thumb?.ActualWidth ?? 20));

    private void UpdatePosition()
    {
        double ratio = Maximum > Minimum ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1) : 0;
        double offset = ratio * TrackLength;
        if (_fill is not null) _fill.Width = offset;
        if (_thumbOffset is not null) _thumbOffset.X = offset;
    }

    private void UpdateFromPointer(PointerRoutedEventArgs args)
    {
        if (_track is null || TrackLength <= 0) return;
        double x = args.GetCurrentPoint(_track).Position.X - (_thumb?.ActualWidth ?? 20) / 2;
        Value = Minimum + Math.Clamp(x / TrackLength, 0, 1) * (Maximum - Minimum);
    }

    private void SeekPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!IsEnabled || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(FocusState.Pointer);
        _dragging = CapturePointer(args.Pointer);
        if (_dragging)
        {
            SeekStarted?.Invoke(this, EventArgs.Empty);
            UpdateFromPointer(args);
            args.Handled = true;
        }
    }

    private void SeekMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragging) { UpdateFromPointer(args); args.Handled = true; }
    }

    private void SeekReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_dragging) return;
        UpdateFromPointer(args);
        CancelDrag();
        args.Handled = true;
        SeekCompleted?.Invoke(this, EventArgs.Empty);
    }

    internal void CancelDrag()
    {
        _dragging = false;
        ReleasePointerCaptures();
    }

    private static bool IsSeekKey(VirtualKey key) => key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or
        VirtualKey.Down or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown;

    private void SeekKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            CancelDrag();
            SeekCanceled?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!IsEnabled || !IsSeekKey(args.Key)) return;
        SeekStarted?.Invoke(this, EventArgs.Empty);
        double next = args.Key switch
        {
            VirtualKey.Home => Minimum,
            VirtualKey.End => Maximum,
            VirtualKey.Left or VirtualKey.Down => Value - SmallChange,
            VirtualKey.Right or VirtualKey.Up => Value + SmallChange,
            VirtualKey.PageDown => Value - LargeChange,
            _ => Value + LargeChange
        };
        Value = Math.Clamp(next, Minimum, Maximum);
        args.Handled = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SeekAutomationPeer(this);

    private sealed partial class SeekAutomationPeer(PlaybackSeekBar owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(PlaybackSeekBar);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override object GetPatternCore(PatternInterface pattern) =>
            pattern == PatternInterface.RangeValue ? this : base.GetPatternCore(pattern);
        public bool IsReadOnly => !owner.IsEnabled;
        public double LargeChange => owner.LargeChange;
        public double SmallChange => owner.SmallChange;
        public double Maximum => owner.Maximum;
        public double Minimum => owner.Minimum;
        public double Value => owner.Value;
        public void SetValue(double value)
        {
            if (IsReadOnly) throw new ElementNotEnabledException();
            if (!double.IsFinite(value) || value < Minimum || value > Maximum) throw new ArgumentOutOfRangeException(nameof(value));
            owner.SeekStarted?.Invoke(owner, EventArgs.Empty);
            owner.Value = value;
            owner.SeekCompleted?.Invoke(owner, EventArgs.Empty);
        }
    }
}
