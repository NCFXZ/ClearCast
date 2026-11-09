"""Keep the first displayed PIN usable after a failed sender probe disconnects.

All identities and state are disposable. The generated PINs, keys, SRP payloads
and credentials are never printed or written to this report.
SPDX-License-Identifier: GPL-3.0-or-later
"""
from receiver_test_bootstrap import ensure_controlled_dependencies, loaded_dependency_evidence
ensure_controlled_dependencies()

import json
import os
from argparse import ArgumentParser
from pathlib import Path
import re
import secrets
import time

from receiver_test_support import (ROOT, N, Receiver, connect, integer_bytes, integer_hash,
    padded, plist_request, request, sha1, verify_pairing)

parser = ArgumentParser(description=__doc__)
parser.add_argument("--skip-expiry", action="store_true", help="Rerun short protocol cases without repeating the real 61-second expiry check.")
args = parser.parse_args()
receiver = Receiver()
report = {"passed": False, "checks": [], "loadedDependencies": loaded_dependency_evidence()}
output = Path(os.environ.get("CLEARCAST_FIRST_PIN_REPORT", ROOT / ".runtime/qa-pairing/first-pin-retry-results.json"))
initial = {"method": "pin", "user": "AA:BB:CC:DD:EE:FF"}


def check(condition, name):
    assert condition, name
    report["checks"].append(name)


def connection_id():
    text = (receiver.state / "receiver.log").read_text(encoding="utf-8")
    return int(re.findall(r"connection=(\d+) opened", text)[-1])


def close_connection(sock, identifier):
    sock.close()
    deadline = time.monotonic() + 3
    while time.monotonic() < deadline:
        if f"connection={identifier} closed" in (receiver.state / "receiver.log").read_text(encoding="utf-8"):
            return
        time.sleep(.01)
    raise AssertionError("The disposable control connection did not close")


def challenge():
    receiver.events()
    discovery = connect()
    request(discovery, "/pair-pin-start")
    identifier = connection_id()
    required = [event for event in receiver.events() if event[0] == 6]
    check(len(required) == 1 and required[0][2] > 0, "one visible challenge with a generation")
    pin, generation = str(required[0][1]).zfill(4), required[0][2]
    close_connection(discovery, identifier)
    return pin, generation


def wrong_credential_proof(sock, server, visible_pin):
    # Compute a genuine SRP client proof for a different credential. Do not just
    # inject a malformed proof: the real sender completed these exact stages.
    wrong_pin = str(int(visible_pin) % 9999 + 1).zfill(4)
    salt, B = server["salt"], int.from_bytes(server["pk"], "big")
    a = secrets.randbits(256)
    A = pow(2, a, N)
    x = integer_hash(salt.lstrip(b"\0") + sha1((initial["user"] + ":" + wrong_pin).encode()))
    k = integer_hash(padded(N) + padded(2))
    u = integer_hash(padded(A) + padded(B))
    shared = pow((B - k * pow(2, x, N)) % N, a + u * x, N)
    K = sha1(integer_bytes(shared) + b"\0\0\0\0") + sha1(integer_bytes(shared) + b"\0\0\0\1")
    xor = bytes(left ^ right for left, right in zip(sha1(integer_bytes(N)), sha1(integer_bytes(2))))
    proof = sha1(xor + sha1(initial["user"].encode()) + salt.lstrip(b"\0") +
                 integer_bytes(A) + integer_bytes(B) + K)
    plist_request(sock, {"pk": integer_bytes(A), "proof": proof}, expected=470)


try:
    receiver.start()
    pin, generation = challenge()
    displayed = time.monotonic()
    probe = connect()
    server = plist_request(probe, initial)
    probe_id = connection_id()
    with connect("127.0.0.2") as outsider:
        plist_request(outsider, initial, expected=470)
    with connect() as concurrent:
        plist_request(concurrent, initial, expected=470)
    check(True, "other peers and concurrent same-peer sockets cannot share the active SRP claim")
    receiver.events()
    wrong_credential_proof(probe, server, pin)
    report["firstProbeMilliseconds"] = round((time.monotonic() - displayed) * 1000, 2)
    check(receiver.events() == [[7, 0, generation, 0, 2]], "wrong automatic proof remains rejected once for the original challenge")
    close_connection(probe, probe_id)
    with connect("127.0.0.2") as outsider:
        plist_request(outsider, initial, expected=470)
    check(True, "restored pending PIN remains bound to its original peer after the failed claimant closes")
    with connect() as retry:
        private, server_ed = receiver.pair(retry, pin=pin)
    events = receiver.events()
    check([10, 0, generation, 0, 0] in events and not any(event[0] == 6 for event in events),
          "first displayed PIN completes pairing on a fresh socket without issuing a second challenge")
    with connect() as returning:
        verify_pairing(returning, private, server_ed)
    check(True, "fresh first-pair verification succeeds after recovered PIN setup")
    with connect() as replay:
        plist_request(replay, initial, expected=470)
    check(True, "successful setup consumes its PIN; replay cannot acquire it")

    pin, generation = challenge()
    abandoned = connect()
    plist_request(abandoned, initial)
    close_connection(abandoned, connection_id())
    with connect() as retry:
        receiver.pair(retry, pin=pin)
    check(True, "an aborted SRP socket also leaves its original unexpired PIN usable")

    old_pin, old_generation = challenge()
    old = connect()
    plist_request(old, initial)
    old_id = connection_id()
    current_pin, current_generation = challenge()
    current = connect()
    plist_request(current, initial)
    current_id = connection_id()
    close_connection(old, old_id)
    with connect() as contender:
        plist_request(contender, initial, expected=470)
    check(current_generation > old_generation, "closing an obsolete claim cannot restore an old PIN while a newer claim is active")
    close_connection(current, current_id)
    with connect() as retry:
        receiver.pair(retry, pin=current_pin)
    check(True, "only the latest displayed challenge is recovered after its claimant disconnects")

    pin, generation = challenge()
    for _ in range(5):
        rejected = connect()
        server = plist_request(rejected, initial)
        identifier = connection_id()
        wrong_credential_proof(rejected, server, pin)
        close_connection(rejected, identifier)
    with connect() as blocked:
        plist_request(blocked, initial, expected=470)
    with connect() as blocked:
        request(blocked, "/pair-pin-start", expected=470)
    check(True, "five failed proofs across recovered sockets still enforce the existing cooldown")

    # A late disconnect must not restart the lifetime of the displayed code.
    # Use real production time rather than adding a test-only expiry bypass.
    if not args.skip_expiry:
        receiver.stop()
        receiver.start()
        pin, generation = challenge()
        issued = time.monotonic()
        late = connect()
        plist_request(late, initial)
        late_id = connection_id()
        time.sleep(max(0, 55 - (time.monotonic() - issued)))
        close_connection(late, late_id)
        with connect() as still_valid:
            plist_request(still_valid, initial)
            identifier = connection_id()
        close_connection(still_valid, identifier)
        report["lateHandoffSeconds"] = round(time.monotonic() - issued, 3)
        time.sleep(max(0, 61 - (time.monotonic() - issued)))
        with connect() as expired:
            plist_request(expired, initial, expected=470)
        check(True, "late socket recovery preserves the original sixty-second expiry without extending it")
        report["expiryCheckSeconds"] = round(time.monotonic() - issued, 3)
    else:
        report["expiryCheckSkipped"] = True
    report["passed"] = True
    print(f"PASS: {len(report['checks'])} first-visible-PIN recovery and authentication checks", flush=True)
except Exception as error:
    report["error"] = f"{type(error).__name__}: {error}"
    raise
finally:
    receiver.close()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
