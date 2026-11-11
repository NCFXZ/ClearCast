/* Adapter inventory and kernel packet metadata tests; no receiver ports. */
#define CLEARCAST_NETWORK_SCOPE_IMPLEMENTATION
#include "clearcast_network_scope.h"
#include <stdio.h>

#define CHECK(value) do { if (!(value)) { fprintf(stderr, "line %d: %s (error %d)\n", __LINE__, #value, WSAGetLastError()); return 1; } } while (0)

typedef struct {
    IP_ADAPTER_ADDRESSES adapter;
    IP_ADAPTER_UNICAST_ADDRESS unicast[4];
    struct sockaddr_in v4;
    struct sockaddr_in6 v6[3];
} FakeAdapter;

static uint32_t ipv4(const char *text) {
    struct in_addr value;
    if (inet_pton(AF_INET, text, &value) != 1) abort();
    return value.s_addr;
}

static void adapter_init(FakeAdapter *fake, const char *address, ULONG index) {
    memset(fake, 0, sizeof(*fake));
    fake->adapter.Length = sizeof(fake->adapter);
    fake->adapter.IfIndex = fake->adapter.Ipv6IfIndex = index;
    fake->adapter.Luid.Value = index + 100;
    fake->adapter.OperStatus = IfOperStatusUp;
    fake->adapter.IfType = IF_TYPE_IEEE80211;
    fake->adapter.FirstUnicastAddress = &fake->unicast[0];
    fake->unicast[0].Length = sizeof(fake->unicast[0]);
    fake->unicast[0].DadState = IpDadStatePreferred;
    fake->unicast[0].Address.lpSockaddr = (struct sockaddr *)&fake->v4;
    fake->unicast[0].Address.iSockaddrLength = sizeof(fake->v4);
    fake->v4.sin_family = AF_INET;
    fake->v4.sin_addr.s_addr = ipv4(address);
}

static void adapter_v6(FakeAdapter *fake, int index, const char *address, IP_DAD_STATE state) {
    fake->unicast[index].Length = sizeof(fake->unicast[index]);
    fake->unicast[index].DadState = state;
    fake->unicast[index].Address.lpSockaddr = (struct sockaddr *)&fake->v6[index - 1];
    fake->unicast[index].Address.iSockaddrLength = sizeof(fake->v6[index - 1]);
    fake->v6[index - 1].sin6_family = AF_INET6;
    if (inet_pton(AF_INET6, address, &fake->v6[index - 1].sin6_addr) != 1) abort();
    fake->unicast[index - 1].Next = &fake->unicast[index];
}

static int adapter_tests(void) {
    FakeAdapter first, second;
    ClearCastNetworkScope scope;
    adapter_init(&first, "192.168.10.2", 11);
    adapter_init(&second, "10.0.0.2", 22);
    first.adapter.Next = &second.adapter;
    adapter_v6(&first, 1, "fe80::11", IpDadStatePreferred);
    adapter_v6(&second, 1, "2001:db8::22", IpDadStatePreferred);
    adapter_v6(&second, 2, "fe80::22", IpDadStatePreferred);
    CHECK(clearcast_scope_from_adapters(&first.adapter, ipv4("10.0.0.2"), &scope) == 0);
    CHECK(scope.ipv4 == ipv4("10.0.0.2") && scope.if4 == 22 && scope.if6 == 22 && scope.luid == 122);
    CHECK(scope.has_ipv6 && !memcmp(scope.ipv6, &second.v6[1].sin6_addr, 16));
    CHECK(clearcast_scope_matches_adapters(&scope, &first.adapter));
    second.adapter.Luid.Value++;
    CHECK(!clearcast_scope_matches_adapters(&scope, &first.adapter));
    second.adapter.Luid.Value--;
    second.adapter.IfIndex++;
    CHECK(!clearcast_scope_matches_adapters(&scope, &first.adapter));
    second.adapter.IfIndex--;
    second.unicast[2].DadState = IpDadStateTentative;
    CHECK(!clearcast_scope_matches_adapters(&scope, &first.adapter));
    CHECK(clearcast_scope_from_adapters(&first.adapter, ipv4("10.0.0.2"), &scope) == 0);
    CHECK(scope.has_ipv6 && !memcmp(scope.ipv6, &second.v6[0].sin6_addr, 16));
    second.unicast[2].DadState = IpDadStatePreferred;
    CHECK(clearcast_scope_matches_adapters(&scope, &first.adapter)); /* New IPv6 does not invalidate one in use. */
    second.unicast[2].DadState = IpDadStateTentative;
    second.unicast[1].DadState = IpDadStateDuplicate;
    CHECK(clearcast_scope_from_adapters(&first.adapter, ipv4("10.0.0.2"), &scope) == 0);
    CHECK(!scope.has_ipv6); /* IPv4-only stays usable, never borrows another NIC's IPv6. */
    second.unicast[2].DadState = IpDadStatePreferred;
    CHECK(clearcast_scope_matches_adapters(&scope, &first.adapter)); /* Newly available IPv6 does not interrupt IPv4. */
    second.adapter.Ipv6IfIndex++;
    CHECK(clearcast_scope_matches_adapters(&scope, &first.adapter));
    second.adapter.Ipv6IfIndex--;
    second.unicast[2].DadState = IpDadStateTentative;
    second.adapter.OperStatus = IfOperStatusDown;
    CHECK(clearcast_scope_from_adapters(&first.adapter, ipv4("10.0.0.2"), &scope) == WSAEADDRNOTAVAIL);
    CHECK(!scope.ipv4 && !scope.has_ipv6);
    second.adapter.OperStatus = IfOperStatusUp;
    second.unicast[0].DadState = IpDadStateTentative;
    CHECK(clearcast_scope_from_adapters(&first.adapter, ipv4("10.0.0.2"), &scope) == WSAEADDRNOTAVAIL);
    second.unicast[0].DadState = IpDadStatePreferred;
    second.v4.sin_addr.s_addr = first.v4.sin_addr.s_addr;
    CHECK(clearcast_scope_from_adapters(&first.adapter, first.v4.sin_addr.s_addr, &scope) == WSAEADDRNOTAVAIL);
    second.v4.sin_addr.s_addr = ipv4("10.0.0.2");
    CHECK(clearcast_scope_from_adapters(&first.adapter, ipv4("203.0.113.2"), &scope) == WSAEADDRNOTAVAIL);
    second.adapter.FirstGatewayAddress = (IP_ADAPTER_GATEWAY_ADDRESS *)&second;
    CHECK(clearcast_scope_from_adapters(&first.adapter, 0, &scope) == 0 && scope.if4 == 22);
    second.adapter.Flags = IP_ADAPTER_NO_MULTICAST;
    CHECK(clearcast_scope_from_adapters(&first.adapter, 0, &scope) == 0 && scope.if4 == 11);
    first.adapter.OperStatus = IfOperStatusDown;
    CHECK(clearcast_scope_from_adapters(&first.adapter, 0, &scope) == WSAEADDRNOTAVAIL);

    adapter_init(&first, "127.0.0.1", 1);
    first.adapter.IfType = IF_TYPE_SOFTWARE_LOOPBACK;
    first.adapter.Flags = IP_ADAPTER_NO_MULTICAST;
    adapter_v6(&first, 1, "::1", IpDadStatePreferred);
    CHECK(clearcast_scope_from_adapters(&first.adapter, 0, &scope) == WSAEADDRNOTAVAIL);
    CHECK(clearcast_scope_from_adapters(&first.adapter, ipv4("127.0.0.1"), &scope) == 0);
    CHECK(scope.ipv4 == ipv4("127.0.0.1") && scope.has_ipv6 && scope.if6 == 1);

    adapter_init(&first, "192.168.10.2", 11);
    adapter_v6(&first, 1, "::1", IpDadStatePreferred);
    adapter_v6(&first, 2, "ff02::fb", IpDadStatePreferred);
    adapter_v6(&first, 3, "::ffff:192.168.10.2", IpDadStatePreferred);
    CHECK(clearcast_scope_from_adapters(&first.adapter, first.v4.sin_addr.s_addr, &scope) == 0 && !scope.has_ipv6);
    first.v4.sin_addr.s_addr = ipv4("224.0.0.1");
    CHECK(clearcast_scope_from_adapters(&first.adapter, 0, &scope) == WSAEADDRNOTAVAIL);
    CHECK(clearcast_scope_from_adapters(NULL, 0, &scope) == WSAEADDRNOTAVAIL);
    return 0;
}

static int packet_family, packet_index, packet_missing, packet_truncated;
static INT WSAAPI fake_recvmsg(SOCKET socket, LPWSAMSG message, LPDWORD received,
    LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion) {
    (void)socket; (void)overlapped; (void)completion;
    *received = 7;
    message->dwFlags = packet_truncated ? MSG_CTRUNC : 0;
    if (packet_missing) { message->Control.len = 0; return 0; }
    const size_t size = packet_family ? sizeof(IN6_PKTINFO) : sizeof(IN_PKTINFO);
    memset(message->Control.buf, 0, message->Control.len);
    message->Control.len = WSA_CMSG_SPACE(size);
    WSACMSGHDR *item = WSA_CMSG_FIRSTHDR(message);
    item->cmsg_level = packet_family ? IPPROTO_IPV6 : IPPROTO_IP;
    item->cmsg_type = packet_family ? IPV6_PKTINFO : IP_PKTINFO;
    item->cmsg_len = WSA_CMSG_LEN(size);
    if (packet_family) ((IN6_PKTINFO *)WSA_CMSG_DATA(item))->ipi6_ifindex = packet_index;
    else ((IN_PKTINFO *)WSA_CMSG_DATA(item))->ipi_ifindex = packet_index;
    return 0;
}

static int packet_tests(void) {
    char buffer[64];
    struct sockaddr_storage from;
    int length = sizeof(from);
    clearcast_network_scope_clear();
    CHECK(clearcast_network_scope_recvfrom(0, buffer, sizeof(buffer), (struct sockaddr *)&from, &length, 0) == -1);
    clearcast_scope.if4 = 11; clearcast_scope.if6 = 22;
    clearcast_scope_recv4 = clearcast_scope_recv6 = fake_recvmsg;
    for (packet_family = 0; packet_family <= 1; ++packet_family) {
        packet_index = packet_family ? 22 : 11;
        CHECK(clearcast_network_scope_recvfrom(0, buffer, sizeof(buffer), (struct sockaddr *)&from, &length, packet_family) == 7);
        ++packet_index;
        CHECK(clearcast_network_scope_recvfrom(0, buffer, sizeof(buffer), (struct sockaddr *)&from, &length, packet_family) == 0);
        --packet_index; packet_missing = 1;
        CHECK(clearcast_network_scope_recvfrom(0, buffer, sizeof(buffer), (struct sockaddr *)&from, &length, packet_family) == 0);
        packet_missing = 0; packet_truncated = 1;
        CHECK(clearcast_network_scope_recvfrom(0, buffer, sizeof(buffer), (struct sockaddr *)&from, &length, packet_family) == 0);
        packet_truncated = 0;
        packet_family = !packet_family;
        const int wrong_family = packet_family;
        packet_family = !packet_family;
        CHECK(clearcast_network_scope_recvfrom(0, buffer, sizeof(buffer), (struct sockaddr *)&from, &length, wrong_family) == 0);
    }
    clearcast_network_scope_clear();
    return 0;
}

int main(void) {
    WSADATA data;
    CHECK(WSAStartup(MAKEWORD(2, 2), &data) == 0);
    CHECK(adapter_tests() == 0);
    CHECK(packet_tests() == 0);
    CHECK(clearcast_network_scope_select(ipv4("127.0.0.1")) == 0);
    CHECK(clearcast_network_scope_ipv4() == ipv4("127.0.0.1"));
    CHECK(clearcast_network_scope_is_current());
    for (int ipv6 = 0; ipv6 <= 1; ++ipv6) {
        SOCKET client = socket(ipv6 ? AF_INET6 : AF_INET, SOCK_STREAM, IPPROTO_TCP);
        CHECK(client != INVALID_SOCKET);
        CHECK(clearcast_network_scope_prepare_unicast((int)client, ipv6) == 0);
        DWORD selected = 0; int length = sizeof(selected);
        CHECK(getsockopt(client, ipv6 ? IPPROTO_IPV6 : IPPROTO_IP,
            ipv6 ? IPV6_UNICAST_IF : IP_UNICAST_IF, (char *)&selected, &length) == 0);
        // Winsock accepts IPv4 indices in network order, but returns host order.
        CHECK(selected == (ipv6 ? clearcast_scope.if6 : clearcast_scope.if4));
        closesocket(client);
    }
    CHECK(clearcast_network_scope_select(ipv4("203.0.113.19")) != 0);
    CHECK(!clearcast_network_scope_ipv4() && !clearcast_network_scope_is_current());
    CHECK(clearcast_network_scope_prepare_unicast(0, 0) == -1 && WSAGetLastError() == WSAENETDOWN);
    CHECK(clearcast_network_scope_prepare_unicast(0, 1) == -1 && WSAGetLastError() == WSAENETDOWN);
    unsigned char v6[16]; unsigned int index = 999;
    CHECK(!clearcast_network_scope_ipv6(v6, &index) && !index);
    clearcast_network_scope_clear();
    WSACleanup();
    puts("Adapter scope and mDNS incoming interface tests passed.");
    return 0;
}
