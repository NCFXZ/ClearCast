using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace ClearCast;

public sealed class MusicBackdrop : UserControl, IDisposable
{
    private sealed class FlowResources(Compositor compositor) : IDisposable
    {
        internal readonly ContainerVisual Root = compositor.CreateContainerVisual();
        internal readonly CompositionPropertySet Clock = compositor.CreatePropertySet();
        internal readonly List<SpriteVisual> Layers = [];
        internal readonly List<CompositionSurfaceBrush> Brushes = [];
        internal readonly List<LoadedImageSurface> Surfaces = [];
        internal readonly List<InMemoryRandomAccessStream> Streams = [];
        internal readonly List<CompositionAnimation> Animations = [];
        internal readonly List<ExpressionAnimation> BeatExpressions = [];
        internal ScalarKeyFrameAnimation? Timeline;
        internal ScalarKeyFrameAnimation? BeatAnimation;
        internal CompositionEasingFunction? BeatEasing;
        internal AnimationController? Controller;
        internal int ArtworkGeneration;
        internal float BeatDirection = 1;
        internal bool BeatEnabled, BeatScheduled;
        internal bool ClockStarted;
        internal bool RateControlUnavailable;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            void Close(Action action) { try { action(); } catch (Exception error) when (IsCompositionFailure(error)) { } }
            Close(() => Clock.StopAnimation("Phase"));
            Close(() => Clock.StopAnimation("Beat"));
            Close(() => Root.StopAnimation("Scale"));
            Close(() => Root.StopAnimation("Offset"));
            foreach (var layer in Layers) Close(() => layer.StopAnimation("Opacity"));
            Close(Root.Children.RemoveAll);
            if (Controller is { } controller) Close(controller.Dispose);
            foreach (var animation in Animations) Close(animation.Dispose);
            foreach (var expression in BeatExpressions) Close(expression.Dispose);
            if (Timeline is { } timeline) Close(timeline.Dispose);
            if (BeatAnimation is { } beatAnimation) Close(beatAnimation.Dispose);
            if (BeatEasing is { } beatEasing) Close(beatEasing.Dispose);
            foreach (var layer in Layers) Close(layer.Dispose);
            foreach (var brush in Brushes) Close(brush.Dispose);
            foreach (var surface in Surfaces) Close(surface.Dispose);
            foreach (var stream in Streams) Close(stream.Dispose);
            Close(Clock.Dispose);
            Close(Root.Dispose);
        }
    }

    private readonly Border _fallback = new() { Opacity = 0 };
    private readonly SolidColorBrush _fallbackBrush = new();
    private readonly MusicMotion _motion = new();
    private readonly BeatImpulse _beat = new();
    private MusicPalette _palette = MusicPalette.FromBgra([], false);
    private ContainerVisual? _root;
    private SpriteVisual? _fill;
    private CompositionColorBrush? _fillBrush;
    private FlowResources? _flow;
    private CancellationTokenSource? _artworkCancellation;
    private long _lastActivityAt;
    private float _appliedMotionRate = MusicMotion.BaseRate;
    private int _artworkGeneration;
    private bool _active, _animate, _loaded, _disposed;

    public MusicBackdrop()
    {
        _fallback.Background = _fallbackBrush;
        Content = _fallback;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    internal bool IsActive { get; private set; }
    internal bool IsAnimating { get; private set; }
    internal int TextureFrameCount => _flow?.Surfaces.Count ?? 0;
    internal bool HasArtworkFlow => TextureFrameCount == ArtworkFlow.FrameCount;
    internal int RateControlledChannelCount => _flow?.Controller is null ? 0 : 1;
    internal int AnimationChannelCount => IsAnimating && _flow is { } flow ? flow.Animations.Count + 1 + BeatAnimationChannelCount : 0;
    internal long AnimationStartCount { get; private set; }
    internal long BeatStartCount { get; private set; }
    internal bool BeatAnimationActive => IsAnimating && _flow is { BeatEnabled: true, BeatScheduled: true } && _beat.IsActive(Environment.TickCount64);
    internal int BeatAnimationChannelCount => IsAnimating && _flow is { BeatEnabled: true } flow ? flow.BeatExpressions.Count + (BeatAnimationActive ? 1 : 0) : 0;
    internal float BeatBaselineScale => BeatImpulse.BaselineScale;
    internal float BeatMaximumScale => BeatImpulse.BaselineScale + BeatImpulse.ZoomAmount;
    internal float BeatMinimumScale => BeatImpulse.BaselineScale + BeatImpulse.ZoomAmount * BeatImpulse.MinimumEnvelope;
    internal Vector2 BeatOffsetLimit => BeatImpulse.OffsetLimit((float)ActualWidth, (float)ActualHeight);
    internal ulong BeatEpoch => _beat.Epoch;
    internal ulong BeatSequence => _beat.Sequence;
    internal float MotionRate => _appliedMotionRate;
    internal float? AppliedPlaybackRate
    {
        get
        {
            try { return _flow?.Controller?.PlaybackRate; }
            catch (Exception error) when (IsCompositionFailure(error)) { return null; }
        }
    }
    internal float? AnimationProgress => Phase;
    internal float? Phase
    {
        get
        {
            try { return _flow?.Controller?.Progress; }
            catch (Exception error) when (IsCompositionFailure(error)) { return null; }
        }
    }
    internal MusicPalette Palette => _palette;

    public void SetAudioActivity(float energy, float density, bool hasSample = true)
    {
        if (_disposed) return;
        if (!_animate) { ResetAudioActivity(); return; }
        long now = Stopwatch.GetTimestamp();
        TimeSpan elapsed = _lastActivityAt == 0 ? TimeSpan.FromMilliseconds(150) : Stopwatch.GetElapsedTime(_lastActivityAt, now);
        _lastActivityAt = now;
        float rate = _motion.Update(energy, density, hasSample, elapsed);
        if (Math.Abs(rate - _appliedMotionRate) >= 0.003f) ApplyMotionRate(rate);
    }

    public void ResetAudioActivity()
    {
        if (_disposed) return;
        _motion.Reset();
        _lastActivityAt = 0;
        ApplyMotionRate(MusicMotion.BaseRate);
        ResetBeatActivity();
    }

    public void SetBeatActivity(ulong epoch, ulong sequence, float strength, bool fresh = true)
    {
        if (_disposed) return;
        var flow = _flow;
        bool enabled = IsAnimating && _active && _animate && _loaded &&
            flow is { BeatEnabled: true } && flow.ArtworkGeneration == _artworkGeneration;
        long now = Environment.TickCount64;
        bool continuing = _beat.IsActive(now);
        bool start = _beat.TryStart(epoch, sequence, strength, fresh, enabled, now,
            out float amplitude, out bool epochChanged);
        if (epochChanged) StopBeatVisuals();
        if (!start || flow is null) return;
        if (!ReferenceEquals(flow, _flow) || !flow.BeatEnabled || flow.BeatAnimation is not { } animation)
        {
            _beat.Reset();
            return;
        }
        try
        {
            if (!continuing || epochChanged)
                flow.BeatDirection = (sequence & 1) == 0 ? 1 : -1;
            UpdateBeatGeometry(flow);
            animation.SetScalarParameter("strength", amplitude);
            flow.Clock.StartAnimation("Beat", animation);
            flow.BeatScheduled = true;
            BeatStartCount++;
        }
        catch (Exception error) when (IsCompositionFailure(error))
        {
            DisableBeatVisuals(flow);
            _beat.Reset();
        }
    }

    public void ResetBeatActivity()
    {
        if (_disposed) return;
        _beat.Reset();
        StopBeatVisuals();
    }

    private void StopBeatVisuals()
    {
        if (_flow is not { BeatScheduled: true } flow) return;
        flow.BeatScheduled = false;
        try
        {
            flow.Clock.StopAnimation("Beat");
            flow.Clock.InsertScalar("Beat", 0);
        }
        catch (Exception error) when (IsCompositionFailure(error)) { DisableBeatVisuals(flow); }
    }

#if CLEARCAST_UI_TESTS
    internal float BeatDirectionForTest => _flow?.BeatDirection ?? 0;

    internal void PauseFlowClockForTest(bool paused)
    {
        if (_flow?.Controller is not { } controller) return;
        if (paused) controller.Pause();
        else if (IsAnimating) controller.Resume();
    }
#endif

    private void ApplyMotionRate(float rate)
    {
        _appliedMotionRate = rate;
        var flow = _flow;
        if (flow?.Controller is not { } controller) return;
        try { controller.PlaybackRate = rate / MusicMotion.BaseRate; }
        catch (Exception error) when (IsCompositionFailure(error))
        {
            // Degrade once to the existing slow timeline. Later samples do not
            // retry unsupported control or allocate replacement animations.
            flow.Controller = null;
            flow.ClockStarted = false;
            flow.RateControlUnavailable = true;
            CloseComposition(controller.Dispose);
            CloseComposition(() => flow.Clock.StopAnimation("Phase"));
            if (!_animate || !_active || !_loaded || ActualWidth <= 0 || ActualHeight <= 0)
            {
                IsAnimating = false;
                return;
            }
            try
            {
                flow.Clock.StartAnimation("Phase", flow.Timeline!);
                AnimationStartCount++;
                flow.ClockStarted = true;
            }
            catch (Exception fallbackError) when (IsCompositionFailure(fallbackError)) { IsAnimating = false; }
        }
    }

    public async Task SetArtworkAsync(byte[]? bytes, bool darkTheme)
    {
        if (_disposed) return;
        int generation = ++_artworkGeneration;
        ResetBeatActivity();
        _artworkCancellation?.Cancel();
        _artworkCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _artworkCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        MusicPalette palette = MusicPalette.FromBgra([], darkTheme);
        FlowResources? pending = null;
        try
        {
            if (bytes is not { Length: > 0 and <= 2 * 1024 * 1024 })
            {
                SetStaticPalette(palette);
                return;
            }
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            Guid format = decoder.DecoderInformation.CodecId;
            if ((format != BitmapDecoder.PngDecoderId && format != BitmapDecoder.JpegDecoderId) ||
                decoder.PixelWidth is 0 or > 4096 || decoder.PixelHeight is 0 or > 4096 ||
                (ulong)decoder.PixelWidth * decoder.PixelHeight > 16_000_000)
            {
                if (generation == _artworkGeneration && !_disposed) SetStaticPalette(palette);
                return;
            }
            var transform = new BitmapTransform { ScaledWidth = 32, ScaledHeight = 32, InterpolationMode = BitmapInterpolationMode.Linear };
            var sampled = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
            palette = MusicPalette.FromBgra(sampled.DetachPixelData(), darkTheme);
            transform.ScaledWidth = transform.ScaledHeight = ArtworkFlow.Side;
            var decoded = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
            byte[] source = decoded.DetachPixelData();
            token.ThrowIfCancellationRequested();
            byte[][] frames = await Task.Run(() => ArtworkFlow.Generate(source, ArtworkFlow.Side, ArtworkFlow.Side, darkTheme, token), token);
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _artworkGeneration) return;
            EnsureVisuals();
            pending = await CreateFlowAsync(frames, generation, token);
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _artworkGeneration) return;
            ClearFlow();
            _palette = palette;
            ApplyPalette();
            _flow = pending;
            pending = null;
            _root!.Children.InsertAtTop(_flow.Root);
            UpdatePlayback();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (IsCompositionFailure(error) || error is IOException or TimeoutException)
        {
            if (!_disposed && generation == _artworkGeneration) SetStaticPalette(palette);
        }
        finally { pending?.Dispose(); }
    }

    private async Task<FlowResources> CreateFlowAsync(byte[][] frames, int generation, CancellationToken cancellation)
    {
        var compositor = _root!.Compositor;
        var flow = new FlowResources(compositor);
        flow.ArtworkGeneration = generation;
        flow.Root.RelativeSizeAdjustment = Vector2.One;
        flow.Clock.InsertScalar("Phase", 0);
        try
        {
            foreach (byte[] frame in frames)
            {
                cancellation.ThrowIfCancellationRequested();
                var stream = new InMemoryRandomAccessStream();
                flow.Streams.Add(stream);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, ArtworkFlow.Side, ArtworkFlow.Side, 96, 96, frame);
                await encoder.FlushAsync();
                stream.Seek(0);
                var surface = LoadedImageSurface.StartLoadFromStream(stream);
                flow.Surfaces.Add(surface);
                var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void Loaded(LoadedImageSurface sender, LoadedImageSourceLoadCompletedEventArgs args) =>
                    completed.TrySetResult(args.Status == LoadedImageSourceLoadStatus.Success);
                surface.LoadCompleted += Loaded;
                try
                {
                    if (!await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation))
                        throw new IOException("Could not upload an artwork flow frame.");
                }
                finally { surface.LoadCompleted -= Loaded; }
            }
            for (int index = 0; index <= ArtworkFlow.FrameCount; index++)
            {
                var brush = compositor.CreateSurfaceBrush(flow.Surfaces[index % ArtworkFlow.FrameCount]);
                brush.Stretch = CompositionStretch.Fill;
                flow.Brushes.Add(brush);
                var layer = compositor.CreateSpriteVisual();
                layer.RelativeSizeAdjustment = Vector2.One;
                layer.Brush = brush;
                layer.Opacity = ArtworkFlow.LayerOpacity(index, 0);
                flow.Layers.Add(layer);
                flow.Root.Children.InsertAtTop(layer);
                var opacity = compositor.CreateExpressionAnimation(OpacityExpression(index));
                opacity.SetReferenceParameter("clock", flow.Clock);
                layer.StartAnimation("Opacity", opacity);
                AnimationStartCount++;
                flow.Animations.Add(opacity);
            }
            flow.Timeline = compositor.CreateScalarKeyFrameAnimation();
            flow.Timeline.InsertKeyFrame(0, 0);
            flow.Timeline.InsertKeyFrame(1, 1, compositor.CreateLinearEasingFunction());
            flow.Timeline.Duration = TimeSpan.FromSeconds(48);
            flow.Timeline.IterationBehavior = AnimationIterationBehavior.Forever;
            PrepareBeatVisuals(flow);
            return flow;
        }
        catch { flow.Dispose(); throw; }
    }

    private void PrepareBeatVisuals(FlowResources flow)
    {
        var compositor = flow.Root.Compositor;
        try
        {
            flow.Root.Scale = new Vector3(BeatImpulse.BaselineScale, BeatImpulse.BaselineScale, 1);
            UpdateBeatGeometry(flow);
            flow.Clock.InsertScalar("Beat", 0);
            string baseline = BeatImpulse.BaselineScale.ToString("R", CultureInfo.InvariantCulture);
            string zoom = BeatImpulse.ZoomAmount.ToString("R", CultureInfo.InvariantCulture);
            var scale = compositor.CreateExpressionAnimation("Vector3(" + baseline + " + " + zoom + " * clock.Beat, " + baseline + " + " + zoom + " * clock.Beat, 1)");
            flow.BeatExpressions.Add(scale);
            scale.SetReferenceParameter("clock", flow.Clock);
            flow.Root.StartAnimation("Scale", scale);
            var offset = compositor.CreateExpressionAnimation("clock.BeatOffset * clock.Beat");
            flow.BeatExpressions.Add(offset);
            offset.SetReferenceParameter("clock", flow.Clock);
            flow.Root.StartAnimation("Offset", offset);
            var animation = compositor.CreateScalarKeyFrameAnimation();
            flow.BeatAnimation = animation;
            animation.Duration = TimeSpan.FromMilliseconds(BeatImpulse.DurationMs);
            animation.IterationCount = 1;
            animation.SetScalarParameter("strength", 0);
            flow.BeatEasing = compositor.CreateLinearEasingFunction();
            foreach (var (progress, value) in BeatImpulse.KeyFrames)
            {
                string expression = progress == 0 ? "this.StartingValue" : "strength * " + value.ToString("R", CultureInfo.InvariantCulture);
                animation.InsertExpressionKeyFrame(progress, expression, flow.BeatEasing);
            }
            flow.BeatEnabled = true;
        }
        catch (Exception error) when (IsCompositionFailure(error)) { DisableBeatVisuals(flow); }
    }

    private void UpdateBeatGeometry(FlowResources flow)
    {
        flow.Root.CenterPoint = new Vector3((float)Math.Max(0, ActualWidth) * 0.5f, (float)Math.Max(0, ActualHeight) * 0.5f, 0);
        Vector2 limit = BeatOffsetLimit;
        flow.Clock.InsertVector3("BeatOffset", new Vector3(limit.X * flow.BeatDirection, -limit.Y * 0.65f, 0));
    }

    private void DisableBeatVisuals(FlowResources flow)
    {
        flow.BeatEnabled = false;
        flow.BeatScheduled = false;
        CloseComposition(() => flow.Clock.StopAnimation("Beat"));
        CloseComposition(() => flow.Clock.InsertScalar("Beat", 0));
        CloseComposition(() => flow.Root.StopAnimation("Scale"));
        CloseComposition(() => flow.Root.StopAnimation("Offset"));
        CloseComposition(() => flow.Root.Scale = Vector3.One);
        CloseComposition(() => flow.Root.Offset = Vector3.Zero);
        foreach (var expression in flow.BeatExpressions) CloseComposition(expression.Dispose);
        flow.BeatExpressions.Clear();
        if (flow.BeatAnimation is { } animation) CloseComposition(animation.Dispose);
        flow.BeatAnimation = null;
        if (flow.BeatEasing is { } easing) CloseComposition(easing.Dispose);
        flow.BeatEasing = null;
    }

    private static string OpacityExpression(int index)
    {
        string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        var window = ArtworkFlow.LayerWindow(index);
        if (index == 0) return "clock.Phase < " + Number(window.End) + " ? 1 : 0";
        string fade = "Clamp((clock.Phase - " + Number(window.Begin) + ") * " + ArtworkFlow.FrameCount + ", 0, 1)";
        if (index == ArtworkFlow.FrameCount) return fade;
        // Only the current opaque frame and the incoming alpha frame are visible.
        // The final layer aliases frame zero, so wrapping the clock cannot expose
        // a transparent gap or two partially opaque frames over the base color.
        return "clock.Phase < " + Number(window.End) + " ? " + fade + " : 0";
    }

    public void SetActive(bool active, bool animate)
    {
        if (_disposed || (_active == active && _animate == animate)) return;
        _active = active;
        _animate = animate;
        if (!animate) ResetAudioActivity();
        UpdatePlayback();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _loaded = true;
        if (_disposed) return;
        EnsureVisuals();
        ApplyPalette();
        UpdateSize();
        UpdatePlayback();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) { _loaded = false; UpdatePlayback(); }
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => UpdateSize();

    private void EnsureVisuals()
    {
        if (_root is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _root = compositor.CreateContainerVisual();
        _root.Clip = compositor.CreateInsetClip();
        _root.Opacity = 0;
        _fillBrush = compositor.CreateColorBrush(ToColor(_palette.Background));
        _fill = compositor.CreateSpriteVisual();
        _fill.RelativeSizeAdjustment = Vector2.One;
        _fill.Brush = _fillBrush;
        _root.Children.InsertAtBottom(_fill);
        ElementCompositionPreview.SetElementChildVisual(this, _root);
        UpdateSize();
    }

    private static Color ToColor(uint color) => Color.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color);
    private void ApplyPalette()
    {
        _fallbackBrush.Color = ToColor(_palette.Background);
        if (_fillBrush is not null) _fillBrush.Color = ToColor(_palette.Background);
    }

    private void SetStaticPalette(MusicPalette palette)
    {
        ClearFlow();
        _palette = palette;
        ApplyPalette();
        UpdatePlayback();
    }

    private void UpdateSize()
    {
        if (_root is not null && !_disposed)
        {
            _root.Size = new Vector2((float)Math.Max(0, ActualWidth), (float)Math.Max(0, ActualHeight));
            if (_flow is { BeatEnabled: true } flow)
            {
                try { UpdateBeatGeometry(flow); }
                catch (Exception error) when (IsCompositionFailure(error)) { DisableBeatVisuals(flow); _beat.Reset(); }
            }
        }
    }

    private void UpdatePlayback()
    {
        if (_disposed) return;
        bool visible = _active && _loaded && ActualWidth > 0 && ActualHeight > 0;
        IsActive = visible;
        _fallback.Opacity = visible ? 1 : 0;
        if (_root is not null) _root.Opacity = visible ? 1 : 0;
        bool run = visible && _animate && HasArtworkFlow;
        if (!run) ResetBeatActivity();
        if (run == IsAnimating) return;
        if (!run)
        {
            if (_flow is { ClockStarted: true } paused)
            {
                try
                {
                    if (paused.Controller is { } controller) controller.Pause();
                    else { paused.Clock.StopAnimation("Phase"); paused.ClockStarted = false; }
                }
                catch (Exception error) when (IsCompositionFailure(error))
                {
                    var failedController = paused.Controller;
                    paused.Controller = null;
                    paused.ClockStarted = false;
                    paused.RateControlUnavailable = true;
                    CloseComposition(() => paused.Clock.StopAnimation("Phase"));
                    if (failedController is not null) CloseComposition(failedController.Dispose);
                }
            }
            IsAnimating = false;
            return;
        }
        var flow = _flow!;
        try
        {
            if (!flow.ClockStarted)
            {
                flow.Clock.StartAnimation("Phase", flow.Timeline!);
                AnimationStartCount++;
                flow.ClockStarted = true;
                if (!flow.RateControlUnavailable)
                {
                    try
                    {
                        flow.Controller = flow.Clock.TryGetAnimationController("Phase");
                        flow.RateControlUnavailable = flow.Controller is null;
                    }
                    catch (Exception error) when (IsCompositionFailure(error)) { flow.RateControlUnavailable = true; }
                }
            }
            else flow.Controller?.Resume();
            ApplyMotionRate(_motion.Rate);
            IsAnimating = flow.ClockStarted;
        }
        catch (Exception error) when (IsCompositionFailure(error)) { IsAnimating = false; }
    }

    private static bool IsCompositionFailure(Exception error) =>
        error is COMException or ArgumentException or NotImplementedException or NotSupportedException or InvalidOperationException;

    private static void CloseComposition(Action action)
    {
        try { action(); }
        catch (Exception error) when (IsCompositionFailure(error)) { }
    }

    private void ClearFlow()
    {
        ResetBeatActivity();
        IsAnimating = false;
        if (_flow is null) return;
        _root?.Children.Remove(_flow.Root);
        _flow.Dispose();
        _flow = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        ++_artworkGeneration;
        _artworkCancellation?.Cancel();
        _artworkCancellation?.Dispose();
        _artworkCancellation = null;
        ResetBeatActivity();
        _disposed = true;
        IsActive = false;
        _fallback.Opacity = 0;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;
        ClearFlow();
        if (_root is not null) ElementCompositionPreview.SetElementChildVisual(this, null);
        _fill?.Dispose();
        _fillBrush?.Dispose();
        _root?.Clip?.Dispose();
        _root?.Dispose();
        _root = null;
    }
}
