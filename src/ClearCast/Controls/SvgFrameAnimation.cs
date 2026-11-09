using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace ClearCast;

public sealed partial class SvgFrameAnimation : UserControl, IDisposable
{
    internal const int FrameCount = 80;
    internal const int FramesPerSecond = 30;
    private const int DecodeConcurrency = 4;
    private const int StartupBufferFrames = 6;
    private const double PreferredWidth = 480;
    private const double AspectRatio = 498.0 / 324;

    private readonly Image _image = new() { Stretch = Stretch.Uniform, UseLayoutRounding = true };
    private readonly Stopwatch _playback = new();
    private readonly Stopwatch _startup = Stopwatch.StartNew();
    private readonly Task<byte[]>?[] _svgBytes = new Task<byte[]>?[FrameCount];
    private readonly List<double> _startupPresentationTimes = [];
    private readonly List<int> _startupPresentationIndices = [];
    private readonly List<double> _startupRenderingCallbacks = [];
    private SvgImageSource?[]? _frames;
    private XamlRoot? _root;
    private int _decodeGeneration;
    private int _contiguousFrames;
    private bool _bufferedPlaybackStarted;
    private bool _waitingForFrame;
    private bool _isPlaying;
    private bool _subscribed;
    private bool _disposed;

    public SvgFrameAnimation()
    {
        Content = _image;
        UseLayoutRounding = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            _isPlaying = value;
            UpdatePlayback();
        }
    }

    internal int LoadedFrameCount { get; private set; }
    internal int CurrentFrameIndex { get; private set; } = -1;
    internal long PresentedFrameCount { get; private set; }
    internal long CompletedLoops => (long)(_playback.Elapsed.TotalSeconds * FramesPerSecond) / FrameCount;
    internal bool IsAnimating => _subscribed;
    internal Exception? LoadError { get; private set; }
    internal int TargetPixelWidth { get; private set; }
    internal int TargetPixelHeight { get; private set; }
    internal int DecodedPixelWidth { get; private set; }
    internal int DecodedPixelHeight { get; private set; }
    internal int LoadedFrameCountAtPlaybackStart { get; private set; }
    internal int LoadedFrameCountAtFirstAdvance { get; private set; }
    internal double FirstFrameMilliseconds { get; private set; } = -1;
    internal double FirstAdvanceMilliseconds { get; private set; } = -1;
    internal double AllFramesLoadedMilliseconds { get; private set; } = -1;
    internal double PlaybackStartMilliseconds { get; private set; } = -1;
    internal int BufferStarvationCount { get; private set; }
    internal IReadOnlyList<double> StartupPresentationTimesMilliseconds => _startupPresentationTimes;
    internal IReadOnlyList<int> StartupPresentationFrameIndices => _startupPresentationIndices;
    internal double StartupElapsedMilliseconds => _startup.Elapsed.TotalMilliseconds;
    internal IReadOnlyList<double> StartupRenderingCallbackTimesMilliseconds => _startupRenderingCallbacks;
    internal double MaximumSourceAssignmentMilliseconds { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width)
            ? Math.Min(PreferredWidth, Math.Max(0, availableSize.Width)) : PreferredWidth;
        var size = new Size(width, width / AspectRatio);
        base.MeasureOverride(size);
        return size;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _root = XamlRoot;
        if (_root is not null)
            _root.Changed += OnRootChanged;
        RequestRasterization();
        UpdatePlayback();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Pause();
        DetachRoot();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => RequestRasterization();

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RequestRasterization();

    private void RequestRasterization()
    {
        if (_disposed || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0 || XamlRoot is not { } root)
            return;

        // SetSourceAsync decodes an unattached SVG at this explicit pixel size.
        // Match the final screen pixels so a high-DPI display never magnifies it.
        int width = Math.Max(1, (int)Math.Ceiling(ActualWidth * root.RasterizationScale));
        int height = Math.Max(1, (int)Math.Ceiling(ActualHeight * root.RasterizationScale));
        if (width == TargetPixelWidth && height == TargetPixelHeight)
            return;
        TargetPixelWidth = width;
        TargetPixelHeight = height;
        LoadError = null;
        int generation = ++_decodeGeneration;
        var frames = new SvgImageSource?[FrameCount];
        bool progressive = _frames is null || LoadedFrameCount < FrameCount;
        if (progressive)
        {
            Pause();
            _playback.Reset();
            _frames = frames;
            LoadedFrameCount = 0;
            CurrentFrameIndex = -1;
            _contiguousFrames = 0;
            _bufferedPlaybackStarted = false;
            _waitingForFrame = false;
        }
        _ = LoadFramesAsync(frames, generation, width, height, progressive);
    }

    private async Task LoadFramesAsync(SvgImageSource?[] frames, int generation, int width, int height, bool progressive)
    {
        try
        {
            bool IsCurrent() => !_disposed && generation == _decodeGeneration;

            async Task DecodeFrameAsync(int index)
            {
                var bytes = await (_svgBytes[index] ??= File.ReadAllBytesAsync(Path.Combine(
                    AppContext.BaseDirectory, "Assets", "MirroringAnimation", $"frame-{index:000}.svg")));
                if (!IsCurrent())
                    return;
                using var memory = new MemoryStream(bytes, writable: false);
                using var stream = memory.AsRandomAccessStream();
                var source = new SvgImageSource { RasterizePixelWidth = width, RasterizePixelHeight = height };
                var status = await source.SetSourceAsync(stream);
                if (!IsCurrent())
                    return;
                if (status != SvgImageSourceLoadStatus.Success)
                    throw new InvalidDataException($"SVG frame {index:000} failed to load: {status}.");
                frames[index] = source;
                if (progressive)
                {
                    LoadedFrameCount++;
                    while (_contiguousFrames < frames.Length && frames[_contiguousFrames] is not null)
                        _contiguousFrames++;
                    DecodedPixelWidth = width;
                    DecodedPixelHeight = height;
                    if (index == 0)
                        PresentFrame(0, source);
                    UpdatePlayback();
                }
            }

            // Give the first visible frame priority, then decode ahead using a
            // bounded set of UI-thread async workers, each with its own stream.
            await DecodeFrameAsync(0);
            if (!IsCurrent())
                return;
            int nextFrame = 1;
            async Task DecodeAheadAsync()
            {
                while (IsCurrent())
                {
                    int index = nextFrame++;
                    if (index >= frames.Length)
                        return;
                    await DecodeFrameAsync(index);
                }
            }
            await Task.WhenAll(Enumerable.Range(0, DecodeConcurrency).Select(_ => DecodeAheadAsync()));
            if (!IsCurrent())
                return;
            if (!progressive)
            {
                // Keep the existing animation playing during a DPI change; swap
                // the complete sharper cache without changing its timeline.
                _frames = frames;
                DecodedPixelWidth = width;
                DecodedPixelHeight = height;
                _image.Source = frames[Math.Max(0, CurrentFrameIndex)];
            }
            if (AllFramesLoadedMilliseconds < 0)
                AllFramesLoadedMilliseconds = _startup.Elapsed.TotalMilliseconds;
            UpdatePlayback();
        }
        catch (Exception error)
        {
            if (_disposed || generation != _decodeGeneration)
                return;
            LoadError = error;
            Debug.WriteLine($"Could not load the mirroring illustration: {error}");
        }
    }

    private void UpdatePlayback()
    {
        int index = (int)((long)(_playback.Elapsed.TotalSeconds * FramesPerSecond) % FrameCount);
        if (!_bufferedPlaybackStarted && _contiguousFrames < StartupBufferFrames)
        {
            Pause();
            return;
        }
        if (_disposed || !_isPlaying || !IsLoaded || _frames?[index] is not { } source)
        {
            Pause();
            return;
        }
        PresentFrame(index, source);
        if (_subscribed)
            return;
        if (LoadedFrameCountAtPlaybackStart == 0)
        {
            LoadedFrameCountAtPlaybackStart = LoadedFrameCount;
            PlaybackStartMilliseconds = _startup.Elapsed.TotalMilliseconds;
        }
        _bufferedPlaybackStarted = true;
        _waitingForFrame = false;
        _playback.Start();
        CompositionTarget.Rendering += OnRendering;
        _subscribed = true;
    }

    private void OnRendering(object? sender, object args)
    {
        if (!_subscribed)
            return;
        if (_startupRenderingCallbacks.Count < 300)
            _startupRenderingCallbacks.Add(_startup.Elapsed.TotalMilliseconds);
        // Pause the clock on a loading stall instead of skipping unseen frames.
        int index = (int)((long)(_playback.Elapsed.TotalSeconds * FramesPerSecond) % FrameCount);
        if (_frames?[index] is not { } source)
        {
            if (!_waitingForFrame)
                BufferStarvationCount++;
            _waitingForFrame = true;
            Pause();
            return;
        }
        PresentFrame(index, source);
    }

    private void PresentFrame(int index, SvgImageSource source)
    {
        if (index == CurrentFrameIndex)
            return;
        bool advancing = CurrentFrameIndex >= 0;
        long assignmentStarted = Stopwatch.GetTimestamp();
        _image.Source = source;
        MaximumSourceAssignmentMilliseconds = Math.Max(MaximumSourceAssignmentMilliseconds,
            Stopwatch.GetElapsedTime(assignmentStarted).TotalMilliseconds);
        CurrentFrameIndex = index;
        PresentedFrameCount++;
        if (_startupPresentationTimes.Count < 120)
        {
            _startupPresentationTimes.Add(_startup.Elapsed.TotalMilliseconds);
            _startupPresentationIndices.Add(index);
        }
        if (FirstFrameMilliseconds < 0)
            FirstFrameMilliseconds = _startup.Elapsed.TotalMilliseconds;
        if (advancing && FirstAdvanceMilliseconds < 0)
        {
            FirstAdvanceMilliseconds = _startup.Elapsed.TotalMilliseconds;
            LoadedFrameCountAtFirstAdvance = LoadedFrameCount;
        }
    }

    private void Pause()
    {
        _playback.Stop();
        if (!_subscribed)
            return;
        CompositionTarget.Rendering -= OnRendering;
        _subscribed = false;
    }

    private void DetachRoot()
    {
        if (_root is null)
            return;
        _root.Changed -= OnRootChanged;
        _root = null;
    }

    public void Dispose()
    {
        _disposed = true;
        ++_decodeGeneration;
        Pause();
        DetachRoot();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;
        _image.Source = null;
        _frames = null;
    }
}
