"""LAN media addresses shared by synthetic fixtures, without network changes."""
import ipaddress
import socket


def media_fixture_address(explicit=None):
    candidates = [explicit] if explicit else list(dict.fromkeys(
        result[4][0] for result in socket.getaddrinfo(socket.gethostname(), 0, socket.AF_INET, socket.SOCK_STREAM)))
    for value in candidates:
        address = ipaddress.IPv4Address(value)
        first = int(address) >> 24
        if first and first != 127 and first < 224:
            return value
    raise RuntimeError("An existing local unicast LAN IPv4 address is required; no network settings are modified.")
