"""No-port dual-stack PIN regression using actual patched handlers and SRP.

Only the separate fixture DLL substitutes a synthetic Windows neighbor table.
No real user state, network sockets, PINs or credential reports are used.
SPDX-License-Identifier: GPL-3.0-or-later
"""
from receiver_test_bootstrap import ensure_controlled_dependencies, loaded_dependency_evidence
controlled_runtime = ensure_controlled_dependencies()

from argparse import ArgumentParser
import ctypes as c
import ipaddress
import json
import os
from pathlib import Path
import secrets
import tempfile

from receiver_test_support import (ROOT, N, Receiver, integer_bytes, integer_hash,
    padded, plist_request, request, sha1, verify_pairing)

parser = ArgumentParser(description=__doc__)
parser.add_argument('--fixture', required=True)
args = parser.parse_args()
dependency_directory = os.add_dll_directory(str(controlled_runtime))
dll = c.CDLL(str(Path(args.fixture).resolve()))
for name, types in {
    'reset': [c.c_char_p], 'mode': [c.c_int], 'open': [c.c_char_p, c.c_int, c.c_uint],
    'close': [c.c_int], 'request': [c.c_int, c.c_char_p, c.c_int, c.c_void_p, c.c_int],
    'match': [c.c_char_p, c.c_int, c.c_uint, c.c_char_p, c.c_int, c.c_uint],
    'feedback': [c.c_int],
}.items():
    getattr(dll, 'clearcast_test_peer_' + name).argtypes = types
dll.clearcast_test_peer_expiry.restype = c.c_uint64
v4, v6 = ipaddress.ip_address('192.0.2.10').packed, ipaddress.ip_address('fe80::1234').packed
initial = {'method': 'pin', 'user': 'AA:BB:CC:DD:EE:FF'}
report = {'passed': False, 'scope': 'No sockets: actual production pairing handlers and crypto with fixture-only neighbor injection.',
          'checks': [], 'loadedDependencies': loaded_dependency_evidence()}
destination = Path(os.environ.get('CLEARCAST_PEER_REPORT', ROOT / '.runtime/qa-followup/pairing-peer-results.json'))


def check(condition, name):
    assert condition, name
    report['checks'].append(name)


class Connection:
    def __init__(self, address, zone=0):
        self.handle = dll.clearcast_test_peer_open(address, len(address), zone)
        assert self.handle, 'Fixture connection allocation failed'
        self.reply = b''

    def sendall(self, frame):
        output = c.create_string_buffer(8192)
        length = dll.clearcast_test_peer_request(self.handle, frame, len(frame), output, len(output))
        assert length > 0, ('Fixture handler failed', length)
        self.reply = output.raw[:length]

    def recv(self, size):
        data, self.reply = self.reply[:size], self.reply[size:]
        return data

    def close(self):
        if self.handle:
            dll.clearcast_test_peer_close(self.handle)
            self.handle = 0

    def __enter__(self):
        return self

    def __exit__(self, *unused):
        self.close()


def challenge(address=v6, zone=7):
    with Connection(address, zone) as discovery:
        request(discovery, '/pair-pin-start')
    return str(dll.clearcast_test_peer_pin()).zfill(4), dll.clearcast_test_peer_generation(), dll.clearcast_test_peer_expiry()


def wrong_proof(sock, server, pin):
    wrong = str(int(pin) % 9999 + 1).zfill(4)
    salt, B = server['salt'], int.from_bytes(server['pk'], 'big')
    a = secrets.randbits(256)
    A = pow(2, a, N)
    x = integer_hash(salt.lstrip(b'\0') + sha1((initial['user'] + ':' + wrong).encode()))
    k = integer_hash(padded(N) + padded(2))
    u = integer_hash(padded(A) + padded(B))
    S = pow((B - k * pow(2, x, N)) % N, a + u * x, N)
    K = sha1(integer_bytes(S) + b'\0\0\0\0') + sha1(integer_bytes(S) + b'\0\0\0\1')
    xor = bytes(left ^ right for left, right in zip(sha1(integer_bytes(N)), sha1(integer_bytes(2))))
    proof = sha1(xor + sha1(initial['user'].encode()) + salt.lstrip(b'\0') + integer_bytes(A) + integer_bytes(B) + K)
    plist_request(sock, {'pk': integer_bytes(A), 'proof': proof}, expected=470)


try:
    # Exercise the exact helper used by the production handler. API failures,
    # duplicates and invalid rows must not create a permissive fallback.
    allowed_modes = {0, 14, 18, 19, 20, 21}
    names = ['reachable', 'different_mac', 'different_luid', 'different_interface',
             'zero_mac', 'multicast_mac', 'router', 'unreachable_flag', 'incomplete',
             'api_error_with_allocated_table', 'missing_ipv4', 'missing_ipv6',
             'ambiguous_ipv4', 'ambiguous_ipv6_on_other_interface',
             'ignore_unreachable_other_interface', 'wrong_row_scope', 'null_table',
             'oversized_table', 'stale', 'delay', 'probe', 'permanent',
             'unreachable_state', 'zero_luid', 'zero_interface', 'zero_mac_length', 'oversized_mac_length']
    for mode, name in enumerate(names):
        dll.clearcast_test_peer_mode(mode)
        expected = mode in allowed_modes
        for a, az, b, bz in [(v6, 7, v4, 0), (v4, 0, v6, 7)]:
            check(bool(dll.clearcast_test_peer_match(a, len(a), az, b, len(b), bz)) == expected,
                  f'{name}: {len(a)}-byte to {len(b)}-byte neighbor policy')
        check(dll.clearcast_test_peer_tables() == 0, f'{name}: neighbor snapshot freed')
    dll.clearcast_test_peer_mode(9)
    check(dll.clearcast_test_peer_match(v6, 16, 7, v6, 16, 7) == 1, 'exact scoped address works without neighbor API')
    check(dll.clearcast_test_peer_match(v6, 16, 7, v6, 16, 8) == 0, 'same IPv6 on a different scope remains rejected')
    check(dll.clearcast_test_peer_match(v4, 4, 0, b'\xc0\x00\x02\x0b', 4, 0) == 0, 'same-family address changes remain rejected')
    check(dll.clearcast_test_peer_match(v6, 16, 0, v4, 4, 0) == 0, 'scope-free link-local alias rejected')
    check(dll.clearcast_test_peer_match(ipaddress.ip_address('2001:db8::1').packed, 16, 7, v4, 4, 0) == 0,
          'global IPv6 alias rejected')
    check(dll.clearcast_test_peer_match(None, 16, 7, v4, 4, 0) == 0, 'null address rejected')
    check(dll.clearcast_test_peer_match(v6, 15, 7, v4, 4, 0) == 0, 'invalid address length rejected')

    with tempfile.TemporaryDirectory(prefix='qa-peer-', dir=ROOT / '.runtime') as state:
        keyfile = str(Path(state) / 'receiver.pem').encode()
        assert dll.clearcast_test_peer_reset(keyfile) == 0
        pin, generation, expiry = challenge()
        with Connection(v6, 7) as probe:
            server = plist_request(probe, initial)
            with Connection(v4) as concurrent:
                plist_request(concurrent, initial, expected=470)
            wrong_proof(probe, server, pin)
        check(dll.clearcast_test_peer_expiry() == expiry, 'failed IPv6 claimant restores original absolute expiry')
        # This is the actual handset order missed by the old single-family
        # fixture: IPv6 discovery/probe, close, then the first manual IPv4 try.
        for mode in (1, 9, 12, 15):
            dll.clearcast_test_peer_mode(mode)
            with Connection(v4) as denied:
                plist_request(denied, initial, expected=470)
            check(dll.clearcast_test_peer_expiry() == expiry, f'policy denial mode{mode} does not consume or extend the pending code')
        dll.clearcast_test_peer_mode(0)
        with Connection(v4) as first_manual:
            private, server_ed = Receiver.pair(None, first_manual, pin=pin)
        check(dll.clearcast_test_peer_generation() == generation, 'first manual IPv4 try completes the original IPv6 challenge without a second PIN')
        with Connection(v4) as fresh:
            verify_pairing(fresh, private, server_ed)
        check(True, 'first-pair signature verification succeeds on the IPv4 connection')
        with Connection(v6, 7) as replay:
            plist_request(replay, initial, expected=470)
        check(dll.clearcast_test_peer_expiry() == 0, 'successful dual-stack setup consumes the visible code')

        assert dll.clearcast_test_peer_reset(keyfile) == 0
        pin, generation, expiry = challenge(v4, 0)
        with Connection(v4) as probe:
            wrong_proof(probe, plist_request(probe, initial), pin)
        with Connection(v6, 7) as retry:
            private, server_ed = Receiver.pair(None, retry, pin=pin)
        check(dll.clearcast_test_peer_generation() == generation, 'IPv4 discovery also supports the same-code first IPv6 retry')

        assert dll.clearcast_test_peer_reset(keyfile) == 0
        pin, generation, expiry = challenge()
        for address, zone in [(v6, 7), (v4, 0), (v6, 7)]:
            with Connection(address, zone) as rejected:
                wrong_proof(rejected, plist_request(rejected, initial), pin)
            check(dll.clearcast_test_peer_expiry() == expiry, 'alternating failed address-family claims retain the original deadline')
        with Connection(v4) as valid:
            Receiver.pair(None, valid, pin=pin)
        check(dll.clearcast_test_peer_generation() == generation, 'alternating-family recovery retains the original generation')

        assert dll.clearcast_test_peer_reset(keyfile) == 0
        old_pin, old_generation, old_expiry = challenge()
        obsolete = Connection(v4)
        plist_request(obsolete, initial)
        pin, generation, expiry = challenge()
        current = Connection(v6, 7)
        plist_request(current, initial)
        obsolete.close()
        with Connection(v4) as contender:
            plist_request(contender, initial, expected=470)
        check(generation > old_generation, 'closing an obsolete IPv4 alias cannot restore its code over a newer IPv6 claim')
        current.close()
        check(dll.clearcast_test_peer_expiry() == expiry, 'latest cross-family recovery preserves only the latest original deadline')
        with Connection(v4) as latest:
            Receiver.pair(None, latest, pin=pin)
        check(dll.clearcast_test_peer_generation() == generation, 'latest challenge alone completes after an obsolete alias closes')

        # The fifth failure already starts the existing shared cooldown. Its
        # event must say RateLimited immediately, without awaiting request six.
        assert dll.clearcast_test_peer_reset(keyfile) == 0
        pin, generation, expiry = challenge()
        for attempt in range(1, 6):
            address, zone = (v6, 7) if attempt % 2 else (v4, 0)
            with Connection(address, zone) as rejected:
                wrong_proof(rejected, plist_request(rejected, initial), pin)
                feedback = tuple(dll.clearcast_test_peer_feedback(field) for field in range(3))
                check(feedback == (1, generation, 2 if attempt < 5 else 4),
                      f'same generation failure{attempt}: immediate expected proof/cooldown feedback')
        check(dll.clearcast_test_peer_expiry() == 0, 'fifth same-generation failure still clears the pending PIN')
        with Connection(v4) as blocked:
            request(blocked, '/pair-pin-start', expected=470)
        check(dll.clearcast_test_peer_feedback(2) == 4, 'active cooldown still rejects a new challenge with RateLimited')

        assert dll.clearcast_test_peer_reset(keyfile) == 0
        for attempt in range(1, 6):
            address, zone = (v6, 7) if attempt % 2 else (v4, 0)
            pin, generation, expiry = challenge(address, zone)
            with Connection(address, zone) as rejected:
                wrong_proof(rejected, plist_request(rejected, initial), pin)
                feedback = tuple(dll.clearcast_test_peer_feedback(field) for field in range(3))
                check(feedback == (1, generation, 2 if attempt < 5 else 4),
                      f'cross-generation failure{attempt}: shared budget and immediate expected feedback')
        check(generation == 5 and dll.clearcast_test_peer_expiry() == 0,
              'new challenge generations do not reset the shared five-failure budget')
        with Connection(v6, 7) as blocked:
            request(blocked, '/pair-pin-start', expected=470)
        check(dll.clearcast_test_peer_feedback(2) == 4, 'cross-generation cooldown still rejects a sixth challenge')
        dll.clearcast_test_peer_destroy()
    report['passed'] = True
    print(f"PASS: {len(report['checks'])} neighbor-policy and actual dual-stack PIN-handler checks", flush=True)
except Exception as error:
    report['error'] = f'{type(error).__name__}: {error}'
    raise
finally:
    dll.clearcast_test_peer_destroy()
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(report, indent=2), encoding='utf-8')
