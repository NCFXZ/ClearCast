# Store capture workflow

Store images show the actual application filling the entire native display, with original demonstration content. Capture only that task-owned fullscreen HWND, never the user's desktop. The capture helpers use DWM visible-frame bounds and preserve those pixels without resampling or painting; `GetWindowRect` also includes invisible resize gutters and must not be used directly as the saved frame.

Create the original fixtures in an ignored directory:

```powershell
& tests/media/New-StoreDemoArtwork.ps1 -OutputDirectory .runtime/store-media
python tests/media/create_store_demo_video.py --output-directory .runtime/store-media
```

The artwork and silent geometric video have separate provenance files. They use no reference album art, private recording or remote media. Track/device names supplied by the Store harness are fictional.

Publish both the normal application and `EnableUiTests=true` test application. For waiting/music/settings, run the test application with an independently marked `CLEARCAST_HOME`, `CLEARCAST_UI_TEST_RUN=1`, `CLEARCAST_UI_STORE_ONLY=1` and an absolute `CLEARCAST_UI_STORE_ARTWORK` pointing at the generated cover. The Store-only harness uses a fullscreen presenter and produces 12 native-display images in `ui-test-output/store-music`; normal production presentation and interaction are unchanged.

Generate two fullscreen video captures once for each `zh-CN` and `en-US` locale using `tests/ui/direct_video_app_test.py --case both --media mp4 --desktop-controls --store-capture-only --store-screenshots <video-directory> --source-video <generated-demo.mp4> --language <locale> --app <normal-app.exe> --report <ignored-report.json>`. Set `CLEARCAST_PROBE_RUNTIME` to that candidate's absolute `receiver` directory before starting each fresh Python host. `video-controls` shows the real paused fullscreen player controls; `video-fullscreen` captures active fullscreen playback without focusing the controls. Both are checked against the monitor bounds, retain its native aspect ratio and may naturally letterbox the video source.

All receiver/application runs are serialized and use independent state. After capture, validate and copy the exact source PNG bytes:

```powershell
python scripts/packaging/Prepare-StoreScreenshots.py --version 1.2.2.0 `
  --input <store-music-directory> --input <video-directory> `
  --output shared/docs/screenshots/1.2.2.0 `
  --report .runtime/store-screenshots.json
```

The tool requires all 16 named images (eight per locale) at the same native fullscreen size, checks bounded dimensions/file size, rejects multi-pixel black capture gutters outside video views, and records PNG/RGB hashes and edge measurements. It does not crop or edit PNGs to hide an invalid capture. Review representative light/dark, music, settings and video images before upload. Compare decoded pixels with the uploaded Store images, since the Store may re-encode PNG bytes.
