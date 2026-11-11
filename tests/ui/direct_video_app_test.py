"""Play authenticated synthetic video through the actual WinUI application.

Uses disposable pairing/preferences, not the user's application state. Run
serially with all receiver tests and app instances.
SPDX-License-Identifier: GPL-3.0-or-later
"""
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/native"))
from receiver_test_support import Receiver, connect, verify_pairing
from direct_video_protocol_test import http_request, start_media_server
from mdns_discovery_test import query as discovery_query
from media_fixture_support import media_fixture_address

import argparse
import ctypes as c
from ctypes import wintypes as w
import json
import math
import os
import plistlib
import re
import shutil
import socket
import subprocess
import tempfile
import time
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--case", choices=("both", "windowed", "fullscreen"), default="both")
    parser.add_argument("--media", choices=("mp4", "hls", "live"), default="mp4")
    parser.add_argument("--desktop-controls", action="store_true")
    parser.add_argument("--no-global-input", action="store_true",
        help="With --desktop-controls, use owned-window UI Automation and messages; skip global keyboard and mouse input.")
    parser.add_argument("--source-video", type=Path, help="Owned 1280x720 synthetic demonstration video instead of the generated test pattern.")
    parser.add_argument("--language", choices=("zh-CN", "en-US"), default="zh-CN")
    parser.add_argument("--theme", choices=("system", "light", "dark"), default="system")
    parser.add_argument("--store-screenshots", type=Path, help="Save native-display fullscreen captures of the owned app during playback.")
    parser.add_argument("--store-capture-only", action="store_true", help="Capture real playback without sending foreground keyboard or mouse input.")
    args = parser.parse_args()
    if args.store_screenshots and not args.desktop_controls:
        parser.error("--store-screenshots requires --desktop-controls")
    if args.store_capture_only and not args.store_screenshots:
        parser.error("--store-capture-only requires --store-screenshots")
    if args.no_global_input and not args.desktop_controls:
        parser.error("--no-global-input requires --desktop-controls")
    app = args.app.resolve(strict=True)
    report_path = args.report.resolve()
    report_path.parent.mkdir(parents=True, exist_ok=True)
    control_probe = report_path.parent / "VideoControlProbe.exe"
    if args.desktop_controls:
        windows = Path(os.environ["SystemRoot"])
        references = [next((windows / "Microsoft.NET/assembly/GAC_MSIL" / name).glob("*/" + name + ".dll"))
            for name in ("UIAutomationClient", "UIAutomationTypes", "WindowsBase")]
        compiler = windows / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
        compilation = subprocess.run([str(compiler), "/nologo", "/out:" + str(control_probe)] +
            ["/r:" + str(reference) for reference in references] + [str(ROOT / "tests/ui/VideoControlProbe.cs")],
            capture_output=True, timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
        if compilation.returncode:
            raise RuntimeError("Desktop probe compilation failed: " + compilation.stdout.decode(errors="replace"))
    for port in (47000, 47001, 47002):
        with socket.socket() as probe:
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            probe.bind(("0.0.0.0", port))

    user32 = c.WinDLL("user32", use_last_error=True)
    callback = c.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    user32.EnumWindows.argtypes = [callback, w.LPARAM]
    user32.GetWindowThreadProcessId.argtypes = [w.HWND, c.POINTER(w.DWORD)]
    user32.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, c.c_int]
    user32.PostMessageW.argtypes = [w.HWND, w.UINT, w.WPARAM, w.LPARAM]
    user32.GetWindowRect.argtypes = [w.HWND, c.POINTER(w.RECT)]

    def window(pid):
        found = []

        @callback
        def collect(hwnd, _):
            owner = w.DWORD()
            user32.GetWindowThreadProcessId(hwnd, c.byref(owner))
            if owner.value == pid:
                title = c.create_unicode_buffer(256)
                user32.GetWindowTextW(hwnd, title, len(title))
                if title.value == "ClearCast":
                    found.append(hwnd)
            return True

        user32.EnumWindows(collect, 0)
        return found[0] if found else None

    def bounds(pid):
        hwnd = window(pid)
        rect = w.RECT()
        if hwnd and user32.GetWindowRect(hwnd, c.byref(rect)):
            return [rect.left, rect.top, rect.right, rect.bottom]
        return None

    work = Path(tempfile.mkdtemp(prefix="winui-video-", dir=report_path.parent))
    media = work / "media"
    media.mkdir()
    tools = ROOT / ".runtime/msys64/ucrt64/bin"
    environment = os.environ.copy()
    environment["PATH"] = str(tools) + os.pathsep + environment.get("PATH", "")
    if args.source_video:
        shutil.copyfile(args.source_video.resolve(strict=True), media / "video.mp4")
    else:
        subprocess.run([str(tools / "ffmpeg.exe"), "-v", "error", "-f", "lavfi", "-i",
            "testsrc2=size=1280x720:rate=15", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
            "-t", "60", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "15",
            "-c:a", "aac", "-movflags", "+faststart", str(media / "video.mp4")],
            env=environment, capture_output=True, check=True, timeout=60)
    if args.media in ("hls", "live"):
        subprocess.run([str(tools / "ffmpeg.exe"), "-v", "error", "-i", str(media / "video.mp4"),
            "-c", "copy", "-hls_time", "2", "-hls_list_size", "0", "-hls_segment_filename",
            str(media / "segment-%03d.ts"), str(media / "stream.m3u8")],
            env=environment, capture_output=True, check=True, timeout=30)
        if args.media == "live":
            playlist = media / "stream.m3u8"
            playlist.write_text(playlist.read_text(encoding="utf-8").replace("#EXT-X-ENDLIST", ""), encoding="utf-8")

    server, worker = start_media_server(media)
    media_host = media_fixture_address()
    report = {"passed": False, "app": str(app), "cases": [],
        "scope": "Actual WinUI app with authenticated synthetic HTTP H264/AAC playback, independent pairing and preferences; no physical-device acceptance."}
    failure = None
    try:
        modes = [False, True] if args.case == "both" else [args.case == "fullscreen"]
        for fullscreen in modes:
            root = work / ("fullscreen" if fullscreen else "windowed")
            state = root / ".clearcast-state"
            state.mkdir(parents=True)
            (state / "preferences.json").write_text(json.dumps({"Decoder": 0, "OriginalSize": False,
                "Theme": ("system", "light", "dark").index(args.theme),
                "Language": int(args.language == "en-US"), "AutoFullScreen": fullscreen}), encoding="utf-8")
            seed = Receiver(runtime=app.parent / "receiver", state=state)
            try:
                seed.start()
                with connect() as pairing:
                    private, public = seed.pair(pairing)
            finally:
                seed.close()
            app_env = os.environ.copy()
            app_env["CLEARCAST_HOME"] = str(root)
            app_env.pop("CLEARCAST_UI_TEST_RUN", None)
            startup = subprocess.STARTUPINFO()
            startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            startup.wShowWindow = 0
            process = subprocess.Popen([str(app)], cwd=app.parent, env=app_env,
                startupinfo=startup, creationflags=subprocess.CREATE_NO_WINDOW)
            result = {"fullscreen": fullscreen, "media": args.media, "passed": False, "state": str(root), "checks": [], "skipped": []}
            report["cases"].append(result)

            def alive():
                if process.poll() is not None:
                    raise AssertionError("Actual app exited during playback: " + str(process.returncode))

            def desktop(action, output=None, extra=None, placement=None):
                command = [str(control_probe), str(process.pid), str(app), action]
                if output is not None:
                    command.append(str(output))
                if extra is not None:
                    command.append(str(extra))
                if placement is not None:
                    command.append(str(placement))
                completed = subprocess.run(command, capture_output=True, text=True,
                    errors="replace", timeout=25 if action == "diagnostics" else 15, creationflags=subprocess.CREATE_NO_WINDOW)
                if completed.returncode:
                    raise AssertionError("Desktop control failed: " + completed.stderr[-2000:])
                return json.loads(completed.stdout)

            def receiver_address():
                log = (state / "receiver.log").read_text(encoding="utf-8")
                addresses = re.findall(r"ready address=([0-9.]+)", log)
                if not addresses:
                    raise OSError("Actual app has not selected a receiver interface")
                return addresses[-1]

            def discover():
                interface = receiver_address()
                for service in (b"_airplay._tcp.local", b"_raop._tcp.local"):
                    assert discovery_query(interface, service), "Actual app stopped responding to discovery"

            try:
                deadline = time.monotonic() + 20
                while time.monotonic() < deadline:
                    alive()
                    if window(process.pid):
                        try:
                            interface = receiver_address()
                            with connect(timeout=.5, host=interface) as probe:
                                http_request(probe, "/server-info", method="GET", session=None)
                            break
                        except OSError:
                            pass
                    time.sleep(.05)
                else:
                    raise TimeoutError("Actual app receiver did not become ready")
                result["initialBounds"] = bounds(process.pid)
                if args.desktop_controls and not fullscreen:
                    result["initialVisual"] = desktop("snapshot", root / "before-video.png")
                with connect(host=interface) as control, connect(host=interface) as reverse:
                    verify_pairing(control, private, public)
                    verify_pairing(reverse, private, public)
                    http_request(reverse, "/reverse", expected=101)
                    filename = "stream.m3u8" if args.media in ("hls", "live") else "video.mp4"
                    body = plistlib.dumps({"uuid": str(uuid.uuid4()), "Content-Location": f"http://{media_host}:{server.server_port}/{filename}",
                        "Start-Position-Seconds": 0.0}, fmt=plistlib.FMT_BINARY)
                    http_request(control, "/play", body)
                    for _ in range(20):
                        time.sleep(.25)
                        alive()
                        info = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                    duration = info["duration"]
                    assert math.isfinite(duration) and (duration >= 0 if args.media == "live" else duration > 0), info
                    assert math.isfinite(info["position"]) and info["position"] > 1 and info["rate"] == 1 and info["readyToPlay"], info
                    for name in ("loadedTimeRanges", "seekableTimeRanges"):
                        assert isinstance(info[name], list), info
                        for interval in info[name]:
                            start, length = interval["start"], interval["duration"]
                            assert math.isfinite(start) and start >= 0 and math.isfinite(length) and length >= 0 and math.isfinite(start + length), info
                    result["checks"].append("A play request alone starts and advances actual app video without an extra resume request")
                    result["checks"].append("Actual playback info has finite nonnegative loaded and seekable time ranges")
                    discover()
                    result["checks"].append("Actual app answers AirPlay and RAOP discovery during playback")
                    result["playingBounds"] = bounds(process.pid)
                    if args.desktop_controls and not args.store_capture_only:
                        result["diagnostics"] = desktop("diagnostics", root / "video-diagnostics.png",
                            "any-rate" if args.source_video else "15", "fullscreen" if fullscreen else "windowed")
                        result["checks"].append("Native video F12 shows source and renderer information at the screen upper left in full screen or video upper left in a window, then hides it")
                    if args.desktop_controls and not fullscreen:
                        visual = desktop("snapshot", root / "playing-video.png")
                        result["playingVisual"] = visual
                        assert abs(visual["videoWidth"] / visual["videoHeight"] - 1280 / 720) < .01, visual
                        assert visual["backgroundChroma"] > result["initialVisual"]["backgroundChroma"] + 2, visual
                        assert visual["rounded"], "Actual video host is not clipped at all four corners"
                        result["checks"].append("Windowed video keeps its full aspect and colors the app-wide backdrop")
                        result["checks"].append("Actual windowed video uses four rounded corners")
                    if args.media == "live":
                        desktop("live-layout")
                        result["checks"].append("Actual live interface offers only disconnect rotation and full screen")
                        http_request(control, "/stop")
                        time.sleep(.5)
                        alive()
                        discover()
                        hwnd = window(process.pid)
                        assert hwnd and user32.PostMessageW(hwnd, 0x0010, 0, 0)
                        assert process.wait(timeout=12) == 0
                        result["passed"] = True
                        continue
                    # The immersive Store view captures active playback without
                    # focusing controls; the other Store view pauses and shows
                    # those controls, but both fill the actual native display.
                    playing_store_capture = bool(args.store_screenshots and args.store_capture_only and fullscreen)
                    if not playing_store_capture:
                        if args.desktop_controls:
                            desktop("toggle")
                        else:
                            http_request(control, "/rate?value=0")
                        time.sleep(.5)
                        alive()
                        info = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                        assert info["rate"] == 0
                        result["checks"].append("Desktop pause changes the actual pipeline" if args.desktop_controls else "Actual app survives remote pause")
                    if args.store_screenshots:
                        if not fullscreen:
                            desktop("fullscreen")
                            time.sleep(.4)
                        args.store_screenshots.mkdir(parents=True, exist_ok=True)
                        shot = args.store_screenshots / (args.language + "-video-" + ("fullscreen" if fullscreen else "controls") + ".png")
                        captured = desktop("snapshot", shot)
                        assert captured["coversMonitor"], "Store capture must fill the actual native display"
                        result["storeScreenshot"] = str(shot.resolve())
                        result["storeCapture"] = captured
                        if args.store_capture_only:
                            http_request(control, "/stop")
                            time.sleep(.5)
                            alive()
                            discover()
                            hwnd = window(process.pid)
                            assert hwnd and user32.PostMessageW(hwnd, 0x0010, 0, 0)
                            assert process.wait(timeout=12) == 0
                            result["passed"] = True
                            result["mode"] = "owned-window Store capture; no keyboard or mouse input"
                            continue
                    if args.desktop_controls:
                        desktop("seek")
                        time.sleep(.5)
                        alive()
                        info = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                        assert abs(info["position"] - info["duration"] / 2) < 2, info
                        result["checks"].append("Desktop accessible seek moves the actual pipeline to the midpoint")
                        if args.no_global_input:
                            desktop("seek-start")
                            result["skipped"].append({"check": "Desktop Home key seeks the actual pipeline to zero",
                                "reason": "--no-global-input; used owned-window accessible seek instead"})
                        else:
                            desktop("key")
                        time.sleep(.5)
                        info = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                        assert info["position"] < 2, info
                        result["checks"].append("Desktop accessible seek resets the actual pipeline to zero" if args.no_global_input
                            else "Desktop Home key seeks the actual pipeline to zero")
                        # Start between the fixture's one-second keyframes. This
                        # catches snapping and offsets based on an old UI value.
                        assert info["duration"] >= 18, "Relative skip fixture must leave room for both directions"
                        http_request(control, "/scrub?position=7.25")
                        time.sleep(.25)
                        baseline = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                        assert baseline["rate"] == 0 and abs(baseline["position"] - 7.25) <= .15, baseline
                        desktop("forward10")
                        time.sleep(.35)
                        forward = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                        assert forward["rate"] == 0 and abs(forward["position"] - baseline["position"] - 10) <= .15, (baseline, forward)
                        desktop("back10")
                        time.sleep(.35)
                        backward = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                        assert backward["rate"] == 0 and abs(backward["position"] - baseline["position"]) <= .15, (baseline, backward)
                        result["relativeSkipPaused"] = {"baseline": baseline["position"], "forward": forward["position"],
                            "backward": backward["position"], "toleranceSeconds": .15}
                        result["checks"].append("Paused desktop ten-second skips use the current non-keyframe position and preserve pause")
                        desktop("toggle")
                        time.sleep(.25)
                        moving = []
                        for action, offset in (("forward10", 10), ("back10", -10)):
                            before = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                            started = time.monotonic()
                            desktop(action)
                            time.sleep(.25)
                            after = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                            elapsed = time.monotonic() - started
                            delta = after["position"] - before["position"]
                            assert before["rate"] == after["rate"] == 1 and offset - .3 <= delta <= offset + elapsed + .3, (before, after, elapsed)
                            moving.append({"action": action, "before": before["position"], "after": after["position"], "elapsedSeconds": elapsed})
                        result["relativeSkipPlaying"] = moving
                        result["checks"].append("Playing desktop ten-second skips follow the advancing pipeline position and preserve playback")
                        desktop("toggle")
                        time.sleep(.25)
                        for target in ("RotateLeftButton", "RotateRightButton", "FullScreenButton", "PlayerStopButton"):
                            before = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                            desktop("focused-space", target)
                            time.sleep(.2)
                            after = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                            assert after["rate"] == 1 - before["rate"], (target, before, after)
                        result["checks"].append("Space down/repeat/up on focused rotation full-screen and disconnect actions only toggles playback once")
                        if args.no_global_input:
                            result["skipped"].extend([
                                {"check": "Desktop mouse drag seeks the actual pipeline to the midpoint", "reason": "--no-global-input"},
                                {"check": "Space from focused XAML toggles actual playback", "reason": "--no-global-input"}])
                            desktop("video-space")
                        else:
                            desktop("drag")
                            time.sleep(.5)
                            info = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                            # HLS key-unit seeking can choose the neighboring two-second segment.
                            tolerance = 2 if args.media == "hls" else 1
                            assert abs(info["position"] - info["duration"] / 2) <= tolerance, info
                            result["dragPositionSeconds"] = info["position"]
                            result["checks"].append("Desktop mouse drag seeks the actual pipeline to the midpoint")
                            desktop("space")
                        time.sleep(.5)
                        info = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                        assert info["rate"] == 1, info
                        desktop("video-space")
                        time.sleep(.5)
                        info = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                        assert info["rate"] == 0, info
                        result["checks"].append("Space through owned native video messages resumes and pauses actual playback" if args.no_global_input
                            else "Space from XAML and native video message paths toggles actual playback")
                        desktop("toggle" if args.no_global_input else "space")
                    else:
                        http_request(control, "/rate?value=1")
                    time.sleep(1)
                    alive()
                    http_request(control, "/stop")
                    time.sleep(.5)
                    alive()
                    result["checks"].append("Actual app survives resume and stop")
                    discover()
                    result["checks"].append("Actual app remains discoverable after playback stops")
                hwnd = window(process.pid)
                assert hwnd and user32.PostMessageW(hwnd, 0x0010, 0, 0)
                assert process.wait(timeout=12) == 0
                result["passed"] = True
            finally:
                if process.poll() is None:
                    hwnd = window(process.pid)
                    if hwnd:
                        user32.PostMessageW(hwnd, 0x0010, 0, 0)
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)
                result["exitCode"] = process.returncode
                error = state / "ui-error.log"
                if error.exists():
                    result["uiError"] = error.read_text(encoding="utf-8")[:8192]
        report["passed"] = True
    except Exception as error:
        failure = error
        report["failure"] = repr(error)
    finally:
        server.shutdown()
        server.server_close()
        worker.join(timeout=3)
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(report, indent=2), flush=True)
    if failure:
        raise failure


if __name__ == "__main__":
    main()
