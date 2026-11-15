using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace ClearCast;

public sealed partial class MusicBackdrop
{
    // Streaming video reuses two tiny XAML textures. It must not encode PNGs,
    // rebuild a Composition scene or await a fade/retirement fence per sample.
    private readonly Grid _videoHost = new()
    {
        Visibility = Visibility.Collapsed,
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new ScaleTransform { ScaleX = BeatImpulse.BaselineScale, ScaleY = BeatImpulse.BaselineScale }
    };
    private readonly byte[] _videoFrom = new byte[ArtworkFlow.Side * ArtworkFlow.Side * 4];
    private byte[]? _videoPixels;
    private WriteableBitmap? _videoBaseBitmap, _videoTargetBitmap;
    private Visual? _videoBlendVisual;
    private ScalarKeyFrameAnimation? _videoFade;
    private CompositionEasingFunction? _videoFadeEasing;
    private long _videoTransitionAt;
#if CLEARCAST_UI_TESTS
    internal int UiVideoBitmapCreations;
#endif

    private void ApplyVideoPixels(byte[] pixels, bool smooth)
    {
        if (_videoBaseBitmap is null)
        {
            _videoBaseBitmap = new WriteableBitmap(ArtworkFlow.Side, ArtworkFlow.Side);
            _videoTargetBitmap = new WriteableBitmap(ArtworkFlow.Side, ArtworkFlow.Side);
            _videoHost.Children.Add(new Image { Source = _videoBaseBitmap, Stretch = Stretch.Fill });
            var incoming = new Image { Source = _videoTargetBitmap, Stretch = Stretch.Fill };
            _videoHost.Children.Add(incoming);
            _videoBlendVisual = ElementCompositionPreview.GetElementVisual(incoming);
            var compositor = _videoBlendVisual.Compositor;
            _videoFade = compositor.CreateScalarKeyFrameAnimation();
            _videoFadeEasing = compositor.CreateLinearEasingFunction();
            _videoFade.InsertKeyFrame(0, 0);
            _videoFade.InsertKeyFrame(1, 1, _videoFadeEasing);
            _videoFade.Duration = TimeSpan.FromMilliseconds(VideoTransitionMilliseconds);
#if CLEARCAST_UI_TESTS
            UiVideoBitmapCreations += 2;
#endif
        }
        bool blend = smooth && _videoPixels is not null;
        if (blend)
        {
            // Continue from the color currently being displayed, even when a
            // newer sample arrives before the previous GPU fade has finished.
            float amount = _videoTransitionAt == 0 ? 1 : (float)Math.Clamp(
                Stopwatch.GetElapsedTime(_videoTransitionAt).TotalMilliseconds / VideoTransitionMilliseconds, 0, 1);
            for (int index = 0; index < _videoFrom.Length; index++)
                _videoFrom[index] = (byte)Math.Clamp(MathF.Round(_videoFrom[index] +
                    (_videoPixels![index] - _videoFrom[index]) * amount), 0, 255);
        }
        else pixels.CopyTo(_videoFrom, 0);
        _videoPixels = pixels;
        WritePixels(_videoBaseBitmap, _videoFrom);
        WritePixels(_videoTargetBitmap!, pixels);
        _videoBlendVisual!.StopAnimation("Opacity");
        _videoBlendVisual.Opacity = blend ? 1 : 0;
        _videoTransitionAt = blend ? Stopwatch.GetTimestamp() : 0;
        if (blend) _videoBlendVisual.StartAnimation("Opacity", _videoFade!);
        _videoHost.Visibility = Visibility.Visible;
    }

    private static void WritePixels(WriteableBitmap bitmap, byte[] pixels)
    {
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels);
        bitmap.Invalidate();
    }

    private void StopVideoTransition()
    {
        _videoTransitionAt = 0;
        if (_videoBlendVisual is null) return;
        CloseComposition(() => _videoBlendVisual.StopAnimation("Opacity"));
        CloseComposition(() => _videoBlendVisual.Opacity = 1);
    }

    private void ClearVideoPixels()
    {
        StopVideoTransition();
        _videoPixels = null;
        _videoHost.Visibility = Visibility.Collapsed;
    }
}
