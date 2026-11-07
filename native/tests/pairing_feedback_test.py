"""Real-DLL pairing feedback correlation checks with disposable state.

The protocol still rejects unknown keys and invalid proofs. These checks ensure
that the resulting UI events describe the relevant challenge exactly once.
SPDX-License-Identifier: GPL-3.0-or-later
"""
import json
import os
import pathlib
import re
import socket

from receiver_test_support import (ROOT, Receiver, connect, ed25519,
    plist_request, raw_public, request, verify_pairing)
from receiver_test_bootstrap import loaded_dependency_evidence


for port in (47000, 47001, 47002):
    with socket.socket() as probe:
        probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        try:
            probe.bind(('127.0.0.1', port))
        except OSError as error:
            raise SystemExit('Close ClearCast and other AirPlay receivers first.') from error

receiver = Receiver()
report = {'status': 'fail', 'checks': [], 'state': str(receiver.state),
    'loadedDependencies': loaded_dependency_evidence()}
pins = []


def check(condition, name):
    assert condition, name
    report['checks'].append(name)


def challenge():
    receiver.events()
    with connect() as sock:
        request(sock, '/pair-pin-start')
    events = receiver.events()
    required = [event for event in events if event[0] == 6]
    check(len(required) == 1 and required[0][2] > 0, 'challenge has a positive generation')
    pins.append(str(required[0][1]).zfill(4))
    return pins[-1], required[0][2]


def unknown_key_probe():
    key = raw_public(ed25519.Ed25519PrivateKey.generate().public_key())
    with connect() as sock:
        request(sock, '/pair-verify', b'\1\0\0\0' + bytes(range(32)) + key,
                content_type='application/octet-stream', expected=470)
    events = receiver.events()
    check(len(events) == 1 and events[0] == [12, 0, 0, 0, 0],
          'unknown saved key requires authentication without a failed-PIN event')


def rejected_proof(sock, generation):
    plist_request(sock, {'pk': b'\1' * 256, 'proof': b'\0' * 20}, expected=470)
    events = receiver.events()
    check(events == [[7, 0, generation, 0, 2]],
          'invalid proof reports its own challenge exactly once')


try:
    receiver.start()
    unknown_key_probe()
    pin, old_generation = challenge()
    with connect() as old:
        plist_request(old, {'method': 'pin', 'user': 'AA:BB:CC:DD:EE:FF'})
        _, current_generation = challenge()
        check(current_generation > old_generation, 'new challenge advances the generation')
        unknown_key_probe()
        rejected_proof(old, old_generation)
        with connect() as current:
            plist_request(current, {'method': 'pin', 'user': 'AA:BB:CC:DD:EE:FF'})
            rejected_proof(current, current_generation)

    pin, generation = challenge()
    with connect('127.0.0.2') as outsider:
        plist_request(outsider, {'method': 'pin', 'user': 'AA:BB:CC:DD:EE:FF'}, expected=470)
    check(receiver.events() == [[7, 0, 0, 0, 3]],
          'another peer cannot claim or report failure against the current challenge')
    with connect() as paired:
        private, server_ed = receiver.pair(paired, pin=pin)
    check(receiver.events() == [[10, 0, generation, 0, 0]],
          'successful setup reports its own challenge exactly once')

    with connect() as returning:
        verify_pairing(returning, private, server_ed, corrupt=True)
    check(receiver.events() == [[7, 0, 0, 0, 5]],
          'tampered returning-client signature is still rejected without a PIN association')
    with connect() as returning:
        verify_pairing(returning, private, server_ed)
    check(receiver.events() == [], 'valid saved pairing still verifies successfully')

    log = (receiver.state / 'receiver.log').read_text(encoding='utf-8')
    check('stage=setup-proof outcome=rejected' in log and
          'stage=verification outcome=authentication-required' in log,
          'diagnostics identify the safe pairing stage and outcome')
    pairing_lines = [line.split(' ', 1)[1] for line in log.splitlines() if ' pairing ' in line]
    check(pairing_lines and all(re.fullmatch(
        r'pairing generation=\d+ stage=[a-z-]+ outcome=[a-z-]+', line)
        for line in pairing_lines), 'pairing diagnostics contain only counters and fixed stage names')
    report['status'] = 'pass'
    print('PASS: pairing feedback classification, challenge correlation, proof and signature rejection')
finally:
    receiver.close()
    report['checkCount'] = len(report['checks'])
    output = pathlib.Path(os.environ.get('CLEARCAST_PAIRING_FEEDBACK_REPORT',
        ROOT / '.runtime/qa-pairing-feedback-results.json'))
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding='utf-8')
