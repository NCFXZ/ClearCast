# Managed state and UI regression tests

On Windows with .NET 10, run the managed state tests from the repository root:

```powershell
dotnet run --project tests/managed/ManagedStateTests.csproj -c Release -- --output .runtime/qa-managed-results.json
```

The tests use a newly created temporary directory, never the application's saved state, and remove their fixtures on completion. They cover saved-key parsing, bounded reads of oversized records, atomic single-device removal and server-identity retirement, locked-file rollback, settings recovery and localization, plus media snapshots, progress bounds, session identity, and backward-compatible automatic full-screen settings. `ArtworkFlowTests.cs` checks bounded cover-texture generation, spatial structure, opaque blending and the loop seam; `MusicMotionTests.cs` checks density-driven pace, time-based smoothing, invalid samples and reset behavior. They do not start an AirPlay receiver.

The in-app UI harness sources under `tests/ui/MainWindow.*UiTests.cs` are linked into the app only when `EnableUiTests=true`. Media checks use synthetic state, exercise actual image decoding and window paths, and do not send real playback commands. `MainWindow.ConnectionUiTests.cs` checks authenticated idle connection text independently of metadata revisions, pause and media completion, disconnect and stop, language and tab changes, and startup/PIN/error precedence. The optional strict-boolean `connected` snapshot field is checked by the managed parser tests; actual verified-socket lifecycle is covered by the native protocol tests. Build the UI harness from the repository root with separate output and intermediate directories:

```powershell
dotnet publish src/ClearCast/ClearCast.csproj -c Release -r win-x64 `
  -p:EnableUiTests=true -p:BaseIntermediateOutputPath=../../.runtime/obj-ui/ `
  -p:BaseOutputPath=../../.runtime/bin-ui/ -o .runtime/app-ui
```

Create an empty `clearcast-ui-test-state.marker` in a disposable `CLEARCAST_HOME`, set `CLEARCAST_UI_TEST_RUN=1`, and launch that test executable after other AirPlay receivers have stopped. It writes `ui-test-output/results.json` and controlled-theme PNG files, then exits. Normal builds exclude the UI test source entirely.

For a startup/close lifecycle smoke test, run:

```powershell
python tests/ui/ui_lifecycle_smoke.py --app .runtime/app-ui/ClearCast.exe --output .runtime/qa-ui-lifecycle/results.json
```

Ordinary XAML captures use `RenderTargetBitmap` with an opaque theme background; they exclude the DWM/Mica backdrop, hand-in Composition visuals, and native video surfaces. Synthetic player tests exercise the application's own HWND geometry, clipping, full-screen presenter and Escape handler. They do not establish decoded video quality, audio synchronization, frame rotation or orientation changes on physical devices.

## Music compositor capture

The same UI harness automatically invokes `MainWindow.CompositorUiTests.cs` during the 1120-pixel music layouts in the controlled light and dark themes, and on Settings and About in dark English mode; no separate launch flag is needed. It uses `PrintWindow(PW_RENDERFULLCONTENT=2)` only on the test process's own HWND, verifies ownership before each capture, and compares two frames two seconds apart while page text and the window caption remain fixed. It checks 32 warped and blurred artwork textures and 33 layers with at most two visible at a time. The flow retains its original 34 channels: 33 opacity expressions plus one shared phase clock, controlled by one rate controller. Two persistent background scale/offset expressions bring normal motion to 36 channels; a finite strong-beat scalar animation temporarily brings it to 37. The harness records color-change measurements outside the current page's static content as well as over the whole window.

The media UI checks also read the real controller's playback rate and progress, exercise smooth density changes without restarting animations, and verify pause, missing artwork, stale samples and session resets. System motion, visibility and high-contrast gates remain authoritative. The texture construction, source attribution and PCM boundaries are documented in [Music background](MUSIC_BACKGROUND.md); rendered-frame evidence and pure state assertions establish different parts of the behavior.

`MainWindow.BeatUiTests.cs` adds strong-beat checks through the real host and Composition control in both light and dark themes. With the flow clock held still, it captures the baseline, attack, rebound, and settled background while checking that foreground artwork pixels and layout bounds remain unchanged. The artwork ROI uses the actual image rectangle with zero extra padding and a DPI-aware inner inset. It checks one impulse per accepted event, weak/stale/duplicate/older-epoch rejection, a parsed invalid-sample FLUSH generation, motion-policy resets, and a new beat arriving during the previous tail without flipping the offset direction. The 360 ms impulse uses a precomputed sampled damping curve, a 1.05 baseline, a maximum scale of 1.17, an approximately 1.0336 minimum at full rebound, and an offset capped at 0.8% of the corresponding viewport dimension or 10 logical pixels. Settled captures wait for the configured duration plus a margin. API fallback can disable the extra beat channels while preserving the original flow.

Pure pulse tests cover normalized peaks, decaying rebound, soft/strong strength mapping, timing gates, and viewport coverage across the sampled curve. Parser tests check age attenuation; host/control checks exercise a new beat during the prior tail. Artwork tests cover the stronger light-theme color treatment and its minimum-brightness guard; dark-theme rendering retains its previous processing. Test results and rendered comparisons belong in the ignored QA directory, not this guide.

The same test entry exercises the real isolated receiver's 50 ms activity-only poller: it temporarily stops the 150 ms metadata timer, waits for asynchronous artwork setup, observes repeated native reads without applying idle metadata to the synthetic music page, and checks timer stop/resume. Fast polling is enabled only while the music background can animate; metadata and progress retain their normal cadence. These checks prove host routing and visual behavior. Synthetic host events do not prove PCM beat detection or acoustic synchronization; native PCM and actual codec fixtures cover the signal path separately.

When capture returns visible content, images use the `*-music-window-1.png` and `*-music-window-2.png` names under `ui-test-output/`. The `layouts` entries in `results.json` identify the capture method, animation state, geometry, changed-pixel measurements, and capture status. `PrintWindow` can omit or freeze WinUI Composition visuals; blank or insufficiently changing output is recorded as `unsupported` with a reason, separately from the XAML, palette, and animation-lifecycle assertions. Do not treat that status as proof of a playback failure or as captured compositor-motion evidence. System animation preferences and high-contrast behavior are exercised separately.

## Original music screenshots

For release screenshot preparation, set `CLEARCAST_UI_STORE_ARTWORK` to the absolute path of a bounded original PNG before running the same isolated UI harness. This optional path is implemented only in `MainWindow.StoreUiTests.cs`, which is excluded from normal builds. Keep its source artwork, generator and provenance in the ignored QA directory; do not use private or third-party album covers.

The harness temporarily suspends metadata polling while its isolated receiver remains alive, applies fictional track information and a generic `ClearCast PC` receiver name, and captures the application's own window through the same compositor capture path. It captures music and the waiting page at 1920 × 1080 and Settings at 2560 × 1440 so the settings cards fit. Chinese screenshots use `zh-CN`; other Store languages can use the English `en-US` images. Light and dark captures are saved without image edits to `ui-test-output/store-music/`, with source and screenshot hashes in `provenance.json`. Set `CLEARCAST_UI_STORE_ONLY=1` for the capture workflow alone. Unsupported sender controls remain hidden. The fixtures do not send remote playback commands or establish compatibility with a physical music player. Preferences, media state, window placement, receiver display name and polling are restored afterward.

`direct_video_app_test.py` can also use `--source-video` with an owned 1280 × 720 demonstration MP4, `--language zh-CN|en-US`, `--theme system|light|dark`, and `--store-screenshots` with `--desktop-controls`. The existing authenticated playback checks save unedited owned-window video captures, with the same process-path guard used for control tests. Capture runs must be serialized with other native window or Sandbox interaction tests to avoid foreground contention; the input guard fails rather than typing into another window.

## Software decoder smoke

With 64-bit Python and FFmpeg containing the `libx264` and `libx265` encoders:

```powershell
python tests/media/media_decode_smoke.py --runtime .runtime/receiver --ffmpeg C:/path/to/ffmpeg.exe --output .runtime/qa-media-results.json
```

This loads only the supplied runtime's core/typefind, video parser and libav plugins. It generates twelve 320×180 frames for each codec, checks parser and software-decoder output dimensions, raw buffer size, changing pixel hashes and EOS, then deletes its temporary clips and registry. The JSON records actual component versions and hashes. It uses `fakesink`: no receiver ports, hardware decoder, device video sink or audio output are opened. This smoke test does not replace real AirPlay or hardware/media-quality acceptance.

See [native receiver tests](testing-native.md) and [packaging checks](packaging.md) for the other test groups.
