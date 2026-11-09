"""Authenticated RTSP metadata boundaries against the actual receiver DLL.

The test deliberately sends no encrypted RTP audio, and does not claim physical
device playback. Pure parser/state and DACP socket cases live in media_audio_test.
SPDX-License-Identifier: GPL-3.0-or-later
"""
from receiver_test_bootstrap import ensure_controlled_dependencies, loaded_dependency_evidence
ensure_controlled_dependencies()

import ctypes as c
import json
import os
from pathlib import Path
import random
import struct
import math
import hashlib
import time

from receiver_test_support import ROOT, Receiver, connect, request, verify_pairing


def tag(name, payload):
    return name.encode("ascii") + struct.pack(">I", len(payload)) + payload


receiver = Receiver()
report = {"passed": False, "checks": [], "loadedDependencies": loaded_dependency_evidence()}
report["nativeDllSha256"] = hashlib.sha256((receiver.runtime / "clearcast_receiver.dll").read_bytes()).hexdigest()
output = Path(os.environ.get("CLEARCAST_MEDIA_PROTOCOL_REPORT", ROOT / ".runtime/qa-media/metadata-protocol-results.json"))


def check(value, name):
    assert value, name
    report["checks"].append(name)


receiver.dll.clearcast_receiver_read_media.argtypes = [c.c_void_p, c.c_int]
receiver.dll.clearcast_receiver_read_artwork.argtypes = [c.c_uint64, c.c_void_p, c.c_int]
receiver.dll.clearcast_receiver_media_command.argtypes = [c.c_int]


def snapshot():
    data = c.create_string_buffer(8192)
    required = receiver.dll.clearcast_receiver_read_media(data, len(data))
    check(0 < required <= len(data) and required == len(data.value) + 1, "bounded exact snapshot length")
    value = json.loads(data.value.decode("utf-8"))
    check(type(value.get("connected")) is bool, "connection status is an explicit JSON boolean")
    return value


def wait_disconnected():
    deadline = time.monotonic() + 3
    while time.monotonic() < deadline:
        if not snapshot()["connected"]:
            return
        time.sleep(.02)
    raise AssertionError("last authenticated control closure did not clear connected")


try:
    receiver.start()
    initial = snapshot()
    check(initial["kind"] == "none", "ready receiver has no fabricated music session")
    check(not initial["connected"], "ready receiver has no connected sender")
    activity = initial.get("audioActivity")
    check(isinstance(activity,dict) and set(activity) == {"valid","energy","density","ageMs","beatEpoch","beats"} and
          type(activity["valid"]) is bool and type(activity["ageMs"]) is int and
          type(activity["beatEpoch"]) is int and type(activity["beats"]) is list and
          all(type(activity[name]) in (float,int) and math.isfinite(activity[name]) and 0 <= activity[name] <= 1 for name in ("energy","density")),
          "actual DLL exports the bounded invariant audioActivity schema")
    check(activity == {"valid":False,"energy":0,"density":0,"ageMs":2147483647,"beatEpoch":0,"beats":[]},
          "ready without decoded PCM exports invalid zero activity and no-sample age")
    report["audioActivitySnapshots"] = {"ready":activity}
    with connect() as unauthorized:
        check(not snapshot()["connected"], "unverified TCP connection does not report connected")
        request(unauthorized, "/stream", tag("mlit", tag("minm", b"untrusted")), method="SET_PARAMETER",
                content_type="application/x-dmap-tagged", expected=401)
        check(not snapshot()["connected"], "rejected unauthenticated request does not report connected")
    with connect() as pairing:
        private, server = receiver.pair(pairing)
        check(not snapshot()["connected"], "completed PIN setup alone does not report connected")
    marker = "DO_NOT_LOG_TRACK_684725"
    seed = tag("mlit", tag("minm", marker.encode()))
    nested = seed
    for _ in range(8):
        nested = tag("mlit", nested)
    payloads = [b"", b"mlit\xff\xff\xff\xff", seed, nested,
                tag("mlit", tag("minm", b"\xc0\x80")), tag("minm", b"a\0b"),
                tag("mlit", tag("minm", b"x" * 513)), b"\0" * (64 * 1024 + 1)]
    rng = random.Random(0x4d455441)
    for _ in range(100):
        data = bytearray(seed)
        data[rng.randrange(len(data))] ^= rng.randrange(1, 256)
        payloads.append(bytes(data))
    with connect() as session:
        verify_pairing(session, private, server)
        verified = snapshot()
        check(verified["connected"] and verified["kind"] == "none",
              "pair-verified control is connected before any media or metadata")
        with connect() as sibling:
            verify_pairing(sibling, private, server)
            check(snapshot()["connected"], "two verified control connections report connected")
        check(snapshot()["connected"], "closing one verified control retains the other live connection")
        for body in payloads:
            request(session, "/stream", body, method="SET_PARAMETER", content_type="application/x-dmap-tagged")
        check(True, f"{len(payloads)} bounded and malformed authenticated DMAP requests remain safe")
        for content_type, body in [("image/png", b"\x89PNG\r\n\x1a\n" + b"\xff" * 25),
                                   ("image/jpeg", b"\xff\xd8\xff\xc0\xff\xff"),
                                   ("text/parameters", b"progress: 0/999999999999999999999/10\r\n")]:
            request(session, "/stream", body, method="SET_PARAMETER", content_type=content_type)
        current = snapshot()
        check(current["kind"] == "none" and not current["title"] and not current["artworkBytes"] and not current["controls"],
              "metadata without an active RTP owner does not create a session")
        check(current["connected"], "no-media snapshot retains its live authenticated control status")
        check(current["audioActivity"] == activity, "authenticated metadata alone cannot invent decoded PCM activity")
        report["audioActivitySnapshots"]["metadataWithoutPcm"] = current["audioActivity"]
        small = c.create_string_buffer(b"XXXXXXXX")
        needed = receiver.dll.clearcast_receiver_read_media(small, 1)
        check(needed > 1 and small.raw.startswith(b"XXXXXXXX"), "no partial JSON in undersized native buffer")
        check(receiver.dll.clearcast_receiver_read_artwork(current["artworkRevision"], None, 0) == 0,
              "missing artwork returns no bytes")
        check(receiver.dll.clearcast_receiver_read_artwork(current["artworkRevision"] + 1, None, 0) == -1,
              "stale artwork revision rejected")
        check(receiver.dll.clearcast_receiver_media_command(0) == 87 and receiver.dll.clearcast_receiver_media_command(4) == 87,
              "unsupported control values rejected")
        check(receiver.dll.clearcast_receiver_media_command(1) == 50, "inactive sender controls remain unavailable")
    wait_disconnected()
    check(not snapshot()["connected"], "last verified socket closure disconnects despite authorization grace")
    check(marker not in (receiver.state / "receiver.log").read_text(encoding="utf-8"), "track metadata is not written to diagnostics")
    receiver.stop()
    stopped = snapshot()
    check(stopped["kind"] == "none", "stop clears transient media snapshot")
    check(not stopped["connected"], "receiver stop reports disconnected")
    check(stopped["audioActivity"] == activity, "stop clears decoded activity and sample age through the real export")
    report["audioActivitySnapshots"]["stopped"] = stopped["audioActivity"]
    report["passed"] = True
    print(f"PASS: {len(report['checks'])} media protocol boundary checks", flush=True)
finally:
    receiver.close()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
