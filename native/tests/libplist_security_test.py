"""Bounded parser/copy regressions against the supplied libplist DLL.

Uses generated binary/XML documents and deterministic malformed inputs only;
does not start a receiver, read application state, or access network resources.
SPDX-License-Identifier: GPL-3.0-or-later
"""
import argparse
import ctypes as c
import json
import os
import pathlib
import plistlib
import random
from receiver_test_bootstrap import ensure_controlled_dependencies, loaded_dependency_evidence

ROOT = pathlib.Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--library', type=pathlib.Path, default=ROOT / '.runtime/receiver/libplist-2.0.dll')
parser.add_argument('--output', type=pathlib.Path, default=ROOT / '.runtime/qa-libplist-security-results.json')
args = parser.parse_args()
library = args.library.resolve()
controlled_names = ('libcrypto-3-x64.dll', 'libplist-2.0.dll') if (library.parent / 'libcrypto-3-x64.dll').is_file() else ('libplist-2.0.dll',)
ensure_controlled_dependencies(library.parent, controlled_names)
import hashlib

cookie = os.add_dll_directory(str(library.parent))
dependency_cookie = os.add_dll_directory(str(ROOT / '.runtime/msys64/ucrt64/bin'))
dll = c.CDLL(str(library))
P = c.c_void_p
dll.libplist_version.argtypes = []
dll.libplist_version.restype = c.c_char_p
for name in ('plist_from_bin', 'plist_from_xml'):
    getattr(dll, name).argtypes = [c.c_char_p, c.c_uint32, c.POINTER(P)]
for name in ('plist_to_bin', 'plist_to_xml'):
    getattr(dll, name).argtypes = [P, c.POINTER(P), c.POINTER(c.c_uint32)]
dll.plist_copy.argtypes = [P]
dll.plist_copy.restype = P
dll.plist_free.argtypes = [P]
dll.plist_mem_free.argtypes = [P]
report = {'passed': False, 'version': dll.libplist_version().decode(),
    'library': str(library), 'sha256': hashlib.sha256(library.read_bytes()).hexdigest(),
    'loadedDependencies': loaded_dependency_evidence(library.parent, controlled_names),
    'validRoundTrips': 0, 'explicitMalformedRejections': 0, 'sanitizedUnicodeCases': 0,
    'boundedMutationCases': 0}
assert report['version'] == '2.8.0', report['version']


def read(data, binary=True):
    node = P()
    result = (dll.plist_from_bin if binary else dll.plist_from_xml)(data, len(data), c.byref(node))
    return result, node


def write(node):
    buffer, length = P(), c.c_uint32()
    result = dll.plist_to_bin(node, c.byref(buffer), c.byref(length))
    try:
        assert result == 0 and buffer and length.value < 2 * 1024 * 1024
        return c.string_at(buffer, length.value)
    finally:
        dll.plist_mem_free(buffer)


def reject(data, binary=True):
    result, node = read(data, binary)
    try:
        assert result != 0 and not node, 'Malformed input was accepted'
        report['explicitMalformedRejections'] += 1
    finally:
        dll.plist_free(node)


def nested_binary(depth):
    objects = [b'\xa1' + (index + 1).to_bytes(2, 'big') for index in range(depth)] + [b'\x09']
    payload, offsets = bytearray(b'bplist00'), []
    for item in objects:
        offsets.append(len(payload))
        payload += item
    table_offset = len(payload)
    for offset in offsets:
        payload += offset.to_bytes(2, 'big')
    payload += b'\0' * 6 + b'\x02\x02' + len(objects).to_bytes(8, 'big')
    payload += b'\0' * 8 + table_offset.to_bytes(8, 'big')
    return bytes(payload)


try:
    fixtures = [{}, {'ascii': 'hello', 'number': 42, 'bool': True},
        {'unicode': '机器人🤖中文' * 4096, 'bytes': bytes(range(256)) * 256},
        {'limits': [-(2**63), 2**64 - 1, 0, True, False], 'real': 1.25}]
    for value in fixtures:
        for fmt in (plistlib.FMT_BINARY, plistlib.FMT_XML):
            # XML's signed integer representation does not accept unsigned64.
            if fmt == plistlib.FMT_XML and value.get('limits'):
                continue
            encoded = plistlib.dumps(value, fmt=fmt)
            result, node = read(encoded, fmt == plistlib.FMT_BINARY)
            clone = None
            try:
                assert result == 0 and node
                clone = dll.plist_copy(node)
                assert clone and plistlib.loads(write(clone)) == value
                assert plistlib.loads(write(node)) == value
                report['validRoundTrips'] += 1
            finally:
                dll.plist_free(clone)
                dll.plist_free(node)

    seed = plistlib.dumps({'key': 'value', 'number': 42}, fmt=plistlib.FMT_BINARY)
    for cut in range(len(seed)):
        reject(seed[:cut])
    for index, values in ((6, (0, 9, 255)), (7, (0, 9, 255))):
        for value in values:
            data = bytearray(seed)
            data[-32 + index] = value
            reject(bytes(data))
    for start, value in ((8, 0), (8, 2**64 - 1), (16, 2**64 - 1),
                         (24, len(seed)), (24, 2**64 - 1)):
        data = bytearray(seed)
        data[-32 + start:-32 + start + 8 if start < 24 else None] = value.to_bytes(8, 'big')
        reject(bytes(data))

    # The upstream reader deliberately skips unpaired UTF-16 surrogates. Check
    # that this produces a valid, empty Unicode value without exposing padding.
    malformed_string = b'bplist00\x61\xd8\x00\x08' + b'\0' * 6 + b'\x01\x01'
    malformed_string += (1).to_bytes(8, 'big') + b'\0' * 8 + (11).to_bytes(8, 'big')
    result, node = read(malformed_string)
    try:
        assert result == 0 and node and plistlib.loads(write(node)) == ''
        report['sanitizedUnicodeCases'] += 1
    finally:
        dll.plist_free(node)
    reject(nested_binary(1000))
    reject(b'<plist>' + b'<array>' * 1000 + b'<true/>' + b'</array>' * 1000 + b'</plist>', False)
    for data in (b'<plist><integer>1</integer><string>second root</string></plist>',
                 b'<plist><unknown>value</unknown></plist>', b'<plist><string>unterminated'):
        reject(data, False)

    rng = random.Random(0xC1EA)
    for unused in range(1000):
        data = bytearray(seed)
        for mutation in range(1 + rng.randrange(4)):
            data[rng.randrange(len(data))] = rng.randrange(256)
        result, node = read(bytes(data))
        try:
            if result == 0 and node:
                clone = dll.plist_copy(node)
                dll.plist_free(clone)
            report['boundedMutationCases'] += 1
        finally:
            dll.plist_free(node)
    report['passed'] = True
    print(json.dumps(report))
finally:
    dependency_cookie.close()
    cookie.close()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
