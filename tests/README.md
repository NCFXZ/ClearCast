# Managed state and UI regression tests

On Windows with .NET 10, run the managed state tests from the repository root:

```powershell
dotnet run --project tests/managed/ManagedStateTests.csproj -c Release -- --output .runtime/qa-managed-results.json
```

The tests use a newly created temporary directory, never the application's saved state, and remove their fixtures on completion. They cover saved-key parsing, bounded reads of oversized records, atomic single-device removal and server-identity retirement, locked-file rollback, settings recovery and localization. They do not start an AirPlay receiver.

To build the in-app UI integration harness, use `-p:EnableUiTests=true` and a separate publish/intermediate directory. Create an empty `clearcast-ui-test-state.marker` in a disposable `CLEARCAST_HOME`, set `CLEARCAST_UI_TEST_RUN=1`, and launch that test executable after other AirPlay receivers have stopped. It writes `ui-test-output/results.json` and controlled-theme PNG files, then exits. Normal builds exclude the UI test source entirely.

UI captures use `RenderTargetBitmap` with an opaque theme background; they exclude the DWM/Mica backdrop and native video surfaces. Synthetic player tests exercise the application's own HWND geometry, clipping, full-screen presenter and Escape handler. They do not establish decoded video quality, audio synchronization, frame rotation or orientation changes on physical Apple devices.

## Software decoder smoke

With 64-bit Python and FFmpeg containing the `libx264` and `libx265` encoders:

```powershell
python tests/media_decode_smoke.py --runtime .runtime/receiver --ffmpeg C:/path/to/ffmpeg.exe --output .runtime/qa-media-results.json
```

This loads only the supplied runtime's core/typefind, video parser and libav plugins. It generates twelve 320×180 frames for each codec, checks parser and software-decoder output dimensions, raw buffer size, changing pixel hashes and EOS, then deletes its temporary clips and registry. The JSON records actual component versions and hashes. It uses `fakesink`: no receiver ports, hardware decoder, device video sink or audio output are opened. This smoke test does not replace real AirPlay or hardware/media-quality acceptance.
