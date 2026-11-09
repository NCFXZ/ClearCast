"""Authenticated real-DLL direct AirPlay video with a disposable native host.

Synthetic local MP4/HLS only; no device, microphone or protected-media content.
SPDX-License-Identifier: GPL-3.0-or-later
"""
from receiver_test_support import ROOT, Receiver, connect, verify_pairing
from receiver_test_bootstrap import loaded_dependency_evidence

import argparse
import ctypes as c
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import plistlib
import socket
import subprocess
import tempfile
import threading
import time
import uuid


def http_request(sock, path, body=b"", method="POST", session="clearcast-direct-fixture", expected=200, cseq=False, protocol="HTTP/1.1"):
    fields = {"Host": "127.0.0.1", "Content-Length": str(len(body))}
    if session is not None:
        fields["X-Apple-Session-ID"] = session
    if cseq:
        fields["CSeq"] = "1"
    if body:
        fields["Content-Type"] = "application/x-apple-binary-plist"
    header = f"{method} {path} {protocol}\r\n" + "".join(f"{key}: {value}\r\n" for key, value in fields.items()) + "\r\n"
    sock.sendall(header.encode() + body)
    reply = b""
    while b"\r\n\r\n" not in reply:
        part = sock.recv(65536)
        if not part:
            raise AssertionError("Receiver closed before an HTTP response.")
        reply += part
        assert len(reply) <= 1024 * 1024
    head, data = reply.split(b"\r\n\r\n", 1)
    status = int(head.split(b"\r\n", 1)[0].split()[1])
    length = next((int(line.split(b":", 1)[1]) for line in head.split(b"\r\n")[1:]
                   if line.lower().startswith(b"content-length:")), 0)
    while len(data) < length:
        part = sock.recv(65536)
        if not part:
            raise AssertionError("Receiver closed during an HTTP response.")
        data += part
    assert status == expected, (path, status, expected)
    return data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", type=Path, default=ROOT / ".runtime/receiver")
    parser.add_argument("--report", type=Path, default=ROOT / ".runtime/qa-rebuild/direct-video-protocol-results.json")
    args = parser.parse_args()
    for port in (47000, 47001, 47002):
        with socket.socket() as probe:
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            try:
                probe.bind(("127.0.0.1", port))
            except OSError as error:
                raise SystemExit("Close ClearCast and other receiver tests before this protocol test.") from error
    report = {"passed": False, "checks": [], "loadedDependencies": loaded_dependency_evidence(args.runtime),
              "scope": "Authenticated synthetic HTTP MP4/HLS through the actual receiver DLL and common D3D11 child host; no physical-device or audio acceptance."}
    checks = report["checks"]
    receiver = None
    server = None
    worker = None
    observed = []
    work_directory = None
    try:
        work_directory = tempfile.TemporaryDirectory(prefix="direct-protocol-", dir=ROOT / ".runtime")
        work = Path(work_directory.name)
        tools = ROOT / ".runtime/msys64/ucrt64/bin"
        environment = dict(os.environ)
        environment["PATH"] = str(tools) + os.pathsep + environment.get("PATH", "")
        for command in ([tools / "ffmpeg.exe", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=15", "-t", "8",
                         "-an", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "15", "-movflags", "+faststart", work / "video.mp4"],
                        [tools / "ffmpeg.exe", "-v", "error", "-i", work / "video.mp4", "-c", "copy", "-hls_time", "1", "-hls_list_size", "0",
                         "-hls_segment_filename", work / "segment-%03d.ts", work / "stream.m3u8"]):
            subprocess.run([str(value) for value in command], check=True, capture_output=True, env=environment, timeout=30)

        blocked = {"media": False}
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_GET(self):
                if blocked["media"]:
                    self.send_error(404)
                    return
                filename = work / self.path.split("?", 1)[0].lstrip("/")
                if filename.parent != work or not filename.is_file():
                    self.send_error(404)
                    return
                data = filename.read_bytes()
                start, end = 0, len(data) - 1
                requested = self.headers.get("Range", "")
                partial = requested.startswith("bytes=")
                if partial:
                    first, _, last = requested[6:].partition("-")
                    start = int(first or "0")
                    end = min(end, int(last) if last else end)
                self.send_response(206 if partial else 200)
                self.send_header("Content-Type", "application/vnd.apple.mpegurl" if filename.suffix == ".m3u8" else "video/mp2t" if filename.suffix == ".ts" else "video/mp4")
                self.send_header("Accept-Ranges", "bytes")
                self.send_header("Content-Length", str(max(0, end - start + 1)))
                if partial:
                    self.send_header("Content-Range", f"bytes {start}-{end}/{len(data)}")
                self.end_headers()
                try:
                    self.wfile.write(data[start:end + 1])
                except (BrokenPipeError, ConnectionResetError):
                    pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        base = f"http://127.0.0.1:{server.server_port}"
        receiver = Receiver(runtime=args.runtime, state=work / "state")
        receiver.dll.clearcast_receiver_read_media.argtypes = [c.c_void_p, c.c_int]
        receiver.dll.clearcast_receiver_read_stats.argtypes = [c.POINTER(c.c_uint64), c.POINTER(c.c_uint64), c.POINTER(c.c_int), c.POINTER(c.c_int)]
        receiver.start()

        def snapshot():
            output = c.create_string_buffer(8192)
            length = receiver.dll.clearcast_receiver_read_media(output, len(output))
            assert 0 < length <= len(output)
            return json.loads(output.value)

        def wait(predicate, timeout=8):
            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                observed.extend(receiver.events())
                if predicate():
                    return
                time.sleep(.03)
            raise AssertionError(("Timed out", observed[-12:], snapshot()))

        def body(uri):
            return plistlib.dumps({"uuid": str(uuid.uuid4()), "Content-Location": uri, "Start-Position-Seconds": 0.0}, fmt=plistlib.FMT_BINARY)

        with connect() as unpaired:
            http_request(unpaired, "/play", body(base + "/video.mp4"), expected=401)
        assert snapshot()["kind"] == "none"
        checks.append("Unpaired HTTP play is rejected without starting media")
        variants = [(protocol, sequence) for protocol in ("HTTP/1.1", "HTTP/1.0", "RTSP/1.0") for sequence in (False, True)]
        report["authorizationProtocolVariants"] = [{"protocol": protocol, "cseq": sequence} for protocol, sequence in variants]
        for protocol, sequence in variants:
            with connect() as unpaired:
                http_request(unpaired, "/play", body(base + "/video.mp4"), expected=401, cseq=sequence, protocol=protocol)
        assert snapshot()["kind"] == "none"
        checks.append("Unauthenticated HTTP1.1/HTTP1.0/RTSP media variants with and without CSeq are rejected")
        with connect() as discovery:
            data = http_request(discovery, "/server-info", method="GET", session=None)
            assert plistlib.loads(data)["features"] & 1
        checks.append("Unauthenticated server discovery remains available")

        with connect() as paired:
            private, server_key = receiver.pair(paired)
            verify_pairing(paired, private, server_key)
            for protocol, sequence in variants:
                with connect("127.0.0.2") as outsider:
                    http_request(outsider, "/play", body(base + "/video.mp4"), expected=401, cseq=sequence, protocol=protocol)
            checks.append("A different unverified peer cannot use the pairing across protocol/CSeq variants")
            with connect() as unsupported:
                http_request(unsupported, "/play", body(base + "/video.mp4"), expected=505, protocol="HTTP/1.0")
            checks.append("Authenticated media rejects unsupported HTTP versions before media side effects")
            with connect() as control:
                http_request(control, "/play", body(base + "/video.mp4"))
                wait(lambda: any(event[0] == 3 and event[1:3] == [320, 180] for event in observed))
                assert snapshot()["kind"] == "video"
                checks.append("Authenticated HTTP play produces actual 320x180 Video event in common host")
                rendered, dropped, width, height = c.c_uint64(), c.c_uint64(), c.c_int(), c.c_int()
                def frames_presented():
                    receiver.dll.clearcast_receiver_read_stats(c.byref(rendered), c.byref(dropped), c.byref(width), c.byref(height))
                    return rendered.value > 1 and (width.value, height.value) == (320, 180)
                wait(frames_presented)
                checks.append("Common D3D11 sink reports presented frames and decoded dimensions")
                for protocol, sequence in variants:
                    with connect() as wrong_session:
                        http_request(wrong_session, "/rate?value=0", session="different-owner", expected=401, cseq=sequence, protocol=protocol)
                checks.append("A mismatched Apple session cannot control video across protocol/CSeq variants")
                for path in ("/rate?", "/rate?value=NaN", "/scrub?", "/scrub?position=-1"):
                    http_request(control, path, expected=400)
                checks.append("Malformed, non-finite and negative control values are rejected")
                http_request(control, "/rate?value=0")
                wait(lambda: snapshot()["playing"] is False)
                paused = snapshot()["positionMs"]
                time.sleep(.3)
                assert abs(snapshot()["positionMs"] - paused) < 250
                checks.append("HTTP pause stops playback position")
                http_request(control, "/scrub?position=3")
                http_request(control, "/rate?value=1")
                wait(lambda: snapshot()["playing"] is True and snapshot()["positionMs"] >= 2500)
                checks.append("HTTP seek and resume update actual player position")
                info = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                assert info["duration"] > 5 and info["position"] >= 2.5
                checks.append("Playback info returns the actual media duration and position")
                http_request(control, "/stop")
                wait(lambda: snapshot()["kind"] == "none")
                assert any(event[0] == 4 for event in observed)
                checks.append("HTTP stop ends video and restores waiting state")
                observed.clear()
                http_request(control, "/play", body(base + "/stream.m3u8"))
                wait(lambda: snapshot()["kind"] == "video" and any(event[0] == 3 for event in observed))
                checks.append("HLS restarts through the same authenticated connection and host")
                observed.clear()
                blocked["media"] = True
                assert receiver.dll.clearcast_receiver_set_decoder(0) == 0
                try:
                    wait(lambda: any(event[0] == 9 and event[1] == 0 for event in observed))
                finally:
                    blocked["media"] = False
                observed.clear()
                wait(lambda: snapshot()["kind"] == "video" and any(event[0] == 3 for event in observed))
                assert not any(event[0] == 4 for event in observed)
                checks.append("A failed decoder rebuild rolls back without dropping the HTTP session")
                assert receiver.dll.clearcast_receiver_set_decoder(0) == 0
                wait(lambda: any(event[0] == 8 and event[1] == 0 for event in observed))
                checks.append("Decoder change rebuilds direct player while retaining the AirPlay control session")
                wait(lambda: snapshot()["kind"] == "none", timeout=14)
                checks.append("HLS end of stream returns to waiting without fatal receiver restart")
                assert not any(event[0] == 5 for event in observed)
        report["passed"] = True
        report["checks"] = checks
        report["presentedFramesBeforePause"] = rendered.value
        report["geometry"] = [width.value, height.value]
    except Exception as error:
        report["error"] = str(error)
        raise
    finally:
        if receiver:
            receiver.close()
        if server:
            server.shutdown()
            server.server_close()
        if worker:
            worker.join(timeout=5)
        if work_directory:
            work_directory.cleanup()
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
