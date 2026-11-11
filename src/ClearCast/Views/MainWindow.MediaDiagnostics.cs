using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.Storage.Streams;

namespace ClearCast;

public sealed partial class MainWindow
{
    private bool _showMediaDiagnostics;
    private ulong _diagnosticsRendered;
    private ulong _diagnosticsDropped;
    private long _diagnosticsStatsAt;
    private double _diagnosticsFrameRate = double.NaN;
    private (string Text, int Width, int Height, double Scale)? _diagnosticsRasterRequest;
    private (string Text, int Width, int Height, double Scale)? _diagnosticsRasterApplied;
    private int _diagnosticsRasterGeneration;
    private bool _diagnosticsRasterRunning;
    private long _diagnosticsRasterFailedAt;
    private string _diagnosticsRenderError = "";
#if CLEARCAST_UI_TESTS
    private byte[]? _uiDiagnosticsPixels;
    private int _uiDiagnosticsWidth, _uiDiagnosticsHeight;
    private Func<Task>? _uiBeforeDiagnosticsRasterAsync;
#endif

    private void ResetMediaDiagnosticsStats()
    {
        _diagnosticsStatsAt = 0;
        _diagnosticsRendered = _diagnosticsDropped = 0;
        _diagnosticsFrameRate = double.NaN;
    }

    private void SampleMediaDiagnosticsStats(PlaybackStats stats, long now)
    {
        double seconds = _diagnosticsStatsAt == 0 ? 0 : Stopwatch.GetElapsedTime(_diagnosticsStatsAt, now).TotalSeconds;
        _diagnosticsFrameRate = seconds > 0 && stats.Rendered >= _diagnosticsRendered && stats.Dropped >= _diagnosticsDropped
            ? (stats.Rendered - _diagnosticsRendered) / seconds : double.NaN;
        _diagnosticsRendered = stats.Rendered;
        _diagnosticsDropped = stats.Dropped;
        _diagnosticsStatsAt = now;
    }

    private void MediaDiagnosticsKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.F12 || !IsMirrorPageSelected || (!_hasVideo && !_hasMusic)) return;
        args.Handled = true;
        if (!args.KeyStatus.WasKeyDown) ToggleMediaDiagnostics();
    }

    private void ToggleMediaDiagnostics()
    {
        if (!IsMirrorPageSelected || (!_hasVideo && !_hasMusic) || _closing || _closed) return;
        _showMediaDiagnostics = !_showMediaDiagnostics;
        UpdateMediaDiagnostics();
    }

    private void UpdateMediaDiagnostics()
    {
        bool video = _showMediaDiagnostics && _hasVideo;
        bool music = _showMediaDiagnostics && _hasMusic && !_hasVideo;
        string text = video || music ? Texts.MediaDiagnosticsText(_mediaState) : "";
        if (video && double.IsFinite(_diagnosticsFrameRate) && _diagnosticsFrameRate is >= 0 and <= 1000)
        {
            if (_mediaState.Diagnostics == default) text = "";
            text += (text.Length > 0 ? "\n" : "") +
                Texts.RenderedMediaDiagnostics(_diagnosticsFrameRate, _diagnosticsDropped);
        }
        VideoDiagnosticsText.Text = video ? text : "";
        MusicDiagnosticsText.Text = music ? text : "";
        VideoDiagnosticsPanel.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        MusicDiagnosticsPanel.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        if (!video && _diagnosticsRasterRequest is not null)
            ResetMediaDiagnosticsRaster();
        UpdateVideoHost();
    }

    private void MediaDiagnosticsSizeChanged(object sender, SizeChangedEventArgs args) => UpdateVideoHost();

    private void PositionMediaDiagnostics(RectInt32 slot, RectInt32 picture, double scale)
    {
        RectInt32 anchor = _playerFullScreen ? slot : picture;
        double width = anchor.Width / scale, height = anchor.Height / scale;
        double inset = _playerFullScreen ? Math.Min(16, Math.Min(width, height) / 8) : 0;
        Point origin = VideoHostSlot.TransformToVisual(null).TransformPoint(default);
        VideoDiagnosticsPanel.Margin = new Thickness(anchor.X / scale - origin.X + inset,
            anchor.Y / scale - origin.Y + inset, 0, 0);
        VideoDiagnosticsPanel.MaxWidth = Math.Min(400, Math.Max(0, width - 2 * inset));
        VideoDiagnosticsPanel.MaxHeight = Math.Max(0, height - 2 * inset);
    }

    private void QueueMediaDiagnosticsRaster(RectInt32 bounds)
    {
        var request = (VideoDiagnosticsText.Text, bounds.Width, bounds.Height, WindowLayout.XamlRoot.RasterizationScale);
        if (_diagnosticsRasterRequest == request)
        {
            // A transient capture failure must not permanently cache a blank
            // paused/static panel. Retry on existing updates, at most once a second.
            if (!_diagnosticsRasterRunning && _diagnosticsRasterApplied != request && _diagnosticsRasterFailedAt != 0 &&
                Stopwatch.GetElapsedTime(_diagnosticsRasterFailedAt).TotalSeconds >= 1)
                _ = RenderMediaDiagnosticsAsync();
            return;
        }
        _diagnosticsRasterRequest = request;
        ++_diagnosticsRasterGeneration;
        if (!_diagnosticsRasterRunning) _ = RenderMediaDiagnosticsAsync();
    }

    private void ResetMediaDiagnosticsRaster()
    {
        _diagnosticsRasterRequest = _diagnosticsRasterApplied = null;
        _diagnosticsRasterFailedAt = 0;
        ++_diagnosticsRasterGeneration;
        _videoHost?.PositionDiagnostics(default, false);
    }

    private async Task RenderMediaDiagnosticsAsync()
    {
        _diagnosticsRasterRunning = true;
        try
        {
            while (!_closed && _hasVideo && IsMirrorPageSelected &&
                _diagnosticsRasterRequest is { } request && _diagnosticsRasterApplied != request)
            {
                int generation = _diagnosticsRasterGeneration;
                var host = _videoHost;
                if (host is null || request.Width <= 0 || request.Height <= 0 ||
                    request.Width > 4096 || request.Height > 4096 || (long)request.Width * request.Height > 4 * 1024 * 1024) return;
#if CLEARCAST_UI_TESTS
                if (_uiBeforeDiagnosticsRasterAsync is { } beforeRaster) await beforeRaster();
#endif
                var bitmap = new RenderTargetBitmap();
                // WinUI already applies the element's rasterization scale.
                await bitmap.RenderAsync(VideoDiagnosticsPanel);
                var buffer = await bitmap.GetPixelsAsync();
                if (_closed || !_hasVideo || !IsMirrorPageSelected || generation != _diagnosticsRasterGeneration ||
                    !ReferenceEquals(host, _videoHost)) continue;
                byte[] pixels = new byte[buffer.Length];
                using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
                if (!host.UpdateDiagnostics(pixels, bitmap.PixelWidth, bitmap.PixelHeight))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to update media information overlay.");
                if (GetMediaDiagnosticsBounds(WindowLayout.XamlRoot.RasterizationScale) is { } bounds)
                    host.PositionDiagnostics(bounds, true);
                _diagnosticsRasterApplied = request;
                _diagnosticsRenderError = "";
                _diagnosticsRasterFailedAt = 0;
#if CLEARCAST_UI_TESTS
                _uiDiagnosticsPixels = pixels;
                _uiDiagnosticsWidth = bitmap.PixelWidth;
                _uiDiagnosticsHeight = bitmap.PixelHeight;
#endif
            }
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or Win32Exception)
        {
            _diagnosticsRenderError = error.ToString();
            _diagnosticsRasterFailedAt = Stopwatch.GetTimestamp();
            Debug.WriteLine(_diagnosticsRenderError);
        }
        finally { _diagnosticsRasterRunning = false; }
    }

    private RectInt32? GetMediaDiagnosticsBounds(double scale)
    {
        if (VideoDiagnosticsPanel.Visibility != Visibility.Visible ||
            VideoDiagnosticsPanel.ActualWidth <= 0 || VideoDiagnosticsPanel.ActualHeight <= 0) return null;
        Rect bounds = VideoDiagnosticsPanel.TransformToVisual(null).TransformBounds(
            new Rect(0, 0, VideoDiagnosticsPanel.ActualWidth, VideoDiagnosticsPanel.ActualHeight));
        return new RectInt32((int)Math.Round(bounds.Left * scale), (int)Math.Round(bounds.Top * scale),
            Math.Max(1, (int)Math.Round(bounds.Width * scale)), Math.Max(1, (int)Math.Round(bounds.Height * scale)));
    }
}
