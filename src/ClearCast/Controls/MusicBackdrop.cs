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
    internal const int VideoTransitionMilliseconds = 100;
    private sealed class FlowResources(Compositor compositor)
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
        internal Task ControllerRetirement = Task.CompletedTask;
        internal int ArtworkGeneration;
        internal float BeatDirection = 1;
        internal bool BeatEnabled, BeatScheduled;
        internal bool ClockStarted;
        internal bool RateControlUnavailable;
        internal bool VideoFrame;
        private bool _disposed;

        public async Task DisposeAsync()
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
            // XAML image sources need a live core; release them when retiring the flow,
            // rather than in a later commit callback that could overlap window teardown.
            foreach (var surface in Surfaces) Close(surface.Dispose);
            foreach (var stream in Streams) Close(stream.Dispose);
            // A controller/rate change can still be queued in DComp's raw-pointer rebuild
            // list. Keep the detached resource group alive until that commit has drained.
            await WaitForResourceCommitAsync(Root.Compositor);
            await ControllerRetirement;
            if (Controller is { } controller) Close(controller.Dispose);
            foreach (var animation in Animations) Close(animation.Dispose);
            foreach (var expression in BeatExpressions) Close(expression.Dispose);
            if (Timeline is { } timeline) Close(timeline.Dispose);
            if (BeatAnimation is { } beatAnimation) Close(beatAnimation.Dispose);
            if (BeatEasing is { } beatEasing) Close(beatEasing.Dispose);
            foreach (var layer in Layers) Close(layer.Dispose);
            foreach (var brush in Brushes) Close(brush.Dispose);
            Close(Clock.Dispose);
            Close(Root.Dispose);
        }
    }

    private readonly Border _fallback = new() { Opacity = 0 };
    private readonly SolidColorBrush _fallbackBrush = new();
    private readonly MusicMotion _motion = new();
    private readonly BeatImpulse _beat = new();
    private readonly Stopwatch _videoClock = new();
    private MusicPalette _palette = MusicPalette.FromBgra([], false);
    private ContainerVisual? _root;
    private SpriteVisual? _fill;
    private CompositionColorBrush? _fillBrush;
    private FlowResources? _flow;
    private Task _flowRetirement = Task.CompletedTask;
    private Task _imagePump = Task.CompletedTask;
    private Task? _disposeTask;
    private CancellationTokenSource? _artworkCancellation;
    private sealed record ImageRequest(byte[] Bytes, bool DarkTheme, bool VideoFrame, bool Smooth,
        int Generation, TaskCompletionSource<bool> Completion);
    private ImageRequest? _imageRequest;
    private bool _imageRunning;
    private long _lastActivityAt;
    private float _appliedMotionRate = MusicMotion.BaseRate;
    private int _artworkGeneration;
    private bool _active, _animate, _loaded, _disposed;
    internal bool IsVideoTransitioning { get; private set; }
#if CLEARCAST_UI_TESTS
    internal Func<Task>? UiBeforeImageDecodeAsync;
    internal int UiImageDecodeStarts, UiImageDecodeActive, UiMaxImageDecodeActive;
    internal bool UiImagePending => _imageRequest is not null;
#endif

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
    internal bool HasArtworkFlow => TextureFrameCount == (_flow?.VideoFrame == true ? 1 : ArtworkFlow.FrameCount);
    internal int RateControlledChannelCount => _flow?.Controller is null ? 0 : 1;
    internal int AnimationChannelCount => IsAnimating && _flow is { } flow ? flow.Animations.Count + 1 + BeatAnimationChannelCount : 0;
    internal long AnimationStartCount { get; private set; }
    internal long BeatStartCount { get; private set; }
    internal bool BeatAnimationActive => IsAnimating && _flow is { BeatEnabled: true, BeatScheduled: true } && _beat.IsActive(Environment.TickCount64);
    internal int BeatAnimationChannelCount => IsAnimating && _flow is { BeatEnabled: true } flow ? flow.BeatExpressions.Count + (BeatAnimationActive ? 1 : 0) : 0;
    internal Vector2 BeatOffsetLimit => BeatImpulse.OffsetLimit((float)ActualWidth, (float)ActualHeight);
    internal ulong BeatEpoch => _beat.Epoch;
    internal float MotionRate => _appliedMotionRate;
    internal float? AppliedPlaybackRate
    {
        get
        {
            try { return _flow?.Controller?.PlaybackRate; }
            catch (Exception error) when (IsCompositionFailure(error)) { return null; }
        }
    }
    internal float? Phase
    {
        get
        {
            try { return _flow?.VideoFrame == true ? (float)(_videoClock.Elapsed.TotalSeconds / 48 % 1) : _flow?.Controller?.Progress; }
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
            CloseComposition(() => flow.Clock.StopAnimation("Phase"));
            flow.ControllerRetirement = RetireControllerAsync(controller, flow.Root.Compositor);
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

    public Task SetArtworkAsync(byte[]? bytes, bool darkTheme) => SetImageAsync(bytes, darkTheme, false, false);

    public Task SetVideoFrameAsync(byte[] pixels, bool darkTheme, bool smooth = true) => SetImageAsync(pixels, darkTheme, true, smooth);

    private Task SetImageAsync(byte[]? bytes, bool darkTheme, bool videoFrame, bool smooth)
    {
        if (_disposed) return Task.CompletedTask;
        int generation = ++_artworkGeneration;
        _artworkCancellation?.Cancel();
        _imageRequest?.Completion.TrySetResult(true);
        _imageRequest = null;
        if (bytes is not { Length: > 0 and <= 2 * 1024 * 1024 })
        {
            try
            {
                if (!videoFrame) _videoClock.Reset();
                ResetBeatActivity();
                // Clearing must not wait behind an uncancellable decoder/upload.
                SetStaticPalette(MusicPalette.FromBgra([], darkTheme));
                IsVideoTransitioning = false;
            }
            catch (Exception error) when (IsCompositionFailure(error)) { return Task.FromException(error); }
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _imageRequest = new ImageRequest(bytes, darkTheme, videoFrame, smooth, generation, completion);
        if (!_imageRunning) _imagePump = ApplyPendingImagesAsync();
        return completion.Task;
    }

    private async Task ApplyPendingImagesAsync()
    {
        _imageRunning = true;
        try
        {
            while (!_disposed && _imageRequest is not null)
            {
                // Keep the existing one-running/one-latest bound while old GPU resources
                // wait for their last commit, including when the window is suspended.
                await _flowRetirement;
                if (_disposed || _imageRequest is not { } request) break;
                _imageRequest = null;
                try
                {
                    await ApplyImageAsync(request.Bytes, request.DarkTheme, request.VideoFrame, request.Smooth, request.Generation);
                    request.Completion.TrySetResult(true);
                }
                catch (Exception error) { request.Completion.TrySetException(error); }
            }
        }
        finally { _imageRunning = false; }
    }

    private async Task ApplyImageAsync(byte[] bytes, bool darkTheme, bool videoFrame, bool smooth, int generation)
    {
        if (!videoFrame) _videoClock.Reset();
        ResetBeatActivity();
        _artworkCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _artworkCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        MusicPalette palette = MusicPalette.FromBgra([], darkTheme);
        FlowResources? pending = null;
        FlowResources? retired = null;
        try
        {
#if CLEARCAST_UI_TESTS
            UiImageDecodeStarts++;
            UiImageDecodeActive++;
            UiMaxImageDecodeActive = Math.Max(UiMaxImageDecodeActive, UiImageDecodeActive);
            if (UiBeforeImageDecodeAsync is { } beforeDecode) await beforeDecode();
#endif
            token.ThrowIfCancellationRequested();
            byte[] source;
            int sourceSide;
            if (videoFrame)
            {
                if (bytes.Length != ReceiverVideoFrame.ByteCount) return;
                source = bytes;
                sourceSide = ReceiverVideoFrame.Side;
                palette = MusicPalette.FromBgra(source, darkTheme);
            }
            else
            {
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }
                token.ThrowIfCancellationRequested();
                stream.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(stream);
                token.ThrowIfCancellationRequested();
                if (!ArtworkImage.IsSupported(decoder))
                {
                    if (generation == _artworkGeneration && !_disposed) SetStaticPalette(palette);
                    return;
                }
                var transform = new BitmapTransform { ScaledWidth = 32, ScaledHeight = 32, InterpolationMode = BitmapInterpolationMode.Linear };
                var sampled = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
                token.ThrowIfCancellationRequested();
                palette = MusicPalette.FromBgra(sampled.DetachPixelData(), darkTheme);
                transform.ScaledWidth = transform.ScaledHeight = ArtworkFlow.Side;
                var decoded = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
                source = decoded.DetachPixelData();
                sourceSide = ArtworkFlow.Side;
            }
            token.ThrowIfCancellationRequested();
            double videoPhase = _videoClock.Elapsed.TotalSeconds / 48;
            byte[][] frames = await Task.Run(() => videoFrame
                ? new[] { ArtworkFlow.GenerateFrame(source, sourceSide, sourceSide, videoPhase, darkTheme, token) }
                : ArtworkFlow.Generate(source, sourceSide, sourceSide, darkTheme, token), token);
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _artworkGeneration) return;
            EnsureVisuals();
            pending = await CreateFlowAsync(frames, generation, token, videoFrame);
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _artworkGeneration) return;
            if (videoFrame)
            {
                retired = _flow;
                _flow = null;
                IsAnimating = false;
            }
            else
            {
                ClearFlow();
                await _flowRetirement;
                token.ThrowIfCancellationRequested();
                if (_disposed || generation != _artworkGeneration) return;
                _palette = palette;
                ApplyPalette();
            }
            _flow = pending;
            pending = null;
            if (videoFrame) _flow.Root.Opacity = 0;
            _root!.Children.InsertAtTop(_flow.Root);
            UpdatePlayback();
            if (videoFrame)
            {
                if (smooth)
                {
                    IsVideoTransitioning = true;
                    using var fade = _root.Compositor.CreateScalarKeyFrameAnimation();
                    fade.InsertKeyFrame(0, 0);
                    fade.InsertKeyFrame(1, 1, _root.Compositor.CreateLinearEasingFunction());
                    fade.Duration = TimeSpan.FromMilliseconds(VideoTransitionMilliseconds);
                    _flow.Root.StartAnimation("Opacity", fade);
                    await Task.Delay(VideoTransitionMilliseconds, token);
                }
                token.ThrowIfCancellationRequested();
                if (_disposed || generation != _artworkGeneration) return;
                _flow.Root.StopAnimation("Opacity");
                _flow.Root.Opacity = 1;
                _palette = palette;
                ApplyPalette();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (IsCompositionFailure(error) || error is IOException or TimeoutException)
        {
            if (!_disposed && generation == _artworkGeneration) SetStaticPalette(palette);
        }
        finally
        {
#if CLEARCAST_UI_TESTS
            UiImageDecodeActive--;
#endif
            if (generation == _artworkGeneration) IsVideoTransitioning = false;
            if (retired is not null)
            {
                CloseComposition(() => _root?.Children.Remove(retired.Root));
                await retired.DisposeAsync();
            }
            if (pending is not null) await pending.DisposeAsync();
        }
    }

    private async Task<FlowResources> CreateFlowAsync(byte[][] frames, int generation, CancellationToken cancellation, bool videoFrame)
    {
        var compositor = _root!.Compositor;
        var flow = new FlowResources(compositor);
        flow.ArtworkGeneration = generation;
        flow.VideoFrame = videoFrame;
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
                cancellation.ThrowIfCancellationRequested();
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, ArtworkFlow.Side, ArtworkFlow.Side, 96, 96, frame);
                await encoder.FlushAsync();
                cancellation.ThrowIfCancellationRequested();
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
            for (int index = 0; index < (videoFrame ? 1 : ArtworkFlow.FrameCount + 1); index++)
            {
                var brush = compositor.CreateSurfaceBrush(flow.Surfaces[index % frames.Length]);
                brush.Stretch = CompositionStretch.Fill;
                flow.Brushes.Add(brush);
                var layer = compositor.CreateSpriteVisual();
                layer.RelativeSizeAdjustment = Vector2.One;
                layer.Brush = brush;
                layer.Opacity = videoFrame ? 1 : ArtworkFlow.LayerOpacity(index, 0);
                flow.Layers.Add(layer);
                flow.Root.Children.InsertAtTop(layer);
                if (videoFrame) continue;
                var opacity = compositor.CreateExpressionAnimation(OpacityExpression(index));
                opacity.SetReferenceParameter("clock", flow.Clock);
                layer.StartAnimation("Opacity", opacity);
                AnimationStartCount++;
                flow.Animations.Add(opacity);
            }
            if (videoFrame)
            {
                flow.Root.Scale = new Vector3(BeatImpulse.BaselineScale, BeatImpulse.BaselineScale, 1);
                UpdateBeatGeometry(flow);
                return flow;
            }
            flow.Timeline = compositor.CreateScalarKeyFrameAnimation();
            flow.Timeline.InsertKeyFrame(0, 0);
            flow.Timeline.InsertKeyFrame(1, 1, compositor.CreateLinearEasingFunction());
            flow.Timeline.Duration = TimeSpan.FromSeconds(48);
            flow.Timeline.IterationBehavior = AnimationIterationBehavior.Forever;
            PrepareBeatVisuals(flow);
            return flow;
        }
        catch { await flow.DisposeAsync(); throw; }
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
            if (_flow is { } flow && (flow.BeatEnabled || flow.VideoFrame))
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
        if (_flow?.VideoFrame == true)
        {
            if (run) _videoClock.Start();
            else _videoClock.Stop();
            IsAnimating = run;
            return;
        }
        _videoClock.Stop();
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
                    if (failedController is not null) paused.ControllerRetirement = RetireControllerAsync(failedController, paused.Root.Compositor);
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

    private static async Task RetireControllerAsync(AnimationController controller, Compositor compositor)
    {
        await WaitForResourceCommitAsync(compositor);
        CloseComposition(controller.Dispose);
    }

    private static async Task WaitForResourceCommitAsync(Compositor compositor)
    {
        int delay = 16;
        for (;;)
        {
            try { await compositor.RequestCommitAsync(); return; }
            // A closed compositor cannot submit another rebuild. Other failures are not a
            // completed fence: retain the resources and retry before admitting a new flow.
            catch (COMException error) when (error.HResult == unchecked((int)0x80000013)) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception error) when (IsCompositionFailure(error) || error is OperationCanceledException)
            {
                await Task.Delay(delay);
                delay = Math.Min(250, delay * 2);
            }
        }
    }

    private void ClearFlow()
    {
        _videoClock.Stop();
        ResetBeatActivity();
        IsAnimating = false;
        if (_flow is null) return;
        _root?.Children.Remove(_flow.Root);
        _flowRetirement = _flow.DisposeAsync();
        _flow = null;
    }

    public void Dispose() => _ = DisposeAsync();

    internal Task DisposeAsync() => _disposeTask ??= DisposeCoreAsync();

    private async Task DisposeCoreAsync()
    {
        if (_disposed) return;
        ++_artworkGeneration;
        _imageRequest?.Completion.TrySetResult(true);
        _imageRequest = null;
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
        // LoadedImageSurface.Close requires the live XAML core. Drain deferred cleanup
        // before closing parent visuals or allowing the owning window to be destroyed.
        await _imagePump;
        await _flowRetirement;
        _fill?.Dispose();
        _fillBrush?.Dispose();
        _root?.Clip?.Dispose();
        _root?.Dispose();
        _root = null;
    }
}
