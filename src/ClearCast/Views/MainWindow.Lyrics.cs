using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ClearCast;

public sealed partial class MainWindow
{
    private const double MusicLyricsSideBySideWidth = 680;
    private static readonly TimeSpan LyricsLookupDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan LyricsLayoutDuration = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan LyricsHideDuration = TimeSpan.FromMilliseconds(160);
    private LyricsClient? _lyricsClient;
    private (string Title, string Artist, string Album)? _lyricsTrack;
    private Lyrics? _lyrics;
    private bool _lyricsPending;
    private long _lyricsLookupDurationMs = -1;
    private bool _lyricsOpen;
    private bool _lyricsClosing;
    private int _lyricsLayoutGeneration;
    private CancellationTokenSource? _lyricsRequest;
    private ((string Title, string Artist, string Album) Track, int Generation, CancellationTokenSource Request)? _pendingLyricsLookup;
    private bool _lyricsLookupRunning;
    private Task _lyricsLookupTask = Task.CompletedTask;
    private int _lyricsGeneration;
    private DispatcherQueueTimer? _lyricsTimer;
    private CompositionEasingFunction? _lyricsLayoutEase;
    private CompositionEasingFunction? _lyricsHideEase;
    private FrameworkElement[]? _musicLayoutElements;
    private bool _lyricsFailed;

    private bool LyricsShown => _lyricsOpen;
    private bool LyricsAvailable => !_lyricsFailed && _lyrics is { Kind: LyricsKind.Synced or LyricsKind.Plain };

    // While the next track is being looked up, an open panel stays open instead of bouncing.
    private bool LyricsWanted => _preferences.ShowLyrics && _hasMusic && !_hasVideo &&
        (LyricsAvailable || _lyricsPending && _lyricsOpen);

    private void MusicLyricsClicked(object sender, RoutedEventArgs args)
    {
        _preferences = _preferences with { ShowLyrics = MusicLyricsButton.IsChecked == true };
        AutoShowLyricsToggle.IsOn = _preferences.ShowLyrics;
        SavePreferences();
        UpdateLyricsPresentation(true);
    }

    /// <summary>Looks up lyrics for each new track and follows visibility and language changes.</summary>
    private void RefreshLyrics()
    {
        var track = (_mediaState.Title, _mediaState.Artist, _mediaState.Album);
        if (_closed || _closing || _lyricsFailed || !_hasMusic || track.Title.Length == 0)
            ResetLyrics();
        else if (_lyricsTrack != track || !_lyricsPending && _lyrics is null &&
            _mediaState.DurationMs > 0 && (_lyricsLookupDurationMs <= 0 ||
                Math.Abs(_mediaState.DurationMs - _lyricsLookupDurationMs) > 2000))
        {
            ResetLyrics();
            _lyricsTrack = track;
            _lyricsPending = true;
            LookUpLyrics(track);
        }
        AutomationProperties.SetName(MusicLyrics, Texts.MusicLyrics);
        UpdateLyricsPresentation(true);
    }

    private void ResetLyrics()
    {
        CancelPendingLyricsWork();
        _lyrics = null;
        _lyricsLookupDurationMs = -1;
        _lyricsTimer?.Stop();
    }

    private void CancelPendingLyricsWork()
    {
        CancelLyricsLookup();
        ++_lyricsGeneration;
        _lyricsTrack = null;
        _lyricsPending = false;
    }

    private void CancelLyricsLookup()
    {
        _lyricsRequest?.Cancel();
        _lyricsRequest = null;
        if (_pendingLyricsLookup is { } pending)
        {
            _pendingLyricsLookup = null;
            pending.Request.Dispose();
        }
    }

    private void LookUpLyrics((string Title, string Artist, string Album) track)
    {
        var request = _lyricsRequest = new CancellationTokenSource();
        _pendingLyricsLookup = (track, _lyricsGeneration, request);
        if (_lyricsLookupRunning) return;
        _lyricsLookupRunning = true;
        _lyricsLookupTask = RunLyricsLookupsAsync();
    }

    private async Task RunLyricsLookupsAsync()
    {
        try
        {
            while (!_closed && !_closing && _pendingLyricsLookup is { } lookup)
            {
                _pendingLyricsLookup = null;
                var request = lookup.Request;
                try
                {
                    // One active lookup and one latest descriptor. Cancellation need not
                    // finish synchronously (for example while DNS is still resolving).
                    await Task.Delay(LyricsLookupDelay, request.Token);
                    if (lookup.Generation != _lyricsGeneration || _closed || _closing) continue;
                    _lyricsClient ??= new LyricsClient();
                    _lyricsLookupDurationMs = _mediaState.DurationMs;
                    var result = await _lyricsClient.FindAsync(new LyricsQuery(lookup.Track.Title,
                        lookup.Track.Artist, lookup.Track.Album, _lyricsLookupDurationMs), request.Token);
                    if (lookup.Generation != _lyricsGeneration || _closed || _closing) continue;
                    _lyrics = result.Lyrics;
                    _lyricsPending = false;
                    // Retry a miss if duration arrived while the request was in flight.
                    RefreshLyrics();
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    // A superseded request cannot disable lyrics for a newer track.
                    if (lookup.Generation == _lyricsGeneration && !_closed && !_closing) DisableLyrics(error);
                }
                finally
                {
                    if (ReferenceEquals(_lyricsRequest, request)) _lyricsRequest = null;
                    request.Dispose();
                }
            }
        }
        finally { _lyricsLookupRunning = false; }
    }

    private void UpdateLyricsButton()
    {
        MusicLyricsButton.IsChecked = _preferences.ShowLyrics;
        MusicLyricsButton.IsEnabled = _hasMusic && LyricsAvailable;
        AutomationProperties.SetName(MusicLyricsButton, Texts.MusicLyrics);
        AutomationProperties.SetHelpText(MusicLyricsButton, LyricsAvailable ? ""
            : _lyricsPending ? Texts.LyricsSearching : Texts.LyricsNotFound);
        ToolTipService.SetToolTip(MusicLyricsButton, _preferences.ShowLyrics ? Texts.MusicHideLyrics : Texts.MusicShowLyrics);
    }

    /// <summary>Opens or closes the lyrics column; the player glides between layouts.</summary>
    private void UpdateLyricsPresentation(bool animate)
    {
        UpdateLyricsButton();
        MusicLyrics.SetSourceText(_lyrics?.Kind == LyricsKind.Plain
            ? Texts.LyricsNotScrollable + "\n" + Texts.LyricsSource : Texts.LyricsSource);
        bool open = LyricsWanted;
        MusicLyrics.AnimationsEnabled = _motionSettings.AnimationsEnabled;
        if (open) MusicLyrics.SetLyrics(_lyrics);
        if (open == _lyricsOpen)
        {
            if (!animate)
            {
                ++_lyricsLayoutGeneration;
                _lyricsClosing = false;
                ResetMusicLayoutAnimation();
                if (!open) MusicLyrics.SetLyrics(null);
            }
            UpdateMusicLayout();
            UpdateLyricsPosition();
            return;
        }
        animate = animate && _motionSettings.AnimationsEnabled && MusicPanel.Visibility == Visibility.Visible &&
            MusicPanel.ActualWidth > 0 && AppWindow.IsVisible && IsMirrorPageSelected;
        int generation = ++_lyricsLayoutGeneration;
        Rect content = LayoutBounds(MusicContent), lyrics = LayoutBounds(MusicLyrics);
        var elements = MusicLayoutElements;
        var previous = elements.Select(LayoutBounds).ToArray();
        bool wasVisible = MusicLyrics.Visibility == Visibility.Visible;
        bool sideBySide = MusicPanel.ViewportWidth >= MusicLyricsSideBySideWidth;
        _lyricsOpen = open;
        _lyricsClosing = !open && animate && lyrics.Width > 0;
        var lyricsVisual = ElementCompositionPreview.GetElementVisual(MusicLyrics);
        UpdateMusicLayout();
        if (!animate)
        {
            ResetMusicLayoutAnimation();
            if (!open) MusicLyrics.SetLyrics(null);
            UpdateLyricsPosition();
            return;
        }
        MusicLayout.UpdateLayout();
        UpdateMusicLyricsBounds();
        MusicLayout.UpdateLayout();
        _lyricsLayoutEase ??= lyricsVisual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.1f, 1f));
        _lyricsHideEase ??= lyricsVisual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
        ElementCompositionPreview.SetIsTranslationEnabled(MusicLyrics, true);
        var compositor = lyricsVisual.Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        for (int index = 0; index < elements.Length; ++index)
            AnimateMusicElement(elements[index], previous[index], LayoutBounds(elements[index]));
        Rect targetContent = LayoutBounds(MusicContent), targetLyrics = LayoutBounds(MusicLyrics);
        // Opening follows the player edge in either layout; closing holds its position.
        var offset = sideBySide
            ? new Vector3((float)(targetContent.Right - content.Right), 0, 0)
            : new Vector3(0, (float)(targetContent.Bottom - content.Bottom), 0);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        var slide = compositor.CreateVector3KeyFrameAnimation();
        fade.Duration = slide.Duration = LyricsLayoutDuration;
        if (open)
        {
            if (wasVisible)
            {
                fade.InsertExpressionKeyFrame(0, "this.StartingValue");
                slide.SetVector3Parameter("shift", new Vector3((float)(lyrics.X - targetLyrics.X), (float)(lyrics.Y - targetLyrics.Y), 0));
                slide.InsertExpressionKeyFrame(0, "this.StartingValue + shift");
            }
            else
            {
                fade.InsertKeyFrame(0, 0);
                slide.InsertKeyFrame(0, -offset);
            }
            fade.InsertKeyFrame(1, 1, _lyricsLayoutEase);
            slide.InsertKeyFrame(1, Vector3.Zero, _lyricsLayoutEase);
        }
        else
        {
            fade.Duration = LyricsHideDuration;
            fade.InsertExpressionKeyFrame(0, "this.StartingValue");
            fade.InsertKeyFrame(1, 0, _lyricsHideEase);
            slide.InsertExpressionKeyFrame(0, "this.StartingValue");
            slide.InsertExpressionKeyFrame(1, "this.StartingValue");
        }
        lyricsVisual.StartAnimation("Opacity", fade);
        lyricsVisual.StartAnimation("Translation", slide);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (generation != _lyricsLayoutGeneration || _closed) return;
            try
            {
                if (!open)
                {
                    MusicLyrics.Visibility = Visibility.Collapsed;
                    _lyricsClosing = false;
                    MusicLyrics.SetLyrics(null);
                    UpdateMusicLayout();
                }
                ResetMusicLayoutAnimation();
            }
            catch (Exception error) { DisableLyrics(error); }
        };
        UpdateLyricsPosition();
    }

    private FrameworkElement[] MusicLayoutElements => _musicLayoutElements ??=
        [MusicArtworkFrame, MusicDiagnosticsPanel, MusicTitle, MusicArtist, MusicAlbum,
            MusicProgress, MusicSeekSlider, MusicElapsed, MusicDuration, MusicPreviousButton,
            MusicPlayPauseButton, MusicNextButton, MusicFullScreenButton, MusicDisconnectButton,
            MusicLyricsButton, MusicControlHint];

    private void MusicLyricsSlotLayoutUpdated(object? sender, object args) => UpdateMusicLyricsBounds();

    private void UpdateMusicLyricsBounds()
    {
        if (_closed || _closing || !_lyricsOpen) return;
        // Keep a stable visual parent: reparenting interrupts the lyric surfaces.
        // The slot reserves space only while open; the Canvas never measures lyrics.
        Rect bounds = LayoutBounds(MusicLyricsSlot);
        if (bounds.Width <= 0) return;
        if (MusicLyrics.Width != bounds.Width) MusicLyrics.Width = bounds.Width;
        if (MusicLyrics.Height != bounds.Height) MusicLyrics.Height = bounds.Height;
        if (Canvas.GetLeft(MusicLyrics) != bounds.X) Canvas.SetLeft(MusicLyrics, bounds.X);
        if (Canvas.GetTop(MusicLyrics) != bounds.Y) Canvas.SetTop(MusicLyrics, bounds.Y);
    }

    private void ResetMusicLayoutAnimation()
    {
        foreach (var element in MusicLayoutElements.Append(MusicLyrics))
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Translation");
            visual.StopAnimation("Scale");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Scale = Vector3.One;
        }
        var lyricsVisual = ElementCompositionPreview.GetElementVisual(MusicLyrics);
        lyricsVisual.StopAnimation("Opacity");
        lyricsVisual.Opacity = 1;
    }

    /// <summary>Preserves the displayed position and artwork size across a layout change.</summary>
    private void AnimateMusicElement(FrameworkElement element, Rect before, Rect after)
    {
        if (before.Width <= 0 || after.Width <= 0) return;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        bool resizeElement = ReferenceEquals(element, MusicArtworkFrame) || ReferenceEquals(element, MusicProgress) ||
            ReferenceEquals(element, MusicSeekSlider);
        bool centeredText = element is TextBlock { TextAlignment: TextAlignment.Center };
        visual.CenterPoint = Vector3.Zero;
        var move = compositor.CreateVector3KeyFrameAnimation();
        move.SetVector3Parameter("shift", new Vector3((float)(before.X - after.X + (centeredText ? (before.Width - after.Width) / 2 : 0)),
            (float)(before.Y - after.Y), 0));
        // StartingValue is sampled by the compositor, including an interrupted transition.
        move.InsertExpressionKeyFrame(0, "this.StartingValue + shift");
        move.InsertKeyFrame(1, Vector3.Zero, _lyricsLayoutEase);
        move.Duration = LyricsLayoutDuration;
        visual.StartAnimation("Translation", move);
        if (resizeElement && before.Height > 0 && after.Height > 0)
        {
            var resize = compositor.CreateVector3KeyFrameAnimation();
            resize.SetVector3Parameter("ratio", new Vector3((float)(before.Width / after.Width), (float)(before.Height / after.Height), 1));
            resize.InsertExpressionKeyFrame(0, "this.StartingValue * ratio");
            resize.InsertKeyFrame(1, Vector3.One, _lyricsLayoutEase);
            resize.Duration = LyricsLayoutDuration;
            visual.StartAnimation("Scale", resize);
        }
    }

    private Rect LayoutBounds(FrameworkElement element)
    {
        // TransformToVisual includes an in-flight Composition transform. Use only
        // arranged offsets here; StartingValue already preserves that transform.
        Vector3 offset = Vector3.Zero;
        for (DependencyObject? parent = element; parent is not null && !ReferenceEquals(parent, MusicLayout); parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not UIElement child) continue;
            if (child.Visibility == Visibility.Collapsed) return default;
            offset += child.ActualOffset;
        }
        return element.ActualWidth > 0
            ? new Rect(offset.X, offset.Y,
                element is TextBlock { TextAlignment: TextAlignment.Center }
                    ? Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(element).Width : element.ActualWidth,
                element.ActualHeight) : default;
    }

    /// <summary>Highlights the current line and wakes exactly at the next line change while playing.</summary>
    private void UpdateLyricsPosition()
    {
        _lyricsTimer?.Stop();
        if (!_lyricsOpen || !_hasMusic || _hasVideo || _lyrics is not { Kind: LyricsKind.Synced } lyrics) return;
        long position = _mediaState.PositionAt(_mediaSnapshotAt == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_mediaSnapshotAt));
        MusicLyrics.SetPosition(position);
        long next = lyrics.NextChangeMs(position);
        if (!_mediaState.Playing || next < 0 || _closed) return;
        if (_lyricsTimer is null)
        {
            _lyricsTimer = DispatcherQueue.CreateTimer();
            _lyricsTimer.IsRepeating = false;
            _lyricsTimer.Tick += (_, _) =>
            {
                try { UpdateLyricsPosition(); }
                catch (Exception error) when (!_closed) { DisableLyrics(error); }
            };
        }
        _lyricsTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(next - position, 16, 1000));
        _lyricsTimer.Start();
    }

    /// <summary>Records an unexpected lyrics failure and keeps playback running without lyrics.</summary>
    private void DisableLyrics(Exception error)
    {
        App.WriteUiError("Lyrics disabled for this session: " + error);
        _lyricsFailed = true;
        try
        {
            ResetLyrics();
            _lyricsClosing = false;
            UpdateLyricsPresentation(false);
        }
        catch (Exception cleanup) when (cleanup is COMException or ArgumentException or InvalidOperationException)
        {
            MusicLyrics.Visibility = Visibility.Collapsed;
        }
    }

    private void StopLyrics()
    {
        _lyricsTimer?.Stop();
        CancelLyricsLookup();
        ++_lyricsGeneration;
        ++_lyricsLayoutGeneration;
        _lyricsClient?.Dispose();
        _lyricsClient = null;
    }
}
