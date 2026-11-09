"""Verify genuine SRP proofs for forced leading-zero salts against actual C code.

The fixture changes only test salt generation. Client proof calculation is
independent of the C verifier, and the old truncated-prefix form is a negative
control demonstrating that this regression catches the original defect.
SPDX-License-Identifier: GPL-3.0-or-later
"""
import argparse
import ctypes as c
import json
import os
import pathlib

from receiver_test_bootstrap import ROOT, ensure_controlled_dependencies, loaded_dependency_evidence

parser = argparse.ArgumentParser()
parser.add_argument('--fixture', required=True, type=pathlib.Path)
parser.add_argument('--runtime', type=pathlib.Path, default=ROOT / '.runtime/receiver')
parser.add_argument('--output', type=pathlib.Path, default=ROOT / '.runtime/qa-srp-salt-results.json')
args = parser.parse_args()
os.environ['CLEARCAST_PROBE_RUNTIME'] = str(args.runtime.resolve())
ensure_controlled_dependencies(args.runtime)
from receiver_test_support import N, integer_bytes, integer_hash, padded, sha1

cookie = os.add_dll_directory(str(args.runtime.resolve()))
fixture = c.CDLL(str(args.fixture.resolve()))
byte = c.c_ubyte
pointer = c.POINTER(byte)
fixture.clearcast_test_srp_begin.argtypes = [c.c_int, c.c_int, pointer, c.POINTER(c.c_int),
    pointer, c.POINTER(c.c_int)]
fixture.clearcast_test_srp_validate.argtypes = [pointer, c.c_int, pointer, c.c_int]
fixture.clearcast_test_srp_reset.argtypes = []
report = {'passed': False, 'cases': [], 'loadedDependencies': loaded_dependency_evidence(args.runtime)}


def verify(salt_length, old_prefix=False, wrong_pin=False):
    salt_buffer, public_buffer = (byte * 16)(), (byte * 256)()
    salt_size, public_size = c.c_int(), c.c_int()
    assert fixture.clearcast_test_srp_begin(salt_length, old_prefix, salt_buffer,
        c.byref(salt_size), public_buffer, c.byref(public_size)) == 0
    salt = bytes(salt_buffer[:salt_size.value])
    assert salt_size.value == salt_length
    if not old_prefix:
        assert salt == bytes(range(1, salt_length + 1))
    server_public = int.from_bytes(bytes(public_buffer[:public_size.value]), 'big')
    username = 'AA:BB:CC:DD:EE:FF'
    pin = '4321' if wrong_pin else '1234'
    private = 123456789
    client_public = pow(2, private, N)
    # Use the transmitted salt bytes verbatim, including for the negative
    # control; the fixed representation has no artificial leading padding.
    x = integer_hash(salt + sha1((username + ':' + pin).encode()))
    multiplier = integer_hash(padded(N) + padded(2))
    scrambling = integer_hash(padded(client_public) + padded(server_public))
    secret = pow((server_public - multiplier * pow(2, x, N)) % N,
        private + scrambling * x, N)
    key = sha1(integer_bytes(secret) + b'\0\0\0\0') + sha1(integer_bytes(secret) + b'\0\0\0\1')
    xor = bytes(a ^ b for a, b in zip(sha1(integer_bytes(N)), sha1(integer_bytes(2))))
    proof = sha1(xor + sha1(username.encode()) + salt + integer_bytes(client_public) +
        integer_bytes(server_public) + key)
    public = integer_bytes(client_public)
    public_array = (byte * len(public)).from_buffer_copy(public)
    proof_array = (byte * len(proof)).from_buffer_copy(proof)
    result = fixture.clearcast_test_srp_validate(public_array, len(public), proof_array, len(proof))
    expected = not wrong_pin and not (old_prefix and salt_length < 16)
    assert (result == 0) == expected, (salt_length, old_prefix, wrong_pin, result)
    mutual = result == 0 and bytes(proof_array) == sha1(integer_bytes(client_public) + proof + key)
    assert not expected or mutual
    report['cases'].append({'saltLength': salt_length, 'oldPrefixControl': old_prefix,
        'wrongPin': wrong_pin, 'accepted': result == 0, 'mutualProofVerified': mutual})


try:
    for length in (16, 15, 8, 1):
        verify(length)
        verify(length, wrong_pin=True)
        if length < 16:
            verify(length, old_prefix=True)
    report['passed'] = True
    print('PASS: 11 actual SRP proof cases, including leading-zero salts, wrong PINs, and old-prefix controls')
finally:
    fixture.clearcast_test_srp_reset()
    cookie.close()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
