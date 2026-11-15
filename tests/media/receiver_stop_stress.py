"""Bounded real-DLL stop/reconnect checks against deliberately stalled media.

Run serially with ClearCast closed. Only disposable child processes, synthetic
media and isolated state are used. No physical-device or long-run leak claim.
SPDX-License-Identifier: GPL-3.0-or-later
"""
from pathlib import Path
import argparse
import json
import os
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/native"))
CASES = [f"{stage}-{action}" for stage in ("headers", "body", "playing", "disconnect")
         for action in ("http", "receiver")]


def save(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def child(args):
    # Controlled crypto must load before hashlib/ssl or HTTP fixture imports.
    os.environ["CLEARCAST_PROBE_RUNTIME"] = str(args.runtime)
    from receiver_test_support import Receiver, connect, verify_pairing
    from receiver_test_bootstrap import loaded_dependency_evidence
    from direct_video_protocol_test import http_request
    from media_fixture_support import media_fixture_address
    import ctypes as c
    from ctypes import wintypes as w
    import gc
    from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
    import plistlib
    import select
    import socket
    import threading
    import uuid

    report_path = args.work_dir / "result.json"
    report = {"case": args.child_case, "passed": False, "cycles": [],
              "loadedDependencies": loaded_dependency_evidence(args.runtime)}
    stage, action = args.child_case.split("-")
    after_playing = stage in ("playing", "disconnect")
    receiver = server = worker = current = None
    stop_limit = 3.0
    report["stopDeadlineSeconds"] = stop_limit

    def progress(label):
        report["lastStage"] = label
        save(report_path, report)

    def wait(predicate, timeout=8):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            pump()
            receiver.events()
            if predicate():
                return
            time.sleep(.02)
        raise AssertionError("Fixture condition did not complete before its deadline")

    class Memory(c.Structure):
        _fields_ = [("cb", w.DWORD), ("PageFaultCount", w.DWORD)] + [
            (name, c.c_size_t) for name in ("PeakWorkingSetSize", "WorkingSetSize",
                "QuotaPeakPagedPoolUsage", "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage",
                "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage", "PrivateUsage")]

    kernel, psapi, user = c.WinDLL("kernel32", use_last_error=True), c.WinDLL("psapi", use_last_error=True), c.WinDLL("user32")
    kernel.GetCurrentProcess.restype = w.HANDLE
    kernel.GetProcessHandleCount.argtypes = [w.HANDLE, c.POINTER(w.DWORD)]
    psapi.GetProcessMemoryInfo.argtypes = [w.HANDLE, c.POINTER(Memory), w.DWORD]
    user.GetGuiResources.argtypes = [w.HANDLE, w.DWORD]
    user.PeekMessageW.argtypes = [c.POINTER(w.MSG), w.HWND, w.UINT, w.UINT, w.UINT]
    user.TranslateMessage.argtypes = [c.POINTER(w.MSG)]
    user.DispatchMessageW.argtypes = [c.POINTER(w.MSG)]
    user.DispatchMessageW.restype = c.c_ssize_t
    handle = kernel.GetCurrentProcess()

    def pump():
        if args.pump_owner_messages:
            message = w.MSG()
            for _ in range(256):
                if not user.PeekMessageW(c.byref(message), None, 0, 0, 1):
                    break
                user.TranslateMessage(c.byref(message))
                user.DispatchMessageW(c.byref(message))

    def settle():
        until = time.monotonic() + args.settle_seconds
        while time.monotonic() < until:
            pump()
            time.sleep(min(.02, max(0, until - time.monotonic())))

    def windows():
        # Only class counts for this disposable process, never window titles.
        callback_type = c.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
        user.GetWindowThreadProcessId.argtypes = [w.HWND, c.POINTER(w.DWORD)]
        user.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, c.c_int]
        user.EnumWindows.argtypes = [callback_type, w.LPARAM]
        user.EnumChildWindows.argtypes = [w.HWND, callback_type, w.LPARAM]
        user.FindWindowExW.argtypes = [w.HWND, w.HWND, w.LPCWSTR, w.LPCWSTR]
        user.FindWindowExW.restype = w.HWND
        found = {}

        @callback_type
        def collect(hwnd, _):
            process_id = w.DWORD()
            user.GetWindowThreadProcessId(hwnd, c.byref(process_id))
            if process_id.value == os.getpid() and hwnd not in found:
                name = c.create_unicode_buffer(256)
                user.GetClassNameW(hwnd, name, len(name))
                found[hwnd] = name.value
            return True

        user.EnumWindows(collect, 0)
        for hwnd in list(found):
            user.EnumChildWindows(hwnd, collect, 0)
        previous = None
        for _ in range(10000):
            previous = user.FindWindowExW(w.HWND(-3), previous, None, None)
            if not previous:
                break
            collect(previous, 0)
        counts = {}
        for name in found.values():
            counts[name] = counts.get(name, 0) + 1
        return counts

    def resources():
        gc.collect()
        counters, count = Memory(), w.DWORD()
        counters.cb = c.sizeof(counters)
        if not psapi.GetProcessMemoryInfo(handle, c.byref(counters), counters.cb) or not kernel.GetProcessHandleCount(handle, c.byref(count)):
            raise c.WinError(c.get_last_error())
        return {"privateBytes": counters.PrivateUsage, "workingSetBytes": counters.WorkingSetSize,
                "handles": count.value, "gdiObjects": user.GetGuiResources(handle, 0),
                "userObjects": user.GetGuiResources(handle, 1)}

    class Gate:
        def __init__(self):
            self.requested, self.stalled, self.release = threading.Event(), threading.Event(), threading.Event()
            self.playing = threading.Event()
            self.lock, self.active, self.held = threading.Lock(), 0, []

        def stalled_connection(self):
            with self.lock:
                record = {"closedAt": None}
                self.held.append(record)
            self.stalled.set()
            return record

    class Server(ThreadingHTTPServer):
        daemon_threads = True
        block_on_close = False

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def hold(self, gate, record):
            deadline = time.monotonic() + 60
            while not gate.release.wait(.02) and time.monotonic() < deadline:
                try:
                    ready, _, _ = select.select([self.connection], [], [], 0)
                    if ready and not self.connection.recv(1, socket.MSG_PEEK):
                        with gate.lock:
                            record["closedAt"] = time.monotonic()
                        return
                except (ConnectionResetError, OSError):
                    with gate.lock:
                        record["closedAt"] = time.monotonic()
                    return

        def do_GET(self):
            gate = server.gate
            with gate.lock:
                gate.active += 1
            try:
                name = self.path.split("?", 1)[0].rsplit("/", 1)[-1]
                if name not in assets:
                    self.send_error(404)
                    return
                gate.requested.set()
                if stage == "headers":
                    self.hold(gate, gate.stalled_connection())
                    return
                withheld = stage == "body" or (after_playing and name.endswith(".ts") and int(name[8:11]) >= 2)
                if after_playing and withheld and not gate.playing.wait(8):
                    self.send_error(503)
                    return
                data = assets[name]
                first, last = 0, len(data) - 1
                byte_range = self.headers.get("Range", "")
                partial = byte_range.startswith("bytes=")
                if partial:
                    start, _, end = byte_range[6:].partition("-")
                    first, last = int(start or "0"), min(last, int(end) if end else last)
                self.send_response(206 if partial else 200)
                self.send_header("Content-Type", "application/vnd.apple.mpegurl" if name.endswith(".m3u8") else "video/mp2t" if name.endswith(".ts") else "video/mp4")
                self.send_header("Accept-Ranges", "bytes")
                self.send_header("Content-Length", str(max(0, last - first + 1)))
                if partial:
                    self.send_header("Content-Range", f"bytes {first}-{last}/{len(data)}")
                self.end_headers()
                payload = data[first:last + 1]
                self.wfile.write(payload[:128] if withheld else payload)
                self.wfile.flush()
                if withheld:
                    if stage == "disconnect":
                        gate.stalled.set()
                        self.close_connection = True  # Truncate the promised body at 128 bytes.
                    else:
                        self.hold(gate, gate.stalled_connection())
            except (BrokenPipeError, ConnectionResetError, OSError):
                pass
            finally:
                with gate.lock:
                    gate.active -= 1

    def timed(call):
        started = time.monotonic()
        call()
        elapsed = time.monotonic() - started
        assert elapsed < stop_limit, f"Stop took {elapsed:.3f}s (limit {stop_limit}s)"
        return round(elapsed * 1000, 3)

    try:
        assets = {path.name: path.read_bytes() for path in args.media_directory.iterdir() if path.is_file()}
        server = Server((media_fixture_address(), 0), Handler)
        server.gate = Gate()
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        base = f"http://{server.server_address[0]}:{server.server_port}"
        receiver = Receiver(runtime=args.runtime, state=args.work_dir / "state")
        receiver.dll.clearcast_receiver_read_media.argtypes = [c.c_void_p, c.c_int]
        receiver.dll.clearcast_receiver_read_stats.argtypes = [c.POINTER(c.c_uint64), c.POINTER(c.c_uint64), c.POINTER(c.c_int), c.POINTER(c.c_int)]

        def snapshot():
            output = c.create_string_buffer(8192)
            length = receiver.dll.clearcast_receiver_read_media(output, len(output))
            assert 0 < length <= len(output), "Media snapshot exceeded its ABI bound"
            return json.loads(output.value)

        def rendered():
            frames, dropped, width, height = c.c_uint64(), c.c_uint64(), c.c_int(), c.c_int()
            receiver.dll.clearcast_receiver_read_stats(c.byref(frames), c.byref(dropped), c.byref(width), c.byref(height))
            return frames.value > 1 and (width.value, height.value) == (320, 180)

        progress("initial-start-and-pair")
        receiver.start()
        with connect() as pairing:
            private, server_key = receiver.pair(pairing)
        report["resourcesInitialReady"] = resources()
        if args.window_census:
            report["windowClassesInitialReady"] = windows()
        for iteration in range(args.cycles):
            current = server.gate = Gate()
            entry = {"iteration": iteration + 1}
            report["cycles"].append(entry)
            progress(f"cycle-{iteration + 1}-play-{stage}")
            with connect(timeout=stop_limit) as control:
                verify_pairing(control, private, server_key)
                name = "stream.m3u8" if after_playing else "video.mp4"
                body = plistlib.dumps({"uuid": str(uuid.uuid4()), "Content-Location": f"{base}/{iteration}/{name}",
                                       "Start-Position-Seconds": 0.0}, fmt=plistlib.FMT_BINARY)
                http_request(control, "/play", body)
                assert current.requested.wait(8), "Receiver never requested the synthetic source"
                if after_playing:
                    wait(lambda: rendered() and snapshot()["positionMs"] >= 250)
                    entry["presentedBeforeStall"] = True
                    current.playing.set()
                assert current.stalled.wait(8), "Synthetic source did not reach the intended stall"
                if stage == "disconnect":
                    wait(lambda: current.active == 0, 3)
                    entry["sourceDisconnected"] = True
                # Never release the blocked source to make stop artificially pass.
                progress(f"cycle-{iteration + 1}-blocked-{action}-stop")
                entry["resourcesBeforeStop"] = resources()
                if args.window_census:
                    entry["windowClassesBeforeStop"] = windows()
                with current.lock:
                    watched = [record for record in current.held if record["closedAt"] is None]
                if stage != "disconnect":
                    assert watched, "No live stalled source remained immediately before stop"
                    entry["liveStalledConnectionsBeforeStop"] = len(watched)
                stop_started = time.monotonic()
                if action == "http":
                    entry["httpStopMs"] = timed(lambda: http_request(control, "/stop"))
                    started = time.monotonic()
                    wait(lambda: snapshot()["kind"] == "none", stop_limit)
                    entry["mediaClearMs"] = round((time.monotonic() - started) * 1000, 3)
                    entry["duplicateHttpStopMs"] = timed(lambda: http_request(control, "/stop"))
                else:
                    entry["receiverStopMs"] = timed(receiver.stop)
                assert not current.release.is_set(), "Test source unexpectedly resumed before stop"
                if receiver.running:
                    entry["receiverStopMs"] = timed(receiver.stop)
                assert snapshot()["kind"] == "none", "Stopped receiver retained media state"
                entry["duplicateReceiverStopMs"] = timed(receiver.stop)
                if stage != "disconnect":
                    def all_closed_after_stop():
                        with current.lock:
                            return all(record["closedAt"] is not None and record["closedAt"] >= stop_started for record in watched)
                    wait(all_closed_after_stop, 3)
                    entry["sourceSocketClosedBeforeGateRelease"] = True
                settle()
                entry["resourcesAfterStop"] = resources()
                if args.window_census:
                    entry["windowClassesAfterStop"] = windows()
            progress(f"cycle-{iteration + 1}-stopped-reconnect")
            current.release.set()
            wait(lambda: current.active == 0, 3)
            receiver.start()
            with connect() as returning:
                verify_pairing(returning, private, server_key)
                http_request(returning, "/server-info", method="GET", session=None)
            entry["pairedReconnect"] = True
            settle()
            entry["resourcesAfterReconnect"] = resources()
            if args.window_census:
                entry["windowClassesAfterReconnect"] = windows()
            progress(f"cycle-{iteration + 1}-complete")
        samples = [row["resourcesAfterReconnect"] for row in report["cycles"]]
        # First pass initializes codec/GPU/HTTP caches. A short-run safety budget
        # detects gross accumulation, not every leak or a long-term RSS bound.
        warmed = samples[1:]
        report["resourceTrend"] = {key: {"firstWarmed": warmed[0][key], "last": warmed[-1][key],
            "growth": warmed[-1][key] - warmed[0][key], "span": max(row[key] for row in warmed) - min(row[key] for row in warmed)}
            for key in samples[0]}
        report["shortRunResourceBudgets"] = {"privateBytesGrowth": 64 * 1024 * 1024, "handlesGrowth": 64, "gdiOrUserGrowth": 16}
        assert report["resourceTrend"]["privateBytes"]["growth"] <= 64 * 1024 * 1024, "Private-byte accumulation exceeded short-run budget"
        assert report["resourceTrend"]["handles"]["growth"] <= 64, "Handle accumulation exceeded short-run budget"
        for key in ("gdiObjects", "userObjects"):
            assert report["resourceTrend"][key]["growth"] <= 16, f"{key} accumulation exceeded short-run budget"
        report["passed"] = True
        progress("complete")
    except BaseException as error:
        report["error"] = f"{type(error).__name__}: {error}"
        save(report_path, report)
        raise
    finally:
        # The parent deadline also covers cleanup; it only kills this child tree.
        if current is not None:
            current.release.set()
        if receiver is not None:
            receiver.close()
            settle()
            report["resourcesAfterClose"] = resources()
            if args.window_census:
                report["windowClassesAfterClose"] = windows()
            save(report_path, report)
        if server is not None:
            server.shutdown()
            server.server_close()
        if worker is not None:
            worker.join(timeout=3)
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", type=Path, default=ROOT / ".runtime/receiver")
    parser.add_argument("--report-directory", type=Path)
    parser.add_argument("--cycles", type=int, default=4)
    parser.add_argument("--case", choices=CASES, help="Run one scenario, for long resource-trend diagnosis")
    parser.add_argument("--pump-owner-messages", action="store_true", help="Dispatch messages on the disposable Python window's thread, like a desktop UI loop")
    parser.add_argument("--window-census", action="store_true", help="Record only this process's window class counts before/after stop and reconnect")
    parser.add_argument("--settle-seconds", type=float, default=.1, help="Quiescent interval before resource samples")
    parser.add_argument("--timeout", type=int, default=100, help="Hard parent deadline in seconds for each owned child, including cleanup")
    parser.add_argument("--list", action="store_true", help="Describe cases without DLL loading, ports or media generation")
    parser.add_argument("--child-case", choices=CASES, help=argparse.SUPPRESS)
    parser.add_argument("--work-dir", type=Path, help=argparse.SUPPRESS)
    parser.add_argument("--media-directory", type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args()
    plan = [args.case] if args.case else CASES
    if args.list:
        print(json.dumps({"cases": plan, "cyclesPerCase": args.cycles, "hardChildDeadlineSeconds": args.timeout}, indent=2))
        return 0
    if os.name != "nt" or args.cycles < 3 or args.timeout <= 0 or not 0 <= args.settle_seconds <= 10:
        parser.error("Windows, at least three cycles, a positive child timeout, and a 0–10 second settle period are required")
    args.runtime = args.runtime.resolve(strict=True)
    if args.child_case:
        if args.work_dir is None or args.media_directory is None:
            parser.error("Child execution requires isolated work and media directories")
        for directory in (args.work_dir, args.media_directory):
            if not directory.resolve().is_relative_to((ROOT / ".runtime").resolve()) or directory.resolve() == (ROOT / ".runtime").resolve():
                parser.error("Child state and media must remain in dedicated .runtime subdirectories")
        return child(args)
    if args.report_directory is None:
        parser.error("--report-directory is required")
    output = args.report_directory.resolve()
    if not output.is_relative_to((ROOT / ".runtime").resolve()) or output == (ROOT / ".runtime").resolve():
        parser.error("Use a dedicated report directory inside .runtime")
    if output.exists() and any(output.iterdir()):
        parser.error("Use an empty report directory; prior evidence is never overwritten")
    from run_protocol_suite import receiver_slot, check_ports, stop_child, fingerprint, sha
    report = {"passed": False, "complete": False, "identity": fingerprint(args.runtime),
              "fixtureSha256": sha(Path(__file__)), "cases": [],
              "configuration": {"cycles": args.cycles, "pumpOwnerMessages": args.pump_owner_messages,
                                "windowCensus": args.window_census, "settleSeconds": args.settle_seconds},
              "scope": "Synthetic stalled HTTP MP4/HLS, actual receiver DLL/common D3D11 host, short stop/reconnect/resource trends; no physical-device or long-run guarantee"}
    output.mkdir(parents=True, exist_ok=True)
    summary = output / "receiver-stop-stress.json"
    save(summary, report)
    environment = dict(os.environ)
    environment["CLEARCAST_PROBE_RUNTIME"] = str(args.runtime)
    hidden = {"creationflags": subprocess.CREATE_NO_WINDOW}
    with receiver_slot(True):
        check_ports()
        media = output / "media"
        media.mkdir()
        ffmpeg = ROOT / ".runtime/msys64/ucrt64/bin/ffmpeg.exe"
        tools_env = dict(environment)
        tools_env["PATH"] = str(ffmpeg.parent) + os.pathsep + tools_env.get("PATH", "")
        commands = [[ffmpeg, "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=15",
            "-f", "lavfi", "-i", "anullsrc=sample_rate=48000:channel_layout=stereo", "-t", "12",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "15",
            "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", media / "video.mp4"],
            [ffmpeg, "-v", "error", "-i", media / "video.mp4", "-c", "copy", "-hls_time", "1",
             "-hls_list_size", "0", "-hls_segment_filename", media / "segment-%03d.ts", media / "stream.m3u8"]]
        for command in commands:
            subprocess.run([str(value) for value in command], env=tools_env, capture_output=True, check=True, timeout=30, **hidden)
        for case in plan:
            check_ports()
            work = output / case
            work.mkdir()
            row = {"name": case, "passed": False}
            report["cases"].append(row)
            save(summary, report)
            command = [sys.executable, str(Path(__file__).resolve()), "--runtime", str(args.runtime),
                       "--child-case", case, "--work-dir", str(work), "--media-directory", str(media),
                       "--cycles", str(args.cycles), "--settle-seconds", str(args.settle_seconds)]
            command += [flag for enabled, flag in ((args.pump_owner_messages, "--pump-owner-messages"),
                                                   (args.window_census, "--window-census")) if enabled]
            started = time.monotonic()
            with (work / "child.log").open("wb") as log:
                process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, env=environment, **hidden)
                try:
                    process.wait(timeout=args.timeout)
                except subprocess.TimeoutExpired:
                    row["timeout"] = True
                finally:
                    stop_child(process)
            row.update(exitCode=process.returncode, elapsedSeconds=round(time.monotonic() - started, 3))
            result_path = work / "result.json"
            if result_path.is_file():
                row["result"] = json.loads(result_path.read_text(encoding="utf-8"))
            row["passed"] = not row.get("timeout", False) and process.returncode == 0 and row.get("result", {}).get("passed") is True
            row["logSha256"] = sha(work / "child.log")
            save(summary, report)
        report["complete"] = True
        report["passed"] = all(row["passed"] for row in report["cases"])
        save(summary, report)
    print(f"{'PASS' if report['passed'] else 'FAIL'}: {sum(row['passed'] for row in report['cases'])}/{len(plan)} bounded receiver-stop scenarios; {summary}")
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
