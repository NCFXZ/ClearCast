"""Name-scoped saved pairings through the real receiver DLL and PIN protocol.

Synthetic shared and independent credentials only; no physical-device state.
SPDX-License-Identifier: GPL-3.0-or-later
"""
from receiver_test_support import (ROOT, Receiver, connect, request, verify_pairing,
                                   raw_public, ed25519, plistlib)
from direct_video_protocol_test import http_request
import argparse
import base64
import ctypes as c
import hashlib
import json
import re
import socket
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--report', type=str, default=str(ROOT / '.runtime/qa-pairing-name/protocol-results.json'))
args = parser.parse_args()
for port in (47000, 47001, 47002):
    with socket.socket() as probe:
        probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        probe.bind(('127.0.0.1', port))

receiver = Receiver()
receiver.dll.clearcast_receiver_read_media.argtypes = [c.c_void_p, c.c_int]
report = {'status': 'running', 'checks': [], 'scope': 'Real DLL, synthetic shared-key named peers and disposable state; no automatic iOS PIN-dialog claim.'}
private = ed25519.Ed25519PrivateKey.generate()
key = base64.b64encode(raw_public(private.public_key())).decode()
peer_a, peer_b = '127.0.0.1', '127.0.0.2'
name_a, name_b = 'Fixture Phone A', 'Fixture Phone B'

def check(condition, message):
    if not condition:
        raise AssertionError(message)
    report['checks'].append(message)

def identity():
    return hashlib.sha256((receiver.state / 'receiver.pem').read_bytes()).digest()

def saved():
    return [line.split(',', 3) for line in (receiver.state / 'clients.txt').read_text(encoding='utf-8').splitlines()]

def snapshot():
    data = c.create_string_buffer(8192)
    required = receiver.dll.clearcast_receiver_read_media(data, len(data))
    assert 0 < required <= len(data)
    return json.loads(data.value.decode('utf-8'))

def wait_disconnected():
    deadline = time.monotonic() + 3
    while time.monotonic() < deadline:
        if not snapshot()['connected']:
            return
        time.sleep(.02)
    raise AssertionError('Closed admitted transports did not clear connected status')

def pending_prompt():
    events = receiver.events()
    prompts = [event for event in events if event[0] == 6]
    check(len(prompts) == 1 and prompts[0][2] > 0 and not any(event[0] == 12 for event in events),
          'Name rejection emits one generated PIN challenge without a generic authentication error')
    return str(prompts[0][1]).zfill(4), prompts[0][2]

def pair(peer, username, name, fresh_tcp=False):
    pin = receiver.challenge(peer)
    with connect(peer) as sock:
        _, server = receiver.pair(sock, pin=pin, private=private, username=username)
        if not fresh_tcp:
            verify_pairing(sock, private, server, name=name)
            return server
    with connect(peer) as sock:
        verify_pairing(sock, private, server, name=name)
    return server

def setup(sock, name, expected=400, header_name=None, authorization=None):
    metadata = {'deviceID': 'AA:BB:CC:DD:EE:01', 'model': 'iPhone', 'eiv': b'', 'ekey': b''}
    if name is not None:
        metadata['name'] = name
    headers = {}
    if header_name is not None:
        headers['X-Apple-Client-Name'] = header_name
    if authorization is not None:
        headers['Authorization'] = authorization
    return request(sock, '/named-fixture', plistlib.dumps(metadata, fmt=plistlib.FMT_BINARY), method='SETUP',
                   content_type='application/x-apple-binary-plist', expected=expected, headers=headers or None)

def challenge_nonce(reply):
    found = re.search(r'WWW-Authenticate: Digest realm="raop", nonce="([0-9a-f]{32})"', reply[2].decode())
    assert found, 'A name refusal that shows a PIN must carry a password challenge'
    return found.group(1)

def answer_setup(sock, name, nonce, pin, expected):
    # A physical iPhone whose saved pairing still verifies never sends
    # pair-setup-pin. It answers this SETUP challenge with the typed code.
    md5 = lambda value: hashlib.md5(value.encode()).hexdigest()
    answer = md5(md5('iPhone:raop:' + pin) + ':' + nonce + ':' + md5('SETUP:/named-fixture'))
    return setup(sock, name, expected=expected, authorization='Digest username="iPhone", realm="raop", '
                 f'nonce="{nonce}", uri="/named-fixture", response="{answer}"')

try:
    receiver.start()
    server = pair(peer_a, 'AA:BB:CC:DD:EE:01', name_a)
    pair(peer_b, 'AA:BB:CC:DD:EE:02', name_b, fresh_tcp=True)
    check({row[3] for row in saved() if len(row) == 4 and row[3]} == {name_a, name_b},
          'Two names sharing one public key persist as independent grants, including fresh-TCP PIN handoff')
    with connect(peer_b) as sock:
        verify_pairing(sock, private, server, name=name_b)
        setup(sock, name_b)
    check(True, 'Known key and name reconnect without another PIN')

    wait_disconnected()
    registry_before_invalid = (receiver.state / 'clients.txt').read_bytes()
    # These are malformed/missing identity declarations, not a valid new name
    # asking to enroll. They must not manufacture a PIN prompt or connection.
    for invalid_name, header in ((None, None), (' \r\n\t ', None), ('Bad\nName', None),
                                 ('x' * 513, None), (7, None), ('Conflicting Name', name_b)):
        receiver.events()
        with connect(peer_a) as sock:
            verify_pairing(sock, private, server)
            setup(sock, invalid_name, expected=470, header_name=header)
            check(not snapshot()['connected'], 'An invalid or missing name never publishes connected status')
        check(not any(event[0] == 6 for event in receiver.events()),
              'An invalid, missing or conflicting name does not start an automatic PIN challenge')
        wait_disconnected()
    check((receiver.state / 'clients.txt').read_bytes() == registry_before_invalid,
          'Rejected invalid names leave every saved pairing record unchanged')

    # The real dispatcher reserves the RAOP connection type before this bounded
    # SETUP rejects its deliberately empty media keys. No RTP pipeline is needed
    # to exercise the existing single-owner/busy guard.
    with connect(peer_b) as owner:
        verify_pairing(owner, private, server, name=name_b)
        setup(owner, name_b)
        check(snapshot()['connected'] and snapshot()['deviceName'] == name_b,
              'The admitted synthetic RAOP owner is connected under its approved name')
        receiver.events()
        with connect(peer_a) as contender:
            verify_pairing(contender, private, server)
            setup(contender, 'Unknown While Busy', expected=503)
        check(not any(event[0] in (6, 7, 11, 12) for event in receiver.events()),
              'A valid unknown name while busy neither replaces the PIN nor emits a pairing or connection error')
        check(snapshot()['connected'] and snapshot()['deviceName'] == name_b,
              'A rejected contender leaves the existing owner connection and name unchanged')
        request(owner, '*', method='OPTIONS')
        check(True, 'The existing owner transport still answers after the busy contender is rejected')
    wait_disconnected()

    with connect(peer_a) as sock:
        verify_pairing(sock, private, server, name='Unknown Name', expected=401)
    check(True, 'A known shared key cannot enroll a different name without another successful PIN')
    with connect(peer_a) as sock:
        verify_pairing(sock, private, server)
        setup(sock, 'Unknown Name', expected=401)
    check(True, 'SETUP-only names are checked before media ownership and cannot bypass the header path')
    with connect(peer_a) as sock:
        verify_pairing(sock, private, server)
        setup(sock, None, expected=470)
    check(True, 'Named keys cannot fall back to an absent name or a hidden unnamed PIN placeholder')
    with connect(peer_b) as sock:
        verify_pairing(sock, private, server, name=name_b)
        setup(sock, name_a, expected=470, header_name=name_b)
    check(True, 'Conflicting header and SETUP names are rejected')

    receiver.stop()
    before_identity = identity()
    path = receiver.state / 'clients.txt'
    original = path.read_text(encoding='utf-8').splitlines(keepends=True)
    retained = []
    for line in original:
        row = line.rstrip('\r\n').split(',', 3)
        if row[0] == key and (len(row) < 4 or not row[3] or row[3] == name_a):
            continue
        retained.append(line)
    path.write_text(''.join(retained), encoding='utf-8', newline='')
    receiver.start()
    check(identity() == before_identity, 'Removing one named record keeps the receiver identity unchanged')
    with connect(peer_b) as sock:
        verify_pairing(sock, private, server, name=name_b, session='named-fixture-session')
        check(snapshot()['connected'] and snapshot()['deviceName'] == name_b,
              'Admitted connections publish the approved device name')
    check(True, 'The other same-key named device reconnects after deletion without another PIN')
    wait_disconnected()
    receiver.events()
    with connect(peer_a) as sock:
        verify_pairing(sock, private, server)
        check(not snapshot()['connected'], 'Key-only verification of a named key does not publish connected')
        setup(sock, name_a, expected=401)
        check(not snapshot()['connected'], 'A rejected SETUP does not publish an unnamed connected device')
    check(True, 'The removed device cannot re-add its name through SETUP after key-only verification')
    pending_prompt()
    with connect(peer_a) as sock:
        verify_pairing(sock, private, server, name=name_a, expected=401)
    with connect(peer_a) as sock:
        verify_pairing(sock, private, server)
        setup(sock, name_a, expected=401)
    check(not any(event[0] in (6, 7, 12) for event in receiver.events()),
          'Same-peer retries reuse the pending PIN and generation without another prompt or generic error')
    check(True, 'The removed key/name pair is rejected even though its shared public key remains trusted')
    with connect(peer_b) as sock:
        http_request(sock, '/playback-info', method='GET', session='named-fixture-session', expected=401,
                     headers={'X-Apple-Client-Name': name_a})
    check(True, 'A bearer HTTP transport cannot attach a different name to an authorized name-bound session')
    with connect(peer_b) as sock:
        http_request(sock, '/playback-info', method='GET', session='named-fixture-session')
    check(True, 'A name-free HTTP handoff inherits only its existing named session grant')
    with connect(peer_b) as sock:
        verify_pairing(sock, private, server)
        http_request(sock, '/playback-info', method='GET', session='named-fixture-session')
    check(True, 'A verified name-free HTTP transport can inherit but cannot mint a named session grant')
    with connect(peer_a) as sock:
        verify_pairing(sock, private, server)
        http_request(sock, '/playback-info', method='GET', session='unissued-named-session', expected=401)
    check(True, 'Key-only verification cannot create an unissued HTTP session after named deletion')

    # A successful PIN from B must not authorize same-key A at a different peer.
    pin = receiver.challenge(peer_b)
    with connect(peer_b) as sock:
        receiver.pair(sock, pin=pin, private=private, username='AA:BB:CC:DD:EE:02')
    with connect(peer_a) as sock:
        verify_pairing(sock, private, server, name=name_a, expected=401)
    auto_pin, auto_generation = pending_prompt()
    check(True, 'One peer cannot consume another same-key peer\'s completed PIN enrollment')
    with connect(peer_b) as sock:
        verify_pairing(sock, private, server, name=name_b)
    with connect(peer_b) as sock:
        verify_pairing(sock, private, server, name='Second New Name', expected=503)
    check(True, 'A completed PIN permits one named enrollment, not arbitrary subsequent aliases')
    check(not any(event[0] == 6 for event in receiver.events()),
          'Another peer cannot replace an active automatic PIN challenge')

    wrong_pin = str((int(auto_pin) + 1) % 10000).zfill(4)
    with connect(peer_a) as sock:
        verify_pairing(sock, private, server)
        stale = challenge_nonce(setup(sock, name_a, expected=401))
        current = challenge_nonce(setup(sock, name_a, expected=401))
        check(stale != current and not receiver.events(),
              'Each refused SETUP carries a fresh password challenge for the PIN already displayed')
        current = challenge_nonce(answer_setup(sock, name_a, stale, auto_pin, expected=401))
        current = challenge_nonce(answer_setup(sock, name_a, current, wrong_pin, expected=401))
        rejected = receiver.events()
        check(rejected and all(event[0] not in (6, 10, 12) and event[2] == auto_generation for event in rejected)
              and not snapshot()['connected'] and name_a not in {row[3] for row in saved() if len(row) == 4},
              'A replayed challenge or a wrong code is a counted PIN rejection that enrolls nothing')
        with connect(peer_b) as other:
            verify_pairing(other, private, server)
            answer_setup(other, name_a, current, auto_pin, expected=503)
        check(not receiver.events() and name_a not in {row[3] for row in saved() if len(row) == 4},
              'Another same-key peer cannot answer the challenge shown for the removed peer')
        answer_setup(sock, name_a, current, auto_pin, expected=400)
        check(receiver.events() == [[10, 0, auto_generation, 0, 0]],
              'The displayed code answered on SETUP completes pairing without pair-setup-pin')
        check(snapshot()['connected'] and snapshot()['deviceName'] == name_a,
              'The removed name is admitted and published by the answered SETUP itself')
    check({row[3] for row in saved() if len(row) == 4 and row[3]} == {name_a, name_b},
          'A new successful PIN restores only the removed named grant and keeps the other name')
    wait_disconnected()
    with connect(peer_a) as sock:
        verify_pairing(sock, private, server)
        answer_setup(sock, 'Replayed Answer', current, auto_pin, expected=401)
    replay_pin, replay_generation = pending_prompt()
    check(replay_generation != auto_generation, 'A used code and challenge cannot enroll a second name')

    def during_srp():
        with connect(peer_a) as retry:
            verify_pairing(retry, private, server)
            setup(retry, 'Replayed Answer', expected=401)
        check(not any(event[0] in (6, 7, 12) for event in receiver.events()),
              'A retry during claimed SRP preserves the original displayed PIN')
    with connect(peer_a) as sock:
        receiver.pair(sock, pin=replay_pin, private=private, username='AA:BB:CC:DD:EE:01',
                      after_challenge=during_srp)
    check(receiver.events() == [[10, 0, replay_generation, 0, 0]],
          'Fresh TCP completes the automatically displayed PIN without pair-pin-start')
    with connect(peer_a) as sock:
        verify_pairing(sock, private, server)
        setup(sock, name_a)
        check(snapshot()['connected'] and snapshot()['deviceName'] == name_a,
              'A SETUP-only approved name appears after automatic re-pairing')
    with connect(peer_b) as sock:
        verify_pairing(sock, private, server, name=name_b)
    check(True, 'The unaffected peer continues to reconnect after the removed peer is paired again')
    report['status'] = 'pass'
except Exception as error:
    report['status'] = 'fail'
    report['failure'] = repr(error)
    raise
finally:
    receiver.close()
    from pathlib import Path
    output = Path(args.report)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))
