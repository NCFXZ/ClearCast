using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ClearCast;

/// <summary>
/// Line-synchronized lyrics. XAML text supplies layout and glyphs; each line is drawn by its own
/// Composition sprite so it can move in a staggered cascade, blur with distance from the current
/// line, and fade near the top and bottom edges of the view.
/// </summary>
internal sealed class LyricsView : Grid
{
    private const float InactiveOpacity = 0.3f;
    private const float PlainOpacity = 0.75f;
    private const float InactiveScale = 0.96f;
    private const float BlurPadding = 16;
    private const double AnchorRatio = 0.3;
    private const int MaximumStaggeredLines = 8;
    private const string BlurProperty = LyricsBlurEffect.EffectName + ".BlurAmount";
    private static readonly TimeSpan ScrollDuration = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan LineDelay = TimeSpan.FromMilliseconds(45);
    private static readonly TimeSpan EmphasisDuration = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ManualScrollHold = TimeSpan.FromSeconds(3);

    private sealed class Line(TextBlock block, SpriteVisual sprite, CompositionVisualSurface surface, CompositionBrush brush)
    {
        public TextBlock Block { get; } = block;
        public SpriteVisual Sprite { get; } = sprite;
        public CompositionVisualSurface Surface { get; } = surface;
        public CompositionBrush Brush { get; } = brush;
        public double Top { get; set; }
        public float Blur { get; set; } = -1;
        public bool Visible { get; set; } = true;
        public bool? Emphasized { get; set; }
        public bool EmphasisAnimating { get; set; }
        public Vector3? Offset { get; set; }
        public bool OffsetAnimating { get; set; }
    }

    private readonly Canvas _viewport;
    private readonly StackPanel _lines;
    private readonly TextBlock _source;
    private readonly Compositor _compositor;
    private readonly CompositionEasingFunction _ease;
    private readonly ScalarKeyFrameAnimation _scalarAnimation;
    private readonly Vector3KeyFrameAnimation _vectorAnimation;
    private readonly ContainerVisual _layer;
    private readonly CompositionVisualSurface _layerSurface;
    private readonly SpriteVisual _display;
    private readonly CompositionSurfaceBrush _displaySource;
    private readonly CompositionMaskBrush _fadeBrush;
    private readonly CompositionEffectFactory? _blurFactory;
    // Held for the view's lifetime so the native effect description outlives any use by Composition.
    private readonly Windows.Graphics.Effects.IGraphicsEffect? _blurEffect;
    private Lyrics? _lyrics;
    private Line?[] _entries = [];
    private int _active = -1;
    private double _manualTranslation;
    private bool _manualBrowsing;
    private bool _manipulating;
    private long _manualScrollAt;
    private bool _revealPending;
    private bool _effectsEnabled = true;
    private double _fontSize = 32;
    private bool _geometryDirty = true;
    private bool _hasPlacement;
    private double _lastTranslation;
    private int _lastActive = -1;
#if CLEARCAST_UI_TESTS
    private long _uiPlaceCalls, _uiVisitedLines, _uiCreatedAnimations, _uiGeometryWrites, _uiStartedAnimations;
    internal long UiBlurAnimationStarts { get; private set; }
    internal (long PlaceCalls, long VisitedLines, long CreatedAnimations, long GeometryWrites, long StartedAnimations) UiPerformance =>
        (_uiPlaceCalls, _uiVisitedLines, _uiCreatedAnimations, _uiGeometryWrites, _uiStartedAnimations);
    internal void UiResetPerformanceCounters()
    {
        (_uiPlaceCalls, _uiVisitedLines, _uiCreatedAnimations, _uiGeometryWrites, _uiStartedAnimations) = (0, 0, 0, 0, 0);
        UiBlurAnimationStarts = 0;
    }
    internal void UiScrollBy(double delta) => ScrollBy(delta);
    internal int UiVisibleLineCount => _entries.Count(entry => entry?.Sprite.IsVisible == true);
    internal double UiManualOffset => _manualBrowsing ? _manualTranslation - ScrollRange().Base : 0;
    internal bool UiManualBrowsing => _manualBrowsing;
    internal void UiBeginManualGesture() => _manipulating = true;
    internal void UiEndManualGesture() => EndManualGesture();
    internal (float Y, float TargetY, bool Animating) UiLineMotion(int index) => _entries[index] is { } entry
        ? (entry.Sprite.Offset.Y, entry.Offset?.Y ?? entry.Sprite.Offset.Y, entry.OffsetAnimating) : default;
    internal float UiLineOpacity(int index) => _entries[index]?.Sprite.Opacity ?? 0;
#endif

    public LyricsView()
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        // Strong deceleration similar to native lyric scrolling: fast start, long settle.
        _ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));
        // Composition snapshots an animation template on StartAnimation.
        _scalarAnimation = _compositor.CreateScalarKeyFrameAnimation();
        _vectorAnimation = _compositor.CreateVector3KeyFrameAnimation();
#if CLEARCAST_UI_TESTS
        _uiCreatedAnimations += 2;
#endif
        _lines = new StackPanel();
        // The XAML lines only provide layout and glyphs; their host is hidden and the sprites draw them.
        _viewport = new Canvas
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            ManipulationMode = ManipulationModes.TranslateY | ManipulationModes.TranslateInertia
        };
        _viewport.Children.Add(_lines);
        ElementCompositionPreview.GetElementVisual(_viewport).Opacity = 0;
        _layer = _compositor.CreateContainerVisual();
        ElementCompositionPreview.SetElementChildVisual(_viewport, _layer);
        _layerSurface = _compositor.CreateVisualSurface();
        _layerSurface.SourceVisual = _layer;
        _displaySource = _compositor.CreateSurfaceBrush(_layerSurface);
        _displaySource.Stretch = CompositionStretch.None;
        _displaySource.HorizontalAlignmentRatio = _displaySource.VerticalAlignmentRatio = 0;
        var fade = _compositor.CreateLinearGradientBrush();
        fade.StartPoint = Vector2.Zero;
        fade.EndPoint = new Vector2(0, 1);
        foreach (var (offset, alpha) in new[] { (0f, (byte)0), (0.16f, (byte)255), (0.78f, (byte)255), (1f, (byte)0) })
            fade.ColorStops.Add(_compositor.CreateColorGradientStop(offset, Windows.UI.Color.FromArgb(alpha, 255, 255, 255)));
        _fadeBrush = _compositor.CreateMaskBrush();
        _fadeBrush.Source = _displaySource;
        _fadeBrush.Mask = fade;
        _display = _compositor.CreateSpriteVisual();
        _display.Brush = _fadeBrush;
        var displayHost = new Border { IsHitTestVisible = false };
        ElementCompositionPreview.SetElementChildVisual(displayHost, _display);
        try
        {
            _blurEffect = LyricsBlurEffect.Create(new CompositionEffectSourceParameter("source"));
            _blurFactory = _compositor.CreateEffectFactory(_blurEffect, [BlurProperty]);
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidCastException or NotSupportedException)
        {
            // Without the Direct2D interop the lines stay sharp; layout and motion are unchanged.
            _blurFactory = null;
            _blurError = $"{error.GetType().Name} 0x{error.HResult:X8}: {error.Message}";
        }
        _viewport.SizeChanged += (_, args) =>
        {
            _geometryDirty = true;
            var size = new Vector2((float)args.NewSize.Width, (float)args.NewSize.Height);
            _layer.Size = _display.Size = _layerSurface.SourceSize = size;
            _lines.Width = args.NewSize.Width;
            double fontSize = Math.Clamp(args.NewSize.Width / 13, 22, 44);
            if (Math.Abs(fontSize - _fontSize) > 0.5)
            {
                _fontSize = fontSize;
                foreach (Line? entry in _entries)
                    if (entry is not null) ApplyTypography(entry.Block);
            }
            Relayout(false);
        };
        _lines.SizeChanged += (_, _) => { _geometryDirty = true; Relayout(false); };
        _viewport.PointerWheelChanged += (_, args) =>
        {
            ScrollBy(args.GetCurrentPoint(_viewport).Properties.MouseWheelDelta * 0.8);
            args.Handled = true;
        };
        _viewport.ManipulationStarted += (_, _) => _manipulating = true;
        _viewport.ManipulationDelta += (_, args) =>
        {
            ScrollBy(args.Delta.Translation.Y);
            args.Handled = true;
        };
        _viewport.ManipulationCompleted += (_, _) => EndManualGesture();
        _viewport.PointerCanceled += (_, _) => EndManualGesture();
        _viewport.Unloaded += (_, _) => EndManualGesture();
        _source = new TextBlock { FontSize = 12, Opacity = 0.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        SetRow(_source, 1);
        Children.Add(_viewport);
        Children.Add(displayHost);
        Children.Add(_source);
    }

    public bool AnimationsEnabled { get; set; } = true;

    /// <summary>Blur and edge fading; turned off for high contrast so every line stays crisp.</summary>
    public bool EffectsEnabled
    {
        get => _effectsEnabled;
        set
        {
            if (_effectsEnabled == value) return;
            _effectsEnabled = value;
            _display.Brush = value ? _fadeBrush : _displaySource;
            Relayout(false);
        }
    }

    /// <summary>Index of the emphasized synced line, or -1 (before the first line or during an interlude).</summary>
    internal int ActiveLineIndex => _active >= 0 && _active < _entries.Length && _entries[_active] is not null ? _active : -1;

    /// <summary>Blur is used only once Composition has compiled the effect.</summary>
    internal bool BlurAvailable => _blurFactory?.LoadStatus == CompositionEffectFactoryLoadStatus.Success;
    internal string? BlurError => BlurAvailable || _blurFactory is null ? _blurError
        : $"LoadStatus {_blurFactory.LoadStatus} 0x{_blurFactory.ExtendedError?.HResult ?? 0:X8}";
    private readonly string? _blurError;

    /// <summary>Target blur of a line in DIPs, for diagnostics.</summary>
    internal float LineBlur(int index) => _effectsEnabled && index >= 0 && index < _entries.Length && _entries[index] is { } entry ? Math.Max(0, entry.Blur) : 0;

    public void SetSourceText(string text) => _source.Text = text;

    /// <summary>Replaces the lines. Null fades the old lines out until new lyrics arrive.</summary>
    public void SetLyrics(Lyrics? lyrics)
    {
        if (ReferenceEquals(lyrics, _lyrics)) return;
        _lyrics = lyrics;
        _active = -1;
        _manualTranslation = 0;
        _manualBrowsing = _manipulating = false;
        if (lyrics is null)
        {
            AnimateScalar(_display, "Opacity", 0f, TimeSpan.FromMilliseconds(200));
            _source.Visibility = Visibility.Collapsed;
            return;
        }
        ClearLines();
        _entries = new Line?[lyrics.Lines.Count];
        bool synced = lyrics.Kind == LyricsKind.Synced;
        for (int index = 0; index < lyrics.Lines.Count; ++index)
        {
            string text = lyrics.Lines[index].Text;
            // Blank synced entries mark interludes; they only end the previous line.
            if (synced && text.Length == 0) continue;
            var block = new TextBlock { Text = text.Length > 0 ? text : " ", TextWrapping = TextWrapping.Wrap };
            ApplyTypography(block);
            _lines.Children.Add(block);
            var surface = _compositor.CreateVisualSurface();
            surface.SourceVisual = ElementCompositionPreview.GetElementVisual(block);
            var glyphs = _compositor.CreateSurfaceBrush(surface);
            glyphs.Stretch = CompositionStretch.None;
            glyphs.HorizontalAlignmentRatio = glyphs.VerticalAlignmentRatio = 0;
            CompositionBrush brush = glyphs;
            if (BlurAvailable)
            {
                var effect = _blurFactory!.CreateBrush();
                effect.SetSourceParameter("source", glyphs);
                brush = effect;
            }
            var sprite = _compositor.CreateSpriteVisual();
            sprite.Brush = brush;
            sprite.Opacity = synced ? InactiveOpacity : PlainOpacity;
            sprite.Scale = synced ? new Vector3(InactiveScale, InactiveScale, 1) : Vector3.One;
            _layer.Children.InsertAtTop(sprite);
            _entries[index] = new Line(block, sprite, surface, brush);
        }
        _source.Visibility = Visibility.Visible;
        _display.StopAnimation("Opacity");
        _display.Opacity = 0;
        if (!_revealPending) _lines.LayoutUpdated += RevealAfterLayout;
        _revealPending = true;
    }

    /// <summary>Emphasizes the line for the playback position and moves the lines when it changes.</summary>
    public void SetPosition(long positionMs)
    {
        if (_lyrics is not { Kind: LyricsKind.Synced } lyrics) return;
        int active = lyrics.ActiveIndex(positionMs);
        bool resume = _manualBrowsing && !_manipulating && Stopwatch.GetElapsedTime(_manualScrollAt) >= ManualScrollHold;
        if (active == _active && !resume) return;
        _active = active;
        if (resume) _manualBrowsing = false;
        Relayout(AnimationsEnabled);
    }

    private void RevealAfterLayout(object? sender, object args)
    {
        // New lines need measured offsets before they are placed and shown.
        if (_lyrics is not null && (_lines.ActualHeight <= 0 || _viewport.ActualHeight <= 0)) return;
        _lines.LayoutUpdated -= RevealAfterLayout;
        _revealPending = false;
        if (_lyrics is null) return;
        Relayout(false);
        if (!AnimationsEnabled) { _display.Opacity = 1; _display.Offset = Vector3.Zero; return; }
        _display.Offset = new Vector3(0, 24, 0);
        AnimateScalar(_display, "Opacity", 1f, RevealDuration);
        AnimateVector(_display, "Offset", Vector3.Zero, RevealDuration, TimeSpan.Zero);
    }

    private void ClearLines()
    {
        foreach (Line? entry in _entries)
        {
            if (entry is null) continue;
            _layer.Children.Remove(entry.Sprite);
            entry.Sprite.Dispose();
            entry.Brush.Dispose();
            entry.Surface.Dispose();
        }
        _entries = [];
        _geometryDirty = true;
        _hasPlacement = false;
        _lastActive = -1;
        _lines.Children.Clear();
    }

    private void ApplyTypography(TextBlock block)
    {
        double size = _fontSize;
        block.FontSize = size;
        // Use the value type directly: the WinUI FontWeights activation factory is unavailable
        // in some unpackaged launches. Timed and plain lyrics share the same Bold weight.
        block.FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 };
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        block.LineHeight = size * 1.2;
        block.Margin = new Thickness(0, 0, 0, size * 0.62);
    }

    private int AnchorIndex()
    {
        // During an interlude the upcoming line stays at the anchor.
        int anchor = Math.Max(0, _active);
        while (anchor < _entries.Length && _entries[anchor] is null) ++anchor;
        if (anchor < _entries.Length) return anchor;
        for (anchor = _entries.Length - 1; anchor >= 0 && _entries[anchor] is null; --anchor) { }
        return anchor;
    }

    private (double Base, double Minimum, double Maximum) ScrollRange()
    {
        double height = _viewport.ActualHeight;
        if (_lyrics?.Kind != LyricsKind.Synced)
            return (height * 0.1, Math.Min(height * 0.1, height * 0.75 - _lines.ActualHeight), height * 0.1);
        double anchorY = height * AnchorRatio;
        Line first = _entries.First(entry => entry is not null)!, last = _entries.Last(entry => entry is not null)!;
        return (anchorY - _entries[AnchorIndex()]!.Top, anchorY - last.Top, anchorY - first.Top);
    }

    private void EndManualGesture()
    {
        _manipulating = false;
        if (_manualBrowsing) _manualScrollAt = Stopwatch.GetTimestamp();
    }

    private void ScrollBy(double delta)
    {
        if (_lines.Children.Count == 0 || _viewport.ActualHeight <= 0) return;
        var range = ScrollRange();
        if (!_manualBrowsing) _manualTranslation = _hasPlacement ? _lastTranslation : range.Base;
        _manualBrowsing = true;
        _manualTranslation = Math.Clamp(_manualTranslation + delta, range.Minimum, range.Maximum);
        _manualScrollAt = Stopwatch.GetTimestamp();
        // Browsing shows every line sharply, as the native player does.
        Place(_manualTranslation, TimeSpan.Zero, staggered: false, browsing: true);
    }

    private void Relayout(bool animate)
    {
        if (_lines.Children.Count == 0 || _viewport.ActualHeight <= 0 || _lines.ActualHeight <= 0) return;
        if (_geometryDirty)
        {
            _geometryDirty = false;
            foreach (Line? entry in _entries)
            {
                if (entry is null) continue;
                // Only layout changes invalidate the captured glyph geometry.
#if CLEARCAST_UI_TESTS
                _uiGeometryWrites += 4;
#endif
                var size = new Vector2((float)entry.Block.ActualWidth + 2 * BlurPadding, (float)entry.Block.ActualHeight + 2 * BlurPadding);
                entry.Top = entry.Block.ActualOffset.Y;
                entry.Surface.SourceOffset = new Vector2(-BlurPadding, -BlurPadding);
                entry.Surface.SourceSize = entry.Sprite.Size = size;
                entry.Sprite.CenterPoint = new Vector3(BlurPadding, size.Y / 2, 0);
            }
        }
        var range = ScrollRange();
        double translation = Math.Clamp(_manualBrowsing ? _manualTranslation : range.Base, range.Minimum, range.Maximum);
        if (_manualBrowsing) _manualTranslation = translation;
        Place(translation, animate ? ScrollDuration : TimeSpan.Zero,
            staggered: animate && !_manualBrowsing, browsing: _manualBrowsing);
    }

    private void Place(double translation, TimeSpan duration, bool staggered, bool browsing)
    {
#if CLEARCAST_UI_TESTS
        ++_uiPlaceCalls;
#endif
        bool synced = _lyrics?.Kind == LyricsKind.Synced;
        int anchor = AnchorIndex();
        double height = _viewport.ActualHeight;
        TimeSpan emphasis = duration > TimeSpan.Zero ? EmphasisDuration : TimeSpan.Zero;
        TimeSpan motion = browsing ? TimeSpan.Zero : duration;
        for (int index = 0; index < _entries.Length; ++index)
        {
            if (_entries[index] is not { } entry) continue;
            double y = entry.Top + translation;
            bool visible = y > -2 * height && y < 3 * height;
            bool entering = visible && !entry.Visible;
            if (visible != entry.Visible) entry.Sprite.IsVisible = entry.Visible = visible;
            // Retain the original overscan band, but submit no hidden-row work.
            if (!visible) continue;
#if CLEARCAST_UI_TESTS
            ++_uiVisitedLines;
#endif
            if (entering)
            {
                // Hidden rows did not follow previous scrolls. Seed their last
                // position before the same eased movement into the viewport.
                entry.Sprite.StopAnimation("Offset");
                var previous = new Vector3(-BlurPadding, (float)(entry.Top + (_hasPlacement ? _lastTranslation : translation)) - BlurPadding, 0);
                entry.Sprite.Offset = previous;
                entry.Offset = previous;
                entry.OffsetAnimating = false;
                if (synced)
                {
                    bool wasCurrent = index == _lastActive;
                    entry.Sprite.StopAnimation("Opacity");
                    entry.Sprite.StopAnimation("Scale");
                    entry.Sprite.Opacity = wasCurrent ? 1f : InactiveOpacity;
                    float previousScale = wasCurrent ? 1f : InactiveScale;
                    entry.Sprite.Scale = new Vector3(previousScale, previousScale, 1);
                    entry.Emphasized = wasCurrent;
                    entry.EmphasisAnimating = false;
                }
            }
            // Lines after the leader follow it with a short cascade.
            int steps = staggered ? Math.Clamp(index - anchor, 0, MaximumStaggeredLines) : 0;
            var offset = new Vector3(-BlurPadding, (float)y - BlurPadding, 0);
            if (entry.Offset != offset || motion <= TimeSpan.Zero && entry.OffsetAnimating)
            {
                AnimateVector(entry.Sprite, "Offset", offset, motion, LineDelay * steps);
                entry.Offset = offset;
                entry.OffsetAnimating = motion > TimeSpan.Zero;
            }
            if (!synced) continue;
            bool current = index == _active;
            if (entry.Emphasized != current || emphasis <= TimeSpan.Zero && entry.EmphasisAnimating)
            {
                AnimateScalar(entry.Sprite, "Opacity", current ? 1f : InactiveOpacity, emphasis);
                float scale = current ? 1f : InactiveScale;
                AnimateVector(entry.Sprite, "Scale", new Vector3(scale, scale, 1), emphasis, TimeSpan.Zero);
                entry.Emphasized = current;
                entry.EmphasisAnimating = emphasis > TimeSpan.Zero;
            }
            int distance = Math.Abs(index - (_active >= 0 && _entries[_active] is not null ? _active : anchor));
            float blur = current || browsing || !_effectsEnabled ? 0 : Math.Min(4f, 0.7f + 0.7f * (distance - 1));
            if (entry.Brush is CompositionEffectBrush effect && blur != entry.Blur)
            {
                if (emphasis > TimeSpan.Zero && entry.Blur >= 0 && _effectsEnabled && !entering)
                {
#if CLEARCAST_UI_TESTS
                    ++UiBlurAnimationStarts;
#endif
                    AnimateScalar(effect, BlurProperty, blur, emphasis);
                }
                else { effect.StopAnimation(BlurProperty); effect.Properties.InsertScalar(BlurProperty, blur); }
            }
            entry.Blur = blur;
        }
        _lastTranslation = translation;
        _hasPlacement = true;
        _lastActive = _active;
    }

    private void AnimateScalar(CompositionObject target, string property, float value, TimeSpan duration)
    {
        target.StopAnimation(property);
        if (duration <= TimeSpan.Zero)
        {
            if (target is Visual visual && property == "Opacity") visual.Opacity = value;
            else target.Properties.InsertScalar(property, value);
            return;
        }
        var animation = _scalarAnimation;
        animation.InsertKeyFrame(1, value, _ease);
        animation.Duration = duration;
#if CLEARCAST_UI_TESTS
        ++_uiStartedAnimations;
#endif
        target.StartAnimation(property, animation);
    }

    private void AnimateVector(Visual visual, string property, Vector3 value, TimeSpan duration, TimeSpan delay)
    {
        visual.StopAnimation(property);
        if (duration <= TimeSpan.Zero)
        {
            if (property == "Scale") visual.Scale = value;
            else visual.Offset = value;
            return;
        }
        var animation = _vectorAnimation;
        animation.InsertKeyFrame(1, value, _ease);
        animation.Duration = duration;
        animation.DelayTime = delay;
#if CLEARCAST_UI_TESTS
        ++_uiStartedAnimations;
#endif
        visual.StartAnimation(property, animation);
    }
}
