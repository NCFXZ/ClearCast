"""No-port authentication-before-media-effects regression.

Exercises the actual patched dispatcher with a synthetic existing RAOP owner
and stop spies, not physical playback. No PIN, key or user state is reported.
SPDX-License-Identifier: GPL-3.0-or-later
"""
from receiver_test_bootstrap import ensure_controlled_dependencies
runtime = ensure_controlled_dependencies()

import argparse
import ctypes as c
import json
import os
from pathlib import Path
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--fixture", type=Path, required=True)
parser.add_argument("--report", type=Path, required=True)
args = parser.parse_args()
cookie = os.add_dll_directory(str(runtime))
dll = c.CDLL(str(args.fixture.resolve()))
dll.clearcast_test_peer_reset.argtypes = [c.c_char_p]
dll.clearcast_test_peer_open.argtypes = [c.c_char_p, c.c_int, c.c_uint]
dll.clearcast_test_peer_close.argtypes = [c.c_int]
dll.clearcast_test_peer_dispatch.argtypes = [c.c_int, c.c_char_p, c.c_int, c.c_void_p, c.c_int]
dll.clearcast_test_peer_route_effects.argtypes = [c.c_int]
dll.clearcast_test_peer_route_error.argtypes = [c.c_int]
dll.clearcast_test_peer_route_error.restype = c.c_char_p
report = {"passed": False, "scope": "Actual dispatcher, synthetic RAOP owner and transport stop spies; no sockets.", "cases": []}
try:
    with tempfile.TemporaryDirectory(prefix="auth-routing-") as state:
        assert dll.clearcast_test_peer_reset(str(Path(state) / "receiver.pem").encode()) == 0
        for protocol in ("RTSP/1.0", "HTTP/1.0", "HTTP/1.1"):
            for cseq in (False, True):
                for session in (False, True):
                    for host in (False, True):
                        for path, method in (("/info", "GET"), ("/unknown", "GET"), ("/play", "POST"), ("/local.m3u8", "GET"),
                            *(("/stream", media_method) for media_method in ("SETUP", "RECORD", "FLUSH", "TEARDOWN", "SET_PARAMETER", "GET_PARAMETER"))):
                            handle = dll.clearcast_test_peer_open(b"\x7f\0\0\x02", 4, 0)
                            assert handle
                            try:
                                # A content type makes /info return its neutral
                                # discovery plist before querying DNS details.
                                fields = ["Content-Length: 0", "Content-Type: application/octet-stream"]
                                if cseq: fields.append("CSeq: 1")
                                if session: fields.append("X-Apple-Session-ID: unverified")
                                if host: fields.append("Host: 127.0.0.1")
                                frame = (f"{method} {path} {protocol}\r\n" + "\r\n".join(fields) + "\r\n\r\n").encode()
                                output = c.create_string_buffer(8192)
                                size = dll.clearcast_test_peer_dispatch(handle, frame, len(frame), output, len(output))
                                assert dll.clearcast_test_peer_route_effects(0) == 0, ("media slot claimed", protocol, cseq, session, host, path)
                                assert dll.clearcast_test_peer_route_effects(1) == 0, ("existing transport stopped", protocol, cseq, session, host, path)
                                if size == -2:
                                    error = dll.clearcast_test_peer_route_error(0)
                                    reason = dll.clearcast_test_peer_route_error(1)
                                    assert protocol.startswith("HTTP/") and path == "/stream" and method not in ("GET", "POST") and \
                                        error == b"HPE_INVALID_CONSTANT" and reason == b"Invalid method for HTTP/x.x request", \
                                        (protocol, method, path, error, reason)
                                    report["cases"].append({"protocol": protocol, "cseq": cseq, "session": session,
                                        "host": host, "path": path, "method": method, "parserRejected": True,
                                        "parserError": error.decode(), "mediaClaims": 0, "transportStops": 0})
                                    continue
                                assert size > 0, (protocol, cseq, session, host, path, method, size)
                                status = int(output.raw[:size].split(b"\r\n", 1)[0].split()[1])
                                if path == "/info": assert status == 200
                                if path in ("/play", "/local.m3u8", "/stream"): assert status == 401
                                report["cases"].append({"protocol": protocol, "cseq": cseq, "session": session,
                                    "host": host, "path": path, "method": method, "parserRejected": False,
                                    "status": status, "mediaClaims": 0, "transportStops": 0})
                            finally:
                                dll.clearcast_test_peer_close(handle)
        report["passed"] = True
        print(f"PASS: {len(report['cases'])} protocol/header variants cannot claim or stop existing media")
finally:
    dll.clearcast_test_peer_destroy()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
