/* Actual DACP discovery with fake sockets; never opens network ports.
 * SPDX-License-Identifier: GPL-3.0-or-later */
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <glib.h>
#include <cstdio>
#include <cstring>
#include <vector>
#include "clearcast_network_scope.h"

struct Reply {
    bool selected_interface;
    sockaddr_storage source;
    std::vector<unsigned char> bytes;
};
struct Fixture {
    bool ipv4 = true, ipv6 = true, fail_prepare = false, fail_multicast = false, fail_bind = false;
    unsigned int index = 47;
    int family = 0, created = 0, closed = 0, prepared = -1, bound = 0, received = 0, raw_received = 0;
    int unicast_prepared = -1, nonblocking = 0, connected = 0, written = 0;
    int multicast_level = 0, multicast_option = 0;
    DWORD multicast_value = 0;
    size_t next_reply = 0;
    std::vector<sockaddr_storage> destinations;
    std::vector<Reply> replies;
};
static Fixture fixture;
static SOCKET test_socket(int family, int type, int protocol) {
    fixture.family = family; ++fixture.created;
    return (type == SOCK_DGRAM && protocol == IPPROTO_UDP) ||
        (type == SOCK_STREAM && protocol == IPPROTO_TCP) ? SOCKET(123) : INVALID_SOCKET;
}
static int test_close(SOCKET) { ++fixture.closed; return 0; }
static int test_bind(SOCKET, const sockaddr*, int) { ++fixture.bound; return fixture.fail_bind ? SOCKET_ERROR : 0; }
static int test_setsockopt(SOCKET, int level, int option, const char* value, int length) {
    fixture.multicast_level = level; fixture.multicast_option = option;
    if (length != sizeof(DWORD)) return SOCKET_ERROR;
    memcpy(&fixture.multicast_value, value, sizeof(DWORD));
    return fixture.fail_multicast ? SOCKET_ERROR : 0;
}
static int test_sendto(SOCKET, const char*, int length, int, const sockaddr* address, int size) {
    sockaddr_storage destination = {};
    if (size > int(sizeof(destination))) return SOCKET_ERROR;
    memcpy(&destination, address, size); fixture.destinations.push_back(destination); return length;
}
static int test_select(int, fd_set*, fd_set*, fd_set*, const timeval*) {
    return fixture.next_reply < fixture.replies.size() ? 1 : 0;
}
static uint32_t test_scope_ipv4() { return fixture.ipv4 ? htonl(0xc0000202) : 0; }
static int test_scope_ipv6(unsigned char address[16], unsigned int* index) {
    memset(address, 0, 16); address[0] = 0x20; address[1] = 1; address[15] = 2;
    *index = fixture.ipv6 ? fixture.index : 0; return fixture.ipv6;
}
static int test_prepare_datagram(int, int ipv6) { fixture.prepared = ipv6; return fixture.fail_prepare ? -1 : 0; }
static int test_prepare_unicast(int, int ipv6) {
    fixture.unicast_prepared = ipv6;
    return fixture.fail_prepare || !(ipv6 ? fixture.ipv6 : fixture.ipv4) ? -1 : 0;
}
static int test_nonblocking(SOCKET, long, u_long*) { ++fixture.nonblocking; return SOCKET_ERROR; }
static int test_connect(SOCKET, const sockaddr*, int) { ++fixture.connected; return SOCKET_ERROR; }
static int test_send(SOCKET, const char*, int, int) { ++fixture.written; return SOCKET_ERROR; }
static int test_socket_error() { return WSAENETDOWN; }
static int test_scope_recvfrom(int, char* bytes, int capacity, sockaddr* source, int* size, int) {
    ++fixture.received;
    const auto& reply = fixture.replies[fixture.next_reply++];
    if (!reply.selected_interface) return 0;
    if (reply.bytes.size() > size_t(capacity)) return SOCKET_ERROR;
    *size = reply.source.ss_family == AF_INET ? sizeof(sockaddr_in) : sizeof(sockaddr_in6);
    memcpy(source, &reply.source, *size); memcpy(bytes, reply.bytes.data(), reply.bytes.size());
    return int(reply.bytes.size());
}
static int test_raw_recvfrom(SOCKET, char*, int, int, sockaddr*, int*) {
    ++fixture.raw_received; fixture.next_reply = fixture.replies.size(); return SOCKET_ERROR;
}

#define socket(...) test_socket(__VA_ARGS__)
#define closesocket(...) test_close(__VA_ARGS__)
#define bind(...) test_bind(__VA_ARGS__)
#define setsockopt(...) test_setsockopt(__VA_ARGS__)
#define sendto(...) test_sendto(__VA_ARGS__)
#define select(...) test_select(__VA_ARGS__)
#define recvfrom(...) test_raw_recvfrom(__VA_ARGS__)
#define ioctlsocket(...) test_nonblocking(__VA_ARGS__)
#define connect(...) test_connect(__VA_ARGS__)
#define send(...) test_send(__VA_ARGS__)
#define WSAGetLastError(...) test_socket_error(__VA_ARGS__)
#define clearcast_network_scope_ipv4(...) test_scope_ipv4(__VA_ARGS__)
#define clearcast_network_scope_ipv6(...) test_scope_ipv6(__VA_ARGS__)
#define clearcast_network_scope_prepare_datagram(...) test_prepare_datagram(__VA_ARGS__)
#define clearcast_network_scope_prepare_unicast(...) test_prepare_unicast(__VA_ARGS__)
#define clearcast_network_scope_recvfrom(...) test_scope_recvfrom(__VA_ARGS__)
#include "clearcast_media_audio.h"
#undef socket
#undef closesocket
#undef bind
#undef setsockopt
#undef sendto
#undef select
#undef recvfrom
#undef ioctlsocket
#undef connect
#undef send
#undef WSAGetLastError
#undef clearcast_network_scope_ipv4
#undef clearcast_network_scope_ipv6
#undef clearcast_network_scope_prepare_datagram
#undef clearcast_network_scope_prepare_unicast
#undef clearcast_network_scope_recvfrom

namespace {
static bool clearcast_mirror_media_active() { return false; }
static bool clearcast_direct_video_snapshot(bool&, int64_t&, int64_t&) { return false; }
static bool clearcast_direct_video_controls(uint64_t&, int&, int64_t&, int64_t&, bool&) { return false; }
static clearcast_diagnostics::Info clearcast_video_diagnostics(bool) { return {}; }
}
extern "C" void clearcast_direct_video_stop() {}
extern "C" bool clearcast_direct_video_is_active() { return false; }

static unsigned checks = 0, failures = 0;
static void check(bool value, const char* message) {
    ++checks; if (!value) { ++failures; fprintf(stderr, "FAIL: %s\n", message); }
}
static std::vector<unsigned char> answer() {
    std::vector<unsigned char> bytes(12, 0); bytes[2] = 0x84; bytes[7] = 1;
    const std::string service = "itunes_ctrl_abcd._dacp._tcp.local";
    for (size_t offset = 0; offset < service.size();) {
        size_t end = service.find('.', offset); if (end == std::string::npos) end = service.size();
        bytes.push_back(static_cast<unsigned char>(end - offset));
        bytes.insert(bytes.end(), service.begin() + offset, service.begin() + end); offset = end + 1;
    }
    bytes.insert(bytes.end(), {0,0,33,0,1,0,0,0,120,0,9,0,0,0,0,0x0e,0x69,1,'x',0});
    return bytes;
}
static void reset() { fixture = Fixture(); clearcast_media::stopping = false; }
static unsigned short discover(const clearcast_media::Peer& peer) {
    const auto port = clearcast_media::discover(peer, "ABCD");
    check(fixture.created == 1 && fixture.closed == 1, "all discovery paths close their socket");
    return port;
}
int main() {
    using clearcast_media::Peer;
    Peer ipv4, ipv6; const unsigned char address4[] = {192,0,2,10};
    const unsigned char address6[] = {0x20,1,0x0d,0xb8,0,0,0,0,0,0,0,0,0,0,0,10};
    check(ipv4.assign(address4, 4, 0) && ipv6.assign(address6, 16, 0), "fixture peers are valid");

    reset();
    fixture.replies.push_back({false, ipv4.address(5353), answer()});
    fixture.replies.push_back({true, ipv4.address(5354), answer()});
    fixture.replies.push_back({true, ipv4.address(5353), answer()});
    check(discover(ipv4) == 3689, "selected-interface sender SRV response resolves DACP port");
    check(fixture.prepared == 0 && fixture.bound == 1, "IPv4 prepares selected-interface send and receive before binding");
    check(fixture.multicast_level == IPPROTO_IP && fixture.multicast_option == IP_MULTICAST_IF &&
        fixture.multicast_value == test_scope_ipv4(), "IPv4 multicast uses the selected address");
    check(fixture.received == 3 && fixture.raw_received == 0, "discovery uses interface-filtered receive and rejects another source port");
    check(fixture.destinations.size() == 2 && fixture.destinations[0].ss_family == AF_INET &&
        reinterpret_cast<const sockaddr_in*>(&fixture.destinations[1])->sin_addr.s_addr == htonl(0xe00000fb),
        "IPv4 sends direct and scoped multicast discovery");

    reset();
    check(discover(ipv6) == 0, "missing IPv6 reply leaves control unavailable");
    check(fixture.prepared == 1 && fixture.multicast_level == IPPROTO_IPV6 &&
        fixture.multicast_option == IPV6_MULTICAST_IF && fixture.multicast_value == fixture.index,
        "IPv6 uses the selected adapter index");
    check(fixture.destinations.size() == 2 && fixture.destinations[1].ss_family == AF_INET6 &&
        reinterpret_cast<const sockaddr_in6*>(&fixture.destinations[1])->sin6_scope_id == fixture.index &&
        reinterpret_cast<const sockaddr_in6*>(&fixture.destinations[1])->sin6_addr.s6_addr[0] == 0xff,
        "global IPv6 sender still gets multicast on the selected interface");

    for (int family = 0; family < 2; ++family) {
        const Peer& peer = family ? ipv6 : ipv4;
        reset(); if (family) fixture.ipv6 = false; else fixture.ipv4 = false;
        check(discover(peer) == 0 && fixture.destinations.empty(), "missing selected scope sends no discovery packet");
        reset(); fixture.fail_prepare = true;
        check(discover(peer) == 0 && fixture.destinations.empty(), "failed interface preparation never falls back to default routes");
        reset(); fixture.fail_multicast = true;
        check(discover(peer) == 0 && fixture.destinations.empty(), "failed multicast pinning sends no discovery packet");
        reset(); fixture.fail_bind = true;
        check(discover(peer) == 0 && fixture.destinations.empty(), "failed bind sends no discovery packet");
    }
    reset(); fixture.index = 0;
    check(discover(ipv6) == 0 && fixture.destinations.empty(), "successful address lookup without an IPv6 index sends no discovery packet");
    reset(); ipv6.zone = fixture.index + 1;
    check(discover(ipv6) == 0 && fixture.destinations.empty(), "different IPv6 sender zone cannot switch the selected interface");
    ipv6.zone = 0;
    for (int family = 0; family < 2; ++family) {
        const Peer& peer = family ? ipv6 : ipv4;
        reset();
        check(clearcast_media::request(peer, 3689, "12345", "play") == WSAENETDOWN &&
            fixture.unicast_prepared == family && fixture.nonblocking == 1 && !fixture.connected && !fixture.written && fixture.closed == 1,
            "TCP pins the selected family before attempting nonblocking mode or sending credentials");
        reset(); fixture.fail_prepare = true;
        check(clearcast_media::request(peer, 3689, "12345", "play") == WSAENETDOWN &&
            fixture.unicast_prepared == family && !fixture.nonblocking && !fixture.connected && !fixture.written && fixture.closed == 1,
            "failed TCP interface preparation closes the socket before configuration, connect or send");
        reset(); if (family) fixture.ipv6 = false; else fixture.ipv4 = false;
        check(clearcast_media::request(peer, 3689, "12345", "play") == WSAENETDOWN &&
            !fixture.nonblocking && !fixture.connected && !fixture.written && fixture.closed == 1,
            "TCP without a selected interface never falls back to a default route");
    }
    printf("%s %u DACP scope checks (fake sockets, no network ports)\n", failures ? "FAIL" : "PASS", checks);
    return failures ? 1 : 0;
}
