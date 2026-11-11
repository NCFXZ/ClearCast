using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace ClearCast;

public sealed partial class MainWindow
{
    private const double MusicLyricsSideBySideWidth = 680;
    private static readonly TimeSpan LyricsLookupDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan LyricsLayoutDuration = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan LyricsHideDuration = TimeSpan.FromMilliseconds(240);
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
    private bool _lyricsFailed;

    private bool LyricsShown => _lyricsOpen;
    private bool LyricsAvailable => !_lyricsFailed && _lyrics is { Kind: LyricsKind.Synced or LyricsKind.Plain };

    // While the next track is being looked up, an open panel stays open instead of bouncing.
    private bool LyricsWanted => _preferences.ShowLyrics && _hasMusic && !_hasVideo &&
        (LyricsAvailable || _lyricsPending && _lyricsOpen);

    private void MusicLyricsClicked(object sender, RoutedEventArgs args)
    {
        _preferences = _preferences with { ShowLyrics = MusicLyricsButton.IsChecked == true };
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
        CancelLyricsLookup();
        ++_lyricsGeneration;
        _lyricsTrack = null;
        _lyrics = null;
        _lyricsPending = false;
        _lyricsLookupDurationMs = -1;
        _lyricsTimer?.Stop();
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
            UpdateMusicLayout();
            UpdateLyricsPosition();
            return;
        }
        animate = animate && _motionSettings.AnimationsEnabled && MusicPanel.Visibility == Visibility.Visible &&
            MusicPanel.ActualWidth > 0 && AppWindow.IsVisible;
        int generation = ++_lyricsLayoutGeneration;
        Rect content = LayoutBounds(MusicContent), lyrics = LayoutBounds(MusicLyrics);
        _lyricsOpen = open;
        _lyricsClosing = !open && animate && lyrics.Width > 0;
        UpdateMusicLayout();
        var lyricsVisual = ElementCompositionPreview.GetElementVisual(MusicLyrics);
        if (_lyricsClosing)
        {
            // Keep the closing lyrics where they were while they fade out.
            Grid.SetRow(MusicLyrics, 0);
            Grid.SetColumn(MusicLyrics, 0);
            MusicLyrics.HorizontalAlignment = HorizontalAlignment.Left;
            MusicLyrics.Margin = new Thickness(lyrics.X, lyrics.Y, 0, 0);
            MusicLyrics.Width = lyrics.Width;
            MusicLyrics.Height = lyrics.Height;
        }
        if (!animate)
        {
            lyricsVisual.StopAnimation("Opacity");
            lyricsVisual.Opacity = 1;
            if (!open) MusicLyrics.SetLyrics(null);
            UpdateLyricsPosition();
            return;
        }
        MusicLayout.UpdateLayout();
        AnimateMusicContent(content, LayoutBounds(MusicContent));
        _lyricsLayoutEase ??= lyricsVisual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.1f, 1f));
        _lyricsHideEase ??= lyricsVisual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
        bool sideBySide = Grid.GetColumn(MusicLyrics) == 1 || lyrics.X > content.X + content.Width - 1;
        var offset = sideBySide ? new Vector3(56, 0, 0) : new Vector3(0, 32, 0);
        ElementCompositionPreview.SetIsTranslationEnabled(MusicLyrics, true);
        var compositor = lyricsVisual.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        var slide = compositor.CreateVector3KeyFrameAnimation();
        if (open)
        {
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1, _lyricsLayoutEase);
            slide.InsertKeyFrame(0, offset);
            slide.InsertKeyFrame(1, Vector3.Zero, _lyricsLayoutEase);
            fade.Duration = slide.Duration = LyricsLayoutDuration;
            fade.DelayTime = slide.DelayTime = TimeSpan.FromMilliseconds(80);
            fade.DelayBehavior = slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            lyricsVisual.StartAnimation("Opacity", fade);
            lyricsVisual.StartAnimation("Translation", slide);
            UpdateLyricsPosition();
            return;
        }
        fade.InsertKeyFrame(1, 0, _lyricsHideEase);
        slide.InsertKeyFrame(1, offset * 0.6f, _lyricsHideEase);
        fade.Duration = slide.Duration = LyricsHideDuration;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        lyricsVisual.StartAnimation("Opacity", fade);
        lyricsVisual.StartAnimation("Translation", slide);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (generation != _lyricsLayoutGeneration || _closed) return;
            try
            {
                _lyricsClosing = false;
                lyricsVisual.Opacity = 1;
                lyricsVisual.Properties.InsertVector3("Translation", Vector3.Zero);
                MusicLyrics.SetLyrics(null);
                UpdateMusicLayout();
            }
            catch (Exception error) { DisableLyrics(error); }
        };
    }

    /// <summary>Animates the player from its previous layout box into the current one.</summary>
    private void AnimateMusicContent(Rect before, Rect after)
    {
        if (before.Width <= 0 || after.Width <= 0) return;
        ElementCompositionPreview.SetIsTranslationEnabled(MusicContent, true);
        var visual = ElementCompositionPreview.GetElementVisual(MusicContent);
        var compositor = visual.Compositor;
        _lyricsLayoutEase ??= compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.1f, 1f));
        float scale = (float)Math.Clamp(before.Width / after.Width, 0.5, 2);
        visual.CenterPoint = new Vector3((float)after.Width / 2, (float)after.Height / 2, 0);
        var move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, new Vector3((float)(before.X + before.Width / 2 - after.X - after.Width / 2),
            (float)(before.Y + before.Height / 2 - after.Y - after.Height / 2), 0));
        move.InsertKeyFrame(1, Vector3.Zero, _lyricsLayoutEase);
        move.Duration = LyricsLayoutDuration;
        var resize = compositor.CreateVector3KeyFrameAnimation();
        resize.InsertKeyFrame(0, new Vector3(scale, scale, 1));
        resize.InsertKeyFrame(1, Vector3.One, _lyricsLayoutEase);
        resize.Duration = LyricsLayoutDuration;
        visual.StartAnimation("Translation", move);
        visual.StartAnimation("Scale", resize);
    }

    private Rect LayoutBounds(FrameworkElement element) =>
        element.Visibility == Visibility.Visible && element.ActualWidth > 0
            ? element.TransformToVisual(MusicLayout).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight))
            : default;

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
        _lyricsClient?.Dispose();
        _lyricsClient = null;
    }
}
