"""Close the actual Windows app at several startup stages, using disposable state.

Usage: python tests/ui/ui_lifecycle_smoke.py --app C:/path/to/ClearCast.exe
       --output .runtime/qa-ui-lifecycle/results.json

Do not run alongside another AirPlay receiver. This exercises the real window
close path, including native startup/teardown; it does not validate media quality.
SPDX-License-Identifier: GPL-3.0-or-later
"""

import argparse
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import socket
import subprocess
import time
import uuid


def main():
    if os.name != "nt":
        raise RuntimeError("This UI smoke requires Windows.")
    parser = argparse.ArgumentParser()
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--output", type=Path,
                        default=Path(".runtime/qa-ui-lifecycle/results.json"))
    args = parser.parse_args()
    app = args.app.resolve(strict=True)
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    for port in (47000, 47001, 47002):
        with socket.socket() as probe:
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            try:
                probe.bind(("0.0.0.0", port))
            except OSError as error:
                raise RuntimeError(f"Close other receivers first (TCP {port} is busy).") from error

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user32.EnumWindows.argtypes = [callback_type, wintypes.LPARAM]
    user32.EnumWindows.restype = wintypes.BOOL
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    user32.IsWindowVisible.argtypes = [wintypes.HWND]
    user32.IsWindowVisible.restype = wintypes.BOOL
    user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
    user32.PostMessageW.restype = wintypes.BOOL

    def main_window(pid):
        windows = []

        @callback_type
        def collect(hwnd, _):
            owner = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            if owner.value == pid and user32.IsWindowVisible(hwnd):
                title = ctypes.create_unicode_buffer(256)
                user32.GetWindowTextW(hwnd, title, len(title))
                if title.value == "ClearCast":
                    windows.append(hwnd)
            return True

        if not user32.EnumWindows(collect, 0):
            raise ctypes.WinError(ctypes.get_last_error())
        return windows[0] if windows else None

    report = {"status": "running", "app": str(app), "cases": [],
              "limitation": "Actual UI startup/close paths only; no physical-device media acceptance."}
    failure = None
    try:
        for close_delay in (0.1, 0.4, 1.2):
            state = output.parent / (f"close-{int(close_delay * 1000)}ms-{uuid.uuid4().hex[:8]}")
            state.mkdir()
            (state / "clearcast-ui-test-state.marker").touch()
            environment = os.environ.copy()
            environment["CLEARCAST_HOME"] = str(state)
            environment["CLEARCAST_UI_TEST_RUN"] = "1"
            launched = time.monotonic()
            process = subprocess.Popen([str(app)], cwd=app.parent, env=environment,
                                       creationflags=subprocess.CREATE_NO_WINDOW)
            try:
                hwnd = None
                deadline = launched + 15
                while hwnd is None and time.monotonic() < deadline:
                    if process.poll() is not None:
                        raise RuntimeError(f"App exited before showing its window: {process.returncode}")
                    hwnd = main_window(process.pid)
                    time.sleep(0.02)
                if hwnd is None:
                    raise TimeoutError("The owned ClearCast window did not appear.")
                window_opened = time.monotonic()
                time.sleep(close_delay)
                close_requested = time.monotonic()
                if not user32.PostMessageW(hwnd, 0x0010, 0, 0):
                    raise ctypes.WinError(ctypes.get_last_error())
                exit_code = process.wait(timeout=8)
                close_seconds = time.monotonic() - close_requested
                if exit_code != 0:
                    raise RuntimeError(f"App returned {exit_code} after WM_CLOSE.")
                report["cases"].append({
                    "status": "pass", "closeDelayMilliseconds": round(close_delay * 1000),
                    "windowAppearedMilliseconds": round((window_opened - launched) * 1000, 2),
                    "closeMilliseconds": round(close_seconds * 1000, 2),
                    "exitCode": exit_code, "state": str(state),
                })
            finally:
                if process.poll() is None:
                    # Failure cleanup is restricted to the exact child created here.
                    process.kill()
                    process.wait(timeout=5)

        # A final bind check detects receiver sockets left behind by a close path.
        for protocol in (socket.SOCK_STREAM, socket.SOCK_DGRAM):
            for port in (47000, 47001, 47002):
                with socket.socket(type=protocol) as probe:
                    probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
                    probe.bind(("0.0.0.0", port))
        report["receiverPortsReleased"] = True
        report["status"] = "pass"
    except Exception as error:
        failure = error
        report["status"] = "fail"
        report["failure"] = repr(error)
    finally:
        output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps(report, ensure_ascii=True, indent=2), flush=True)
    if failure is not None:
        raise failure


if __name__ == "__main__":
    main()
