"""Disposable, real-DLL AirPlay protocol fixtures. No production state is used."""
from receiver_test_bootstrap import ensure_controlled_dependencies, loaded_dependency_evidence
ensure_controlled_dependencies()

import ctypes as c
from ctypes import wintypes as w
import hashlib
import os
import pathlib
import plistlib
import secrets
import socket
import tempfile
import time

from cryptography.hazmat.primitives.asymmetric import ed25519, x25519
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.serialization import Encoding, PublicFormat

ROOT = pathlib.Path(__file__).resolve().parents[2]
N = int(
    'AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050A37329CBB4'
    'A099ED8193E0757767A13DD52312AB4B03310DCD7F48A9DA04FD50E8083969EDB767B0CF60'
    '95179A163AB3661A05FBD5FAAAE82918A9962F0B93B855F97993EC975EEAA80D740ADBF4FF'
    '747359D041D5C33EA71D281E446B14773BCA97B43A23FB801676BD207A436C6481F1D2B907'
    '8717461A5B9D32E688F87748544523B524B0D57D5EA77A2775D2ECFA032CFBDBF52FB37861'
    '60279004E57AE6AF874E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DB'
    'FBB694B5C803D89F7AE435DE236D525F54759B65E372FCD68EF20FA7111F9E4AFF73', 16)


def sha1(data):
    return hashlib.sha1(data).digest()


def integer_hash(data):
    return int.from_bytes(sha1(data), 'big')


def integer_bytes(value):
    return value.to_bytes((value.bit_length() + 7) // 8, 'big')


def padded(value):
    return value.to_bytes(256, 'big')


def raw_public(key):
    return key.public_bytes(Encoding.Raw, PublicFormat.Raw)


def connect(source='127.0.0.1', timeout=5):
    return socket.create_connection(('127.0.0.1', 47001), timeout=timeout,
                                    source_address=(source, 0))


def request(sock, path, body=b'', method='POST', content_type=None, expected=200,
            lowercase=False):
    fields = {'CSeq': '1', 'Content-Length': str(len(body))}
    if content_type:
        fields['Content-Type'] = content_type
    header = f'{method} {path} RTSP/1.0\r\n'
    header += ''.join(f'{name.lower() if lowercase else name}: {value}\r\n'
                      for name, value in fields.items()) + '\r\n'
    sock.sendall(header.encode() + body)
    reply = b''
    while b'\r\n\r\n' not in reply:
        part = sock.recv(65536)
        if not part:
            raise RuntimeError('Connection closed before response')
        reply += part
        if len(reply) > 2 * 1024 * 1024:
            raise RuntimeError('Response exceeded test bound')
    head, payload = reply.split(b'\r\n\r\n', 1)
    lines = head.split(b'\r\n')
    status = int(lines[0].split()[1])
    length = next((int(line.split(b':', 1)[1]) for line in lines[1:]
                   if line.lower().startswith(b'content-length:')), 0)
    while len(payload) < length:
        part = sock.recv(65536)
        if not part:
            raise RuntimeError('Connection closed during response')
        payload += part
    if expected is not None:
        assert status == expected, (path, status, expected)
    return status, payload, head


def plist_request(sock, value, expected=200, lowercase=False):
    status, data, _ = request(sock, '/pair-setup-pin',
        plistlib.dumps(value, fmt=plistlib.FMT_BINARY),
        content_type='application/x-apple-binary-plist', expected=expected,
        lowercase=lowercase)
    return plistlib.loads(data) if data and status == 200 else None


class Receiver:
    def __init__(self, runtime=None, state=None):
        if os.name != 'nt':
            raise RuntimeError('These integration tests require Windows')
        self.runtime = pathlib.Path(runtime or os.environ.get(
            'CLEARCAST_PROBE_RUNTIME', ROOT / '.runtime/receiver')).resolve()
        self.state = pathlib.Path(state or tempfile.mkdtemp(
            prefix='qa-native-', dir=ROOT / '.runtime'))
        c.WinDLL('kernel32').SetDefaultDllDirectories(0x1000)
        self.cookie = os.add_dll_directory(str(self.runtime))
        self.user32 = c.WinDLL('user32', use_last_error=True)
        self.user32.CreateWindowExW.argtypes = [w.DWORD, w.LPCWSTR, w.LPCWSTR,
            w.DWORD, c.c_int, c.c_int, c.c_int, c.c_int, w.HWND, w.HMENU,
            w.HINSTANCE, w.LPVOID]
        self.user32.CreateWindowExW.restype = w.HWND
        self.hwnd = self.user32.CreateWindowExW(0, 'STATIC', 'ClearCast protocol QA',
                                               0, 0, 0, 320, 240, None, None, None, None)
        if not self.hwnd:
            raise c.WinError(c.get_last_error())
        self.dll = c.CDLL(str(self.runtime / 'clearcast_receiver.dll'))
        self.dll.clearcast_receiver_start_v2.argtypes = [c.c_char_p, c.c_char_p,
            c.c_int, c.c_int, c.c_int, c.c_char_p, c.c_ssize_t, c.c_uint32]
        self.dll.clearcast_receiver_read_event.argtypes = [c.POINTER(c.c_int)] * 5
        self.running = False

    def events(self):
        output = []
        while True:
            args = [c.c_int() for _ in range(5)]
            if self.dll.clearcast_receiver_read_event(*[c.byref(a) for a in args]) != 1:
                return output
            output.append([a.value for a in args])

    def start(self, expect_error=None):
        result = self.dll.clearcast_receiver_start_v2(str(self.state).encode(),
            b'1920x1080', 30, 1, 0, b'0001', self.hwnd, 0)
        assert result == 0, ('start', result)
        self.running = True
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            for event in self.events():
                if expect_error is not None and event[0] == 5:
                    assert event[4] == expect_error, event
                    return
                if event[0] == 1:
                    assert expect_error is None, ('unexpected ready', event)
                    return
                if event[0] == 5:
                    raise RuntimeError(('receiver startup failed', event))
            time.sleep(.02)
        raise TimeoutError('Receiver did not become ready')

    def stop(self):
        result = self.dll.clearcast_receiver_stop()
        assert result == 0, ('stop', result)
        self.running = False

    def close(self):
        self.stop()
        self.user32.DestroyWindow(self.hwnd)

    def challenge(self, source='127.0.0.1'):
        self.events()
        with connect(source) as sock:
            request(sock, '/pair-pin-start')
        pending = self.events()
        return str(next(e[1] for e in pending if e[0] == 6)).zfill(4)

    def pair(self, sock, pin=None, private=None, username='AA:BB:CC:DD:EE:FF', lowercase=False):
        if pin is None:
            pin = self.challenge()
        challenge = plist_request(sock, {'method': 'pin', 'user': username},
                                  lowercase=lowercase)
        salt, B = challenge['salt'], int.from_bytes(challenge['pk'], 'big')
        a = secrets.randbits(256)
        A = pow(2, a, N)
        x = integer_hash(salt.lstrip(b'\0') + sha1((username + ':' + pin).encode()))
        k = integer_hash(padded(N) + padded(2))
        u = integer_hash(padded(A) + padded(B))
        S = pow((B - k * pow(2, x, N)) % N, a + u * x, N)
        K = sha1(integer_bytes(S) + b'\0\0\0\0') + sha1(integer_bytes(S) + b'\0\0\0\1')
        xor = bytes(a ^ b for a, b in zip(sha1(integer_bytes(N)), sha1(integer_bytes(2))))
        M = sha1(xor + sha1(username.encode()) + salt.lstrip(b'\0') +
                 integer_bytes(A) + integer_bytes(B) + K)
        proof = plist_request(sock, {'pk': integer_bytes(A), 'proof': M}, lowercase=lowercase)
        assert proof['proof'] == sha1(integer_bytes(A) + M + K), 'Server SRP proof mismatch'
        private = private or ed25519.Ed25519PrivateKey.generate()
        key = hashlib.sha512(b'Pair-Setup-AES-Key' + K).digest()[:16]
        iv = bytearray(hashlib.sha512(b'Pair-Setup-AES-IV' + K).digest()[:16])
        iv[15] = (iv[15] + 1) % 256
        encrypted = AESGCM(key).encrypt(bytes(iv), raw_public(private.public_key()), None)
        reply = plist_request(sock, {'epk': encrypted[:-16], 'authTag': encrypted[-16:]},
                              lowercase=lowercase)
        iv[15] = (iv[15] + 1) % 256
        server_ed = AESGCM(key).decrypt(bytes(iv), reply['epk'] + reply['authTag'], None)
        return private, server_ed


def verify_pairing(sock, private, server_ed, corrupt=False):
    ephemeral = x25519.X25519PrivateKey.generate()
    client_ecdh = raw_public(ephemeral.public_key())
    _, reply, _ = request(sock, '/pair-verify', b'\1\0\0\0' + client_ecdh +
                         raw_public(private.public_key()), content_type='application/octet-stream')
    shared = ephemeral.exchange(x25519.X25519PublicKey.from_public_bytes(reply[:32]))
    key = hashlib.sha512(b'Pair-Verify-AES-Key' + shared).digest()[:16]
    iv = hashlib.sha512(b'Pair-Verify-AES-IV' + shared).digest()[:16]
    cipher = Cipher(algorithms.AES(key), modes.CTR(iv)).encryptor()
    server_sig = cipher.update(reply[32:])
    ed25519.Ed25519PublicKey.from_public_bytes(server_ed).verify(server_sig, reply[:32] + client_ecdh)
    signature = bytearray(private.sign(client_ecdh + reply[:32]))
    if corrupt:
        signature[0] ^= 1
    body = b'\0\0\0\0' + cipher.update(bytes(signature))
    return request(sock, '/pair-verify', body, content_type='application/octet-stream',
                   expected=470 if corrupt else 200)
