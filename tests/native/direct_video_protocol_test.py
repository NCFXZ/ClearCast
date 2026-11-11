"""Authenticated real-DLL direct AirPlay video with a disposable native host.

Synthetic local MP4/HLS only; no device, microphone or protected-media content.
SPDX-License-Identifier: GPL-3.0-or-later
"""
from receiver_test_support import ROOT, Receiver, connect, request, verify_pairing
from receiver_test_bootstrap import loaded_dependency_evidence
from media_fixture_support import media_fixture_address

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


def http_request(sock, path, body=b"", method="POST", session="clearcast-direct-fixture", expected=200, cseq=False, protocol="HTTP/1.1", headers=None):
    fields = {"Host": str(sock.getpeername()[0]), "Content-Length": str(len(body))}
    if headers:
        fields.update(headers)
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


def start_media_server(work, blocked=None):
    blocked = blocked if blocked is not None else {"media": False}

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
            gate = blocked.get("gate")
            if gate is not None:
                requested = blocked.get("requested")
                if requested is not None:
                    requested.set()
                if not gate.wait(timeout=10):
                    self.send_error(503)
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

    server = ThreadingHTTPServer(("0.0.0.0", 0), Handler)
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    return server, worker


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
        for command in ([tools / "ffmpeg.exe", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=15",
                         "-f", "lavfi", "-i", "anullsrc=sample_rate=48000:channel_layout=stereo", "-t", "8",
                         "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "15",
                         "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", work / "video.mp4"],
                        [tools / "ffmpeg.exe", "-v", "error", "-i", work / "video.mp4", "-vf", "scale=256:144", "-r", "24",
                         "-c:v", "libx264", "-preset", "ultrafast", "-g", "24", "-c:a", "aac", "-ar", "44100", "-ac", "1",
                         "-hls_time", "1", "-hls_list_size", "0",
                         "-hls_segment_filename", work / "segment-%03d.ts", work / "stream.m3u8"],
                        [tools / "ffmpeg.exe", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=15",
                         "-t", "60", "-an", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                         "-g", "300", "-keyint_min", "1", "-sc_threshold", "0", "-force_key_frames", "0,9.4,23.6,41.2",
                         "-movflags", "+faststart", work / "sparse.mp4"]):
            subprocess.run([str(value) for value in command], check=True, capture_output=True, env=environment, timeout=30)

        blocked = {"media": False}
        server, worker = start_media_server(work, blocked)
        base = f"http://{media_fixture_address()}:{server.server_port}"
        receiver = Receiver(runtime=args.runtime, state=work / "state")
        receiver.dll.clearcast_receiver_read_media.argtypes = [c.c_void_p, c.c_int]
        receiver.dll.clearcast_receiver_read_stats.argtypes = [c.POINTER(c.c_uint64), c.POINTER(c.c_uint64), c.POINTER(c.c_int), c.POINTER(c.c_int)]
        receiver.dll.clearcast_receiver_read_video_frame.argtypes = [c.c_void_p, c.c_int, c.POINTER(c.c_uint64)]
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

        def diagnostics_match(width, height, fps, rate, channels):
            value = snapshot().get("diagnostics", {})
            return (value.get("videoCodec") == "H.264" and value.get("audioCodec") == "AAC" and
                    (value.get("width"), value.get("height")) == (width, height) and
                    type(value.get("frameRate")) in (int, float) and abs(value["frameRate"] - fps) < .001 and
                    (value.get("sampleRate"), value.get("channels")) == (rate, channels))

        def record_diagnostics(stage, width, height, fps, rate, channels):
            wait(lambda: diagnostics_match(width, height, fps, rate, channels))
            value = snapshot()["diagnostics"]
            for key in ("videoBitrate", "audioBitrate"):
                # Sources may omit bitrate tags; no bitrate is invented from
                # decoded pixels, PCM bytes or the fixture encoder settings.
                assert type(value[key]) is int and 0 <= value[key] <= 1_000_000_000_000, (stage, key, value)
            report.setdefault("mediaDiagnostics", {})[stage] = value

        def assert_diagnostics_cleared():
            value = snapshot()["diagnostics"]
            assert value["videoCodec"] == value["audioCodec"] == "", value
            assert all(value[key] == 0 for key in ("width", "height", "frameRate", "videoBitrate",
                                                   "sampleRate", "channels", "audioBitrate")), value

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
            data = http_request(discovery, "/server-info", method="GET", session=None, headers={"X-Apple-Client-Name": "Unverified device"})
            features = plistlib.loads(data)["features"]
            # Preserve the pinned upstream HTTP video-discovery mask. Replacing
            # it with the full RTSP/TXT mask made real iOS senders negotiate a
            # different setup path and close before sending /reverse or /play.
            assert features == 0x27F, features
        with connect() as discovery:
            _, data, _ = request(discovery, "/info", method="GET")
            rtsp_features = plistlib.loads(data)["features"]
            _, data, _ = request(discovery, "/info", plistlib.dumps({"qualifier": ["txtAirPlay"]}, fmt=plistlib.FMT_BINARY),
                                 method="GET", content_type="application/x-apple-binary-plist")
            txt = plistlib.loads(data)["txtAirPlay"]
        txt_records = {}
        offset = 0
        while offset < len(txt):
            length = txt[offset]
            offset += 1
            assert offset + length <= len(txt), "AirPlay TXT record exceeds its length prefix"
            key, _, value = txt[offset:offset + length].partition(b"=")
            txt_records[key] = value
            offset += length
        txt_features = sum(int(word, 0) << (32 * index) for index, word in enumerate(txt_records[b"features"].split(b",")))
        assert rtsp_features == txt_features, (rtsp_features, txt_features)
        assert all(rtsp_features & (1 << bit) for bit in (0, 4, 7, 9)), rtsp_features
        report["discoveryFeatures"] = {"httpServerInfo": features, "rtspInfo": rtsp_features, "airplayTxt": txt_features}
        assert snapshot().get("deviceName", "") == ""
        checks.append("Unauthenticated server discovery remains available")
        checks.append("HTTP video discovery retains its interoperable upstream mask; RTSP and AirPlay TXT agree on video HLS mirroring and audio capabilities")

        with connect() as paired:
            private, server_key = receiver.pair(paired)
            verify_pairing(paired, private, server_key)
            http_request(paired, "/server-info", method="GET", headers={"X-Apple-Client-Name": "Test iPhone"})
            assert snapshot()["deviceName"] == "Test iPhone"
            checks.append("Only a verified connection supplies the connected sender name")
            for protocol, sequence in variants:
                with connect("127.0.0.2") as outsider:
                    http_request(outsider, "/play", body(base + "/video.mp4"), expected=401, cseq=sequence, protocol=protocol)
            checks.append("A different unverified peer cannot use the pairing across protocol/CSeq variants")
            with connect() as unsupported:
                verify_pairing(unsupported, private, server_key)
                http_request(unsupported, "/play", body(base + "/video.mp4"), expected=505, protocol="HTTP/1.0")
            checks.append("Authenticated media rejects unsupported HTTP versions before media side effects")
            with connect() as control:
                verify_pairing(control, private, server_key)
                # Hold source bytes back until startup /playback-info and /rate
                # run. A sender pause during loading remains a genuine pause;
                # readiness must not silently turn it into an automatic resume.
                source_gate, source_requested = threading.Event(), threading.Event()
                blocked.update(gate=source_gate, requested=source_requested)
                try:
                    http_request(control, "/play", body(base + "/video.mp4"))
                    assert source_requested.wait(timeout=8), "Direct player did not request its source"
                    initial = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                    assert not initial["readyToPlay"] and initial["rate"] == 0 and initial["playbackBufferEmpty"], initial
                    http_request(control, "/rate?value=0")
                finally:
                    source_gate.set()
                    blocked.pop("gate", None)
                    blocked.pop("requested", None)
                wait(lambda: bool(plistlib.loads(http_request(control, "/playback-info", method="GET"))["readyToPlay"]))
                preroll = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                assert preroll["rate"] == 0 and not snapshot()["playing"], preroll
                paused_position = snapshot()["positionMs"]
                time.sleep(.35)
                assert abs(snapshot()["positionMs"] - paused_position) < 150
                checks.append("Startup reports not-ready instead of playing; an explicit rate zero during loading remains paused after preroll")
                http_request(control, "/rate?value=1")
                wait(lambda: snapshot()["playing"] and snapshot()["positionMs"] >= 350)
                assert plistlib.loads(http_request(control, "/playback-info", method="GET"))["rate"] == 1
                checks.append("A sender resume following startup pause advances the actual pipeline")
                http_request(control, "/stop")
                wait(lambda: snapshot()["kind"] == "none")
                observed.clear()
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
                wait(lambda: snapshot()["playing"] and snapshot()["positionMs"] >= 350)
                assert plistlib.loads(http_request(control, "/playback-info", method="GET"))["rate"] == 1
                checks.append("A new play request autoplays and advances without an extra rate-one request after a paused previous session")
                record_diagnostics("mp4", 320, 180, 15, 48000, 2)
                checks.append("Actual MP4 diagnostics report H.264 320x180 at 15 fps and AAC 48 kHz stereo with bounded optional bitrates")
                backdrop = c.create_string_buffer(4097)
                backdrop[4096] = b"\xA5"
                epoch = c.c_uint64()
                wait(lambda: receiver.dll.clearcast_receiver_read_video_frame(backdrop, 4096, c.byref(epoch)) == 4096)
                assert epoch.value > 0 and len(set(backdrop.raw[:4096])) > 8 and backdrop[4096] == b"\xA5"
                checks.append("Actual decoded D3D11 frame supplies a bounded 32x32 BGRA backdrop sample")
                assert receiver.dll.clearcast_receiver_read_video_frame(backdrop, 4095, c.byref(epoch)) == 0
                checks.append("Backdrop sample rejects an undersized destination without overflow")
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
                # A URL player must not depend on mirror/audio /feedback heartbeats.
                until = time.monotonic() + 19
                while time.monotonic() < until:
                    info = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                    assert info["rate"] == 0 and snapshot()["kind"] == "video"
                    time.sleep(.2)
                checks.append("Paused direct video survives beyond the mirror heartbeat limit without feedback")
                with connect() as auxiliary:
                    verify_pairing(auxiliary, private, server_key)
                    request(auxiliary, "/auxiliary", method="FLUSH")
                    request(auxiliary, "/auxiliary", method="TEARDOWN")
                assert snapshot()["kind"] == "video"
                checks.append("Auxiliary RTSP flush teardown and socket closure retain the direct-video owner")
                http_request(control, "/scrub?position=3")
                http_request(control, "/rate?value=1")
                wait(lambda: snapshot()["playing"] is True and snapshot()["positionMs"] >= 2500)
                checks.append("HTTP seek and resume update actual player position")
                info = plistlib.loads(http_request(control, "/playback-info", method="GET"))
                assert info["duration"] > 5 and info["position"] >= 2.5
                checks.append("Playback info returns the actual media duration and position")
                current = snapshot()
                assert current["source"] == "direct" and current["controls"] & 1
                receiver.dll.clearcast_receiver_media_control.argtypes = [c.c_uint64, c.c_uint64, c.c_int, c.c_int64]
                def desktop(command, value=0, state=None):
                    state = state or snapshot()
                    result = receiver.dll.clearcast_receiver_media_control(state["controlSession"], state["contentRevision"], command, value)
                    report.setdefault("desktopCommands", []).append({"command": command, "value": value, "result": result})
                    return result
                assert desktop(1) == 0
                wait(lambda: not snapshot()["playing"])
                wait(lambda: snapshot()["controls"] & 8)
                assert desktop(4, 1000) == 0
                wait(lambda: snapshot()["positionMs"] < 1600)
                assert desktop(4, -1) == 87 and desktop(4, 600_000_001) != 0
                assert desktop(1) == 0
                wait(lambda: snapshot()["playing"])
                checks.append("Desktop pause seek and resume operate the actual pipeline and validate range")
                old_session = snapshot()
                http_request(control, "/stop")
                wait(lambda: snapshot()["kind"] == "none")
                assert_diagnostics_cleared()
                assert any(event[0] == 4 for event in observed)
                checks.append("HTTP stop ends video and restores waiting state")
                checks.append("HTTP stop clears all negotiated video and audio diagnostics")
                observed.clear()
                http_request(control, "/play", body(base + "/stream.m3u8"))
                wait(lambda: snapshot()["kind"] == "video" and any(event[0] == 3 for event in observed))
                checks.append("HLS restarts through the same authenticated connection and host")
                record_diagnostics("replacementHls", 256, 144, 24, 44100, 1)
                checks.append("Replacement HLS reports its own 256x144 24 fps and AAC 44.1 kHz mono instead of the previous MP4 format")
                assert desktop(4, 1000, old_session) != 0
                checks.append("A delayed desktop command cannot seek a replacement session")
                observed.clear()
                blocked["media"] = True
                assert receiver.dll.clearcast_receiver_set_decoder(0) == 0
                try:
                    wait(lambda: any(event[0] == 9 and event[1] == 0 for event in observed))
                finally:
                    blocked["media"] = False
                observed.clear()
                wait(lambda: snapshot()["kind"] == "video" and any(event[0] == 3 for event in observed))
                record_diagnostics("decoderRollbackHls", 256, 144, 24, 44100, 1)
                assert not any(event[0] == 4 for event in observed)
                checks.append("A failed decoder rebuild rolls back without dropping the HTTP session")
                assert receiver.dll.clearcast_receiver_set_decoder(0) == 0
                wait(lambda: any(event[0] == 8 and event[1] == 0 for event in observed))
                checks.append("Decoder change rebuilds direct player while retaining the AirPlay control session")
                wait(lambda: snapshot()["kind"] == "none", timeout=14)
                assert_diagnostics_cleared()
                checks.append("HLS end of stream returns to waiting without fatal receiver restart")
                checks.append("HLS end of stream clears diagnostics from the disposed pipeline")
                assert not any(event[0] == 5 for event in observed)
                playlist = (work / "stream.m3u8").read_text(encoding="utf-8")
                (work / "live.m3u8").write_text(playlist.replace("#EXT-X-ENDLIST", "").replace("#EXT-X-PLAYLIST-TYPE:VOD", "#EXT-X-PLAYLIST-TYPE:EVENT"), encoding="utf-8")
                http_request(control, "/play", body(base + "/live.m3u8"))
                try:
                    wait(lambda: snapshot()["kind"] == "video" and snapshot().get("isLive", False))
                finally:
                    report["liveState"] = snapshot()
                checks.append("A live HLS playlist reports live status separately from its seek window")
                assert desktop(5, 10000) != 0
                checks.append("Relative skipping rejects live streams even when their rolling window is seekable")
                http_request(control, "/stop")
                wait(lambda: snapshot()["kind"] == "none")
                assert_diagnostics_cleared()

                http_request(control, "/play", body(base + "/sparse.mp4"))
                wait(lambda: snapshot()["kind"] == "video" and snapshot()["controls"] & 8)
                http_request(control, "/rate?value=0")
                wait(lambda: not snapshot()["playing"])
                skip_state = snapshot()
                assert desktop(5, 10000, old_session) != 0
                assert all(desktop(5, value, skip_state) == 87 for value in (0, 1, -9999, 10001))
                checks.append("Relative skipping accepts only signed ten-second offsets and rejects a replaced session")

                def wait_position(target, tolerance=120):
                    wait(lambda: snapshot()["kind"] == "video" and abs(snapshot()["positionMs"] - target) <= tolerance)
                    return snapshot()["positionMs"]

                assert desktop(4, 7250, skip_state) == 0
                baseline = wait_position(7250)
                assert not snapshot()["playing"]
                assert desktop(5, 10000, skip_state) == 0
                forward = wait_position(baseline + 10000)
                assert not snapshot()["playing"]
                assert desktop(5, -10000, skip_state) == 0
                backward = wait_position(baseline)
                assert not snapshot()["playing"]
                report["relativeSkipPaused"] = {"baselineMs": baseline, "forwardMs": forward, "backwardMs": backward}
                checks.append("Paused relative skip moves exactly ten seconds from a non-keyframe position on an irregular-GOP source and preserves pause")

                # No snapshot or managed position refresh occurs between these
                # commands; every request deliberately reuses the old snapshot.
                assert desktop(5, 10000, skip_state) == 0
                assert desktop(5, 10000, skip_state) == 0
                assert desktop(5, -10000, skip_state) == 0
                wait_position(baseline + 10000)
                assert not snapshot()["playing"]
                checks.append("Consecutive plus-ten plus-ten minus-ten commands accumulate before any client position refresh")

                # A new absolute request replaces a still-pending relative seek,
                # then HTTP scrub replaces that target within the same session.
                assert desktop(5, 10000, skip_state) == 0
                assert desktop(4, 11750, skip_state) == 0
                assert desktop(5, 10000, skip_state) == 0
                wait_position(21750)
                assert desktop(5, 10000, skip_state) == 0
                http_request(control, "/scrub?position=12.75")
                assert desktop(5, 10000, skip_state) == 0
                wait_position(22750)
                assert not snapshot()["playing"]
                checks.append("Absolute seek and HTTP scrub replace older pending targets accurately without changing pause state")

                http_request(control, "/rate?value=1")
                wait(lambda: snapshot()["positionMs"] >= 24400)
                live_position = snapshot()["positionMs"]
                assert desktop(5, -10000, skip_state) == 0
                wait_position(live_position - 10000, tolerance=450)
                assert snapshot()["playing"]
                time.sleep(1.5)
                after_playing = snapshot()["positionMs"]
                assert after_playing >= live_position - 10000 + 1000
                assert desktop(5, 10000, skip_state) == 0
                wait_position(after_playing + 10000, tolerance=450)
                assert snapshot()["playing"]
                assert plistlib.loads(http_request(control, "/playback-info", method="GET"))["rate"] == 1
                report["relativeSkipPlaying"] = {"firstBaselineMs": live_position, "laterBaselineMs": after_playing}
                checks.append("Playing relative skip uses the current clock after playback advances and never retains the previous seek anchor")

                http_request(control, "/rate?value=0")
                assert desktop(4, 1250, skip_state) == 0
                wait_position(1250)
                assert desktop(5, -10000, skip_state) == 0
                wait_position(0)
                assert not snapshot()["playing"]
                assert desktop(4, 59500, skip_state) == 0
                wait_position(59500)
                assert desktop(5, 10000, skip_state) == 0
                wait(lambda: snapshot()["kind"] == "none" or snapshot()["positionMs"] >= 59900)
                checks.append("Relative skip clamps to the actual beginning and end of the finite seek range")
                if snapshot()["kind"] != "none":
                    http_request(control, "/stop")
                wait(lambda: snapshot()["kind"] == "none")
        report["passed"] = True
        report["checks"] = checks
        report["presentedFramesBeforePause"] = rendered.value
        report["geometry"] = [width.value, height.value]
    except Exception as error:
        report["error"] = str(error)
        print("Direct-video test failure:", repr(error), flush=True)
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
