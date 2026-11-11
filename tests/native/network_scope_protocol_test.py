"""Real-DLL adapter-bound TCP, RTP/NTP UDP, mDNS and restart checks.

Runs serially with every other receiver/application test. Uses existing adapter
addresses and disposable state; never changes an adapter or firewall setting.
No RTP media payload is sent, so this does not establish audiovisual playback.
SPDX-License-Identifier: GPL-3.0-or-later
"""
from receiver_test_bootstrap import ensure_controlled_dependencies, loaded_dependency_evidence
ensure_controlled_dependencies()

import argparse
import ctypes as c
from ctypes import wintypes as w
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import socket
import struct
import tempfile
import time

from receiver_test_support import ROOT, Receiver, request, verify_pairing, plistlib
from mdns_discovery_test import service_ports


class SocketAddress(c.Structure):
    _fields_ = [('value', c.c_void_p), ('length', c.c_int)]


class Unicast(c.Structure):
    pass


Unicast._fields_ = [('length', w.DWORD), ('flags', w.DWORD), ('next', c.POINTER(Unicast)),
    ('address', SocketAddress), ('prefix_origin', c.c_int), ('suffix_origin', c.c_int),
    ('dad', c.c_int), ('valid_lifetime', w.DWORD), ('preferred_lifetime', w.DWORD),
    ('lease_lifetime', w.DWORD), ('prefix_length', c.c_ubyte)]


class Adapter(c.Structure):
    pass


# Only the documented prefix is needed. Windows fills its complete structures
# in the caller's buffer; Next and FirstUnicastAddress stay valid until parsed.
Adapter._fields_ = [('length', w.DWORD), ('if4', w.DWORD), ('next', c.POINTER(Adapter)),
    ('name', c.c_char_p), ('unicast', c.POINTER(Unicast)), ('anycast', c.c_void_p),
    ('multicast', c.c_void_p), ('dns_servers', c.c_void_p), ('dns_suffix', c.c_void_p),
    ('description', c.c_void_p), ('friendly_name', c.c_void_p), ('mac', c.c_ubyte * 8),
    ('mac_length', w.DWORD), ('flags', w.DWORD), ('mtu', w.DWORD), ('kind', w.DWORD),
    ('status', c.c_int), ('if6', w.DWORD), ('zones', w.DWORD * 16)]


class Tcp4(c.Structure):
    _fields_ = [(name, w.DWORD) for name in ('state', 'address', 'port', 'remote', 'remote_port', 'pid')]


class Tcp6(c.Structure):
    _fields_ = [('address', c.c_ubyte * 16), ('scope', w.DWORD), ('port', w.DWORD),
        ('remote', c.c_ubyte * 16), ('remote_scope', w.DWORD), ('remote_port', w.DWORD),
        ('state', w.DWORD), ('pid', w.DWORD)]


class Udp4(c.Structure):
    _fields_ = [(name, w.DWORD) for name in ('address', 'port', 'pid')]


class Udp6(c.Structure):
    _fields_ = [('address', c.c_ubyte * 16), ('scope', w.DWORD), ('port', w.DWORD), ('pid', w.DWORD)]


iphelper = c.WinDLL('iphlpapi')
iphelper.GetAdaptersAddresses.argtypes = [w.ULONG, w.ULONG, c.c_void_p, c.c_void_p, c.POINTER(w.ULONG)]
for api in ('GetExtendedTcpTable', 'GetExtendedUdpTable'):
    getattr(iphelper, api).argtypes = [c.c_void_p, c.POINTER(w.DWORD), w.BOOL, w.ULONG, c.c_int, w.ULONG]


def adapters():
    size = w.ULONG(16384)
    for _ in range(3):
        assert size.value <= 4 * 1024 * 1024
        memory = c.create_string_buffer(size.value)
        result = iphelper.GetAdaptersAddresses(socket.AF_UNSPEC, 0, None, memory, c.byref(size))
        if result == 111:
            continue
        assert result == 0, ('GetAdaptersAddresses', result)
        output, item = [], c.cast(memory, c.POINTER(Adapter))
        while item:
            value = item.contents
            addresses, unicast = [], value.unicast
            while unicast:
                entry = unicast.contents
                if entry.address.value and entry.dad in (3, 4):
                    raw = c.string_at(entry.address.value, entry.address.length)
                    family = struct.unpack_from('<H', raw)[0]
                    if family == socket.AF_INET and len(raw) >= 16:
                        addresses.append({'family': 4, 'address': socket.inet_ntop(socket.AF_INET, raw[4:8]), 'dad': entry.dad})
                    elif family == socket.AF_INET6 and len(raw) >= 28:
                        addresses.append({'family': 6, 'address': socket.inet_ntop(socket.AF_INET6, raw[8:24]), 'dad': entry.dad})
                unicast = entry.next
            description = c.wstring_at(value.description) if value.description else ''
            name = c.wstring_at(value.friendly_name) if value.friendly_name else ''
            virtual = any(marker in (description + ' ' + name).lower() for marker in
                ('virtual', 'hyper-v', 'wsl', 'vethernet', 'vpn', 'tap-', 'wintun', 'wireguard'))
            output.append({'if4': value.if4, 'if6': value.if6, 'kind': value.kind,
                'physical': value.kind in (6, 71) and not virtual,
                'up': value.status == 1, 'multicast': not bool(value.flags & 0x10), 'addresses': addresses})
            item = value.next
        return output
    raise RuntimeError('Adapter inventory kept changing')


def endpoints(tcp, family):
    api = iphelper.GetExtendedTcpTable if tcp else iphelper.GetExtendedUdpTable
    row = (Tcp6 if family == 6 else Tcp4) if tcp else (Udp6 if family == 6 else Udp4)
    af = socket.AF_INET6 if family == 6 else socket.AF_INET
    size = w.DWORD(0)
    result = api(None, c.byref(size), False, af, 5 if tcp else 1, 0)
    assert result in (0, 122), ('endpoint size', result)
    for _ in range(3):
        memory = c.create_string_buffer(size.value)
        result = api(memory, c.byref(size), False, af, 5 if tcp else 1, 0)
        if result == 122:
            continue
        assert result == 0, ('endpoint table', result)
        count = w.DWORD.from_buffer_copy(memory).value
        assert 4 + count * c.sizeof(row) <= len(memory)
        output = []
        for index in range(count):
            value = row.from_buffer_copy(memory, 4 + index * c.sizeof(row))
            if value.pid != os.getpid() or (tcp and value.state != 2):
                continue
            packed = bytes(value.address) if family == 6 else int(value.address).to_bytes(4, 'little')
            output.append({'family': family, 'address': socket.inet_ntop(af, packed),
                'scope': int(value.scope) if family == 6 else 0, 'port': socket.ntohs(value.port & 0xffff)})
        return output
    raise RuntimeError('Endpoint table kept changing')


def expected_v6(adapter, loopback):
    values = []
    if not adapter['if6']:
        return None
    for value in adapter['addresses']:
        if value['family'] != 6:
            continue
        address = ipaddress.IPv6Address(value['address'])
        if address.is_unspecified or address.is_multicast or address.ipv4_mapped or (address.is_loopback and not loopback):
            continue
        rank = (2 if value['dad'] == 4 else 0) + (2 if address.is_link_local or address.is_loopback else 1)
        values.append((rank, value['address']))
    return max(values, key=lambda value: value[0])[1] if values else None


def control(address, family=4, scope=0, port=47001):
    sock = socket.socket(socket.AF_INET6 if family == 6 else socket.AF_INET, socket.SOCK_STREAM)
    sock.settimeout(3)
    target = (address, port, 0, scope if ipaddress.IPv6Address(address).is_link_local else 0) if family == 6 else (address, port)
    try:
        sock.connect(target)
        return sock
    except BaseException:
        sock.close()
        raise


def refused(address, family=4, scope=0):
    try:
        with control(address, family, scope):
            raise AssertionError(('unselected listener reachable', address))
    except (ConnectionRefusedError, TimeoutError):
        return
    except OSError as error:
        assert error.winerror in (10013, 10049, 10051, 10060, 10061, 10065), error


def discover(interface, receiver_address, wanted=True):
    service = b'_airplay._tcp.local'
    encoded = b''.join(bytes([len(part)]) + part for part in service.split(b'.')) + b'\0'
    packet = struct.pack('!6H', 0, 0, 1, 0, 0, 0) + encoded + struct.pack('!HH', 12, 0x8001)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
        sender.bind((interface, 0))
        sender.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(interface))
        sender.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 255)
        sender.settimeout(.2)
        for _ in range(2):
            sender.sendto(packet, (interface if interface.startswith('127.') else '224.0.0.251', 5353))
            deadline = time.monotonic() + (.8 if wanted else .4)
            while time.monotonic() < deadline:
                try:
                    data, peer = sender.recvfrom(65536)
                except socket.timeout:
                    continue
                if peer[0] == receiver_address and service_ports(data, service):
                    assert wanted, ('mDNS response crossed unselected interface', interface)
                    return True
    assert not wanted, ('selected-interface mDNS query unanswered', interface)
    return False


def rtp_bindings(receiver, address, family, scope):
    """Exercise real authenticated setup and sockets without sending media."""
    print(f'RTP IPv{family}: pairing', flush=True)
    with control(address, family, scope) as sock, socket.socket(socket.AF_INET6 if family == 6 else socket.AF_INET, socket.SOCK_DGRAM) as timing_peer:
        timing_peer.bind((address, 0, 0, scope if ipaddress.IPv6Address(address).is_link_local else 0) if family == 6 else (address, 0))
        receiver.events()
        request(sock, '/pair-pin-start')
        pin = str(next(event[1] for event in receiver.events() if event[0] == 6)).zfill(4)
        private, server = receiver.pair(sock, pin=pin, username=f'QA:NETWORK:{family}')
        verify_pairing(sock, private, server)
        print(f'RTP IPv{family}: FairPlay setup', flush=True)
        first = bytearray(16)
        first[:7] = b'FPLY\x03\x01\x01'
        request(sock, '/fp-setup', bytes(first), content_type='application/octet-stream')
        second = bytearray(164)
        second[:7] = b'FPLY\x03\x01\x03'
        request(sock, '/fp-setup', bytes(second), content_type='application/octet-stream')
        print(f'RTP IPv{family}: timing setup', flush=True)
        setup = {'deviceID': f'QA:NETWORK:{family}', 'name': 'Disposable network scope fixture',
            'model': 'QA', 'eiv': bytes(16), 'ekey': bytes(72), 'timingProtocol': 'NTP', 'timingPort': timing_peer.getsockname()[1]}
        _, body, _ = request(sock, '/stream', plistlib.dumps(setup, fmt=plistlib.FMT_BINARY),
            method='SETUP', content_type='application/x-apple-binary-plist')
        timing = plistlib.loads(body)['timingPort']
        print(f'RTP IPv{family}: stream setup', flush=True)
        streams = {'streams': [{'type': 96, 'ct': 2, 'spf': 352, 'audioFormat': 0x40000,
            'isMedia': True, 'usingScreen': False}, {'type': 110, 'streamConnectionID': 1}]}
        _, body, _ = request(sock, '/stream', plistlib.dumps(streams, fmt=plistlib.FMT_BINARY),
            method='SETUP', content_type='application/x-apple-binary-plist')
        result = plistlib.loads(body)['streams']
        print(f'RTP IPv{family}: checking transport bindings', flush=True)
        udp_ports = {timing}
        mirror_port = None
        for stream in result:
            if stream['type'] == 96:
                udp_ports.update((stream['dataPort'], stream['controlPort']))
            elif stream['type'] == 110:
                mirror_port = stream['dataPort']
        assert 0 not in udp_ports and len(udp_ports) == 3, result
        udp = [item for item in endpoints(False, family) if item['port'] in udp_ports]
        assert {item['port'] for item in udp} == udp_ports, ('UDP ports missing', udp_ports, udp)
        assert all(item['address'] == address for item in udp), udp
        tcp = [item for item in endpoints(True, family) if item['port'] == mirror_port]
        assert tcp and all(item['address'] == address for item in tcp), tcp
        if family == 6 and ipaddress.IPv6Address(address).is_link_local:
            assert all(item['scope'] == scope for item in udp + tcp), (scope, udp, tcp)
        request(sock, '/stream', plistlib.dumps({'streams': [{'type': 96}, {'type': 110}]}, fmt=plistlib.FMT_BINARY),
            method='TEARDOWN', content_type='application/x-apple-binary-plist')
        return {'family': family, 'udp_ports': sorted(udp_ports), 'mirror_port': mirror_port,
            'all_bound_to_selected_address': True}


def wait_rearmed(receiver):
    # RTP teardown deliberately asks the renderer to reset. The production
    # worker joins/reopens HTTP before emitting its next Ready; do not race it.
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        for event in receiver.events():
            assert event[0] != 5, ('rearm failed', event)
            if event[0] == 1:
                return
        time.sleep(.02)
    raise TimeoutError('Receiver did not rearm after RTP teardown')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--address', help='An existing local LAN IPv4; no address is added.')
    parser.add_argument('--report', type=Path, default=ROOT / '.runtime/qa-network-boundaries/network-scope-protocol.json')
    args = parser.parse_args()
    report = {'passed': False, 'cycles': [], 'scope': __doc__, 'limitations': [
        'Actual adapter removal/address changes are covered by synthetic inventories; no OS network settings are changed.',
        'Loopback is a synthetic API fixture; its local mDNS reply is not claimed as LAN discovery.',
        'No RTP payload, real handset, AP multicast forwarding, audio/GPU or long weak-network session is asserted.']}
    report['loadedDependencies'] = loaded_dependency_evidence()
    inventory = adapters()
    lan = [(adapter, item['address']) for adapter in inventory if adapter['up'] and adapter['multicast'] and adapter['kind'] != 24
        for item in adapter['addresses'] if item['family'] == 4 and 0 < int(item['address'].split('.')[0]) < 224
        and not item['address'].startswith('127.')]
    if args.address:
        lan = [value for value in lan if value[1] == args.address]
    lan.sort(key=lambda value: not value[0]['physical'])
    assert lan, 'An existing local multicast LAN IPv4 is required.'
    selected_adapter, selected = lan[0]
    loop = next(adapter for adapter in inventory if any(item['address'] == '127.0.0.1' for item in adapter['addresses']))
    all_v4 = {item['address'] for adapter in inventory if adapter['up'] for item in adapter['addresses'] if item['family'] == 4}
    state_parent = args.report.resolve().parent
    state_parent.mkdir(parents=True, exist_ok=True)
    try:
        with tempfile.TemporaryDirectory(prefix='network-scope-', dir=state_parent) as state:
            receiver = Receiver(state=Path(state) / 'state')
            receiver.dll.clearcast_receiver_network_current.argtypes = []
            receiver.dll.clearcast_receiver_network_current.restype = c.c_int
            report['nativeDllSha256'] = hashlib.sha256((receiver.runtime / 'clearcast_receiver.dll').read_bytes()).hexdigest()
            try:
                for index, (adapter, address) in enumerate(((selected_adapter, selected), (loop, '127.0.0.1'), (selected_adapter, selected))):
                    print(f'Cycle {index + 1}: starting selected scope', flush=True)
                    receiver.start(address=address)
                    print(f'Cycle {index + 1}: checking listeners', flush=True)
                    assert receiver.dll.clearcast_receiver_network_current() == 1
                    tcp4 = endpoints(True, 4)
                    assert any(item['port'] == 47001 for item in tcp4), tcp4
                    assert all(item['address'] == address for item in tcp4), tcp4
                    with control(address) as sock:
                        request(sock, '*', method='OPTIONS')
                    denied = sorted(all_v4 - {address})
                    for other in denied:
                        refused(other)
                    v6 = expected_v6(adapter, address.startswith('127.'))
                    tcp6 = endpoints(True, 6)
                    if v6:
                        assert tcp6 and all(item['address'] == v6 for item in tcp6), (v6, tcp6)
                        with control(v6, 6, adapter['if6']) as sock:
                            request(sock, '*', method='OPTIONS')
                    else:
                        assert not tcp6, ('IPv4-only interface borrowed IPv6', tcp6)
                    rtp = [rtp_bindings(receiver, address, 4, 0)]
                    wait_rearmed(receiver)
                    if v6:
                        rtp.append(rtp_bindings(receiver, v6, 6, adapter['if6']))
                        wait_rearmed(receiver)
                    discovery = None if address.startswith('127.') else discover(address, address)
                    other_ifaces = sorted({item['address'] for other in inventory if other['if4'] != adapter['if4'] and other['up'] and other['multicast']
                        for item in other['addresses'] if item['family'] == 4 and not item['address'].startswith('127.')})
                    for other in other_ifaces:
                        discover(other, address, wanted=False)
                    report['cycles'].append({'cycle': index + 1, 'selected': address, 'ipv6': v6,
                        'tcp': tcp4 + tcp6, 'unselected_ipv4_rejected': len(denied), 'rtp': rtp,
                        'mdns_selected': discovery, 'mdns_other_interfaces_checked': len(other_ifaces)})
                    receiver.stop()
                    assert receiver.dll.clearcast_receiver_network_current() == 0
                    assert not endpoints(True, 4) and not endpoints(True, 6), 'Receiver listeners survived stop'
                missing = next(str(ipaddress.IPv4Address(0xcb007100 + index)) for index in range(1, 255)
                    if str(ipaddress.IPv4Address(0xcb007100 + index)) not in all_v4)
                receiver.start(expect_error=10049, address=missing)
                receiver.stop()
                assert receiver.dll.clearcast_receiver_network_current() == 0
                assert not endpoints(True, 4) and not endpoints(True, 6), 'Invalid interface fell back to listeners'
                report['unassigned_address_failed_closed'] = True
            finally:
                receiver.close()
        report['passed'] = True
    except BaseException as error:
        report['error'] = repr(error)
        raise
    finally:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        print(json.dumps(report, indent=2), flush=True)


if __name__ == '__main__':
    main()
