"""Windows AirPlay integration test using disposable identities and local sockets.

SPDX-License-Identifier: GPL-3.0-or-later
"""
import base64
import hashlib
import socket

from receiver_test_support import (Receiver, connect, plist_request, request,
    raw_public, verify_pairing, ed25519)

for port in (47000, 47001, 47002):
    try:
        with socket.socket() as probe:
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            probe.bind(('127.0.0.1', port))
    except OSError as error:
        raise SystemExit(f'Close ClearCast and other AirPlay receivers first (TCP {port} is busy).') from error

receiver = Receiver()
print('Disposable test state:', receiver.state, flush=True)

def identity_hash():
    return hashlib.sha256((receiver.state / 'receiver.pem').read_bytes()).digest()

try:
    receiver.start()
    unpaired_identity = identity_hash()
    assert not (receiver.state / 'clients.txt').exists()
    receiver.stop()
    receiver.start()
    paired_identity = identity_hash()
    assert paired_identity != unpaired_identity, 'An unpaired cached identity was reused'
    assert hashlib.sha256((receiver.state / 'receiver.unpaired.pem').read_bytes()).digest() == unpaired_identity

    pin = receiver.challenge()
    initial = {'method':'pin','user':'AA:BB:CC:DD:EE:FF'}
    with connect('127.0.0.2') as outsider:
        plist_request(outsider, initial, expected=470)
    with connect() as pairing:
        plist_request(pairing, initial)
        with connect() as replay:
            plist_request(replay, initial, expected=470)
        private, server_ed = receiver.pair(pairing, pin=pin)
        saved = (receiver.state / 'clients.txt').read_text(encoding='utf-8')
        assert base64.b64encode(raw_public(private.public_key())).decode() in saved
    with connect() as returning:
        verify_pairing(returning, private, server_ed)
    with connect() as unknown:
        unknown_ed = raw_public(ed25519.Ed25519PrivateKey.generate().public_key())
        request(unknown, '/pair-verify', b'\1\0\0\0' + bytes(range(32)) + unknown_ed,
                content_type='application/octet-stream', expected=470)

    receiver.stop()
    receiver.start()
    assert identity_hash() == paired_identity, 'A paired identity changed after restart'
    with connect() as returning:
        verify_pairing(returning, private, server_ed)
    print('PASS: identity renewal; cross-TCP PIN; peer binding; one-shot challenge; '
          'pre-SETUP persistence; mutual verification; unknown-client rejection; '
          'paired identity and registration survive restart', flush=True)
finally:
    receiver.close()
