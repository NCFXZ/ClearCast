"""Check this receiver's AirPlay/RAOP discovery with disposable local state.

Only records counts and service ports, never other devices' names or packets.
SPDX-License-Identifier: GPL-3.0-or-later
"""
from receiver_test_support import ROOT, Receiver

import argparse
import json
import re
import socket
import struct
import tempfile
import time


def dns_name(packet, offset):
    labels, seen, end = [], set(), None
    while True:
        if offset >= len(packet) or offset in seen or len(seen) > 128:
            raise ValueError("Invalid DNS name")
        seen.add(offset)
        size = packet[offset]
        if size & 0xC0 == 0xC0:
            if offset + 1 >= len(packet):
                raise ValueError("Truncated DNS pointer")
            end = end or offset + 2
            offset = ((size & 63) << 8) | packet[offset + 1]
        elif size == 0:
            return b".".join(labels).lower(), end or offset + 1
        else:
            if size > 63 or offset + 1 + size > len(packet):
                raise ValueError("Invalid DNS label")
            labels.append(packet[offset + 1:offset + 1 + size])
            offset += size + 1


def service_ports(packet, service):
    if len(packet) < 12:
        return []
    _, flags, questions, answers, authority, additional = struct.unpack_from("!6H", packet)
    if not flags & 0x8000:
        return []
    offset, ports = 12, []
    try:
        for _ in range(questions):
            _, offset = dns_name(packet, offset)
            offset += 4
        for _ in range(answers + authority + additional):
            owner, offset = dns_name(packet, offset)
            kind, _, ttl, length = struct.unpack_from("!HHIH", packet, offset)
            offset += 10
            if offset + length > len(packet):
                return []
            if kind == 33 and ttl > 0 and length >= 6 and owner.endswith(service):
                port = struct.unpack_from("!H", packet, offset + 4)[0]
                if port in (47000, 47001, 47002):
                    ports.append(port)
            offset += length
    except (ValueError, struct.error):
        return []
    return ports


def query(interface, service, timeout=2):
    encoded = b"".join(bytes([len(part)]) + part for part in service.split(b".")) + b"\0"
    packet = struct.pack("!6H", 0, 0, 1, 0, 0, 0) + encoded + struct.pack("!HH", 12, 0x8001)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
        sender.bind((interface, 0))
        sender.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(interface))
        sender.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 255)
        sender.settimeout(.3)
        sender.sendto(packet, ("224.0.0.251", 5353))
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                response, peer = sender.recvfrom(65536)
            except socket.timeout:
                continue
            if peer[0] != interface:
                continue
            ports = service_ports(response, service)
            if ports:
                return ports
    return []


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=str, default=str(ROOT / ".runtime/qa-video-crash/discovery-results.json"))
    args = parser.parse_args()
    report = {"passed": False, "cycles": [], "scope": "Local multicast query/QU reply and receiver restart; no claim about phone/AP multicast forwarding."}
    from pathlib import Path
    try:
        with tempfile.TemporaryDirectory(prefix="discovery-", dir=ROOT / ".runtime") as directory:
            receiver = Receiver(state=Path(directory) / "state")
            try:
                for cycle in range(3):
                    receiver.start()
                    log = (receiver.state / "receiver.log").read_text(encoding="utf-8")
                    interface = re.findall(r"ready address=([0-9.]+)", log)[-1]
                    assert interface != "0.0.0.0", "No active discovery interface"
                    results = []
                    for _ in range(3):
                        for service in (b"_airplay._tcp.local", b"_raop._tcp.local"):
                            ports = query(interface, service)
                            results.append({"service": service.decode(), "ports": ports})
                            assert ports, "Receiver failed to answer its service query"
                    report["cycles"].append({"cycle": cycle + 1, "queries": results})
                    receiver.stop()
            finally:
                receiver.close()
        report["passed"] = True
    except Exception as error:
        report["error"] = str(error)
        raise
    finally:
        path = Path(args.report)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(report, indent=2), flush=True)


if __name__ == "__main__":
    main()
