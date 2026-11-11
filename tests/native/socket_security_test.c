/* Exercise the production Winsock binding policy on ephemeral ports.
 * SPDX-License-Identifier: GPL-3.0-or-later */
#include "compat.h"
#include "netutils.h"
#include "clearcast_network_scope.h"
#include <stdio.h>

static int checks;
#define CHECK(test) do { ++checks; if (!(test)) { fprintf(stderr, "FAIL line %d: %s (%d)\n", __LINE__, #test, WSAGetLastError()); return 1; } } while (0)

static int family_test(int ipv6) {
    unsigned short port = 0;
    int listener = netutils_init_socket(&port, ipv6, 0);
    CHECK(listener != -1 && port != 0);
    int exclusive = 0, length = sizeof(exclusive);
    CHECK(getsockopt(listener, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, (char *)&exclusive, &length) == 0 && exclusive);
    CHECK(listen(listener, 1) == 0);
    int attacker = socket(ipv6 ? AF_INET6 : AF_INET, SOCK_STREAM, IPPROTO_TCP);
    CHECK(attacker != -1);
    int reuse = 1;
    CHECK(setsockopt(attacker, SOL_SOCKET, SO_REUSEADDR, (const char *)&reuse, sizeof(reuse)) == 0);
    struct sockaddr_storage address = {0};
    int address_size = sizeof(address);
    CHECK(getsockname(listener, (struct sockaddr *)&address, &address_size) == 0);
    CHECK(bind(attacker, (struct sockaddr *)&address, address_size) == SOCKET_ERROR);
    CHECK(WSAGetLastError() == WSAEACCES || WSAGetLastError() == WSAEADDRINUSE);
    closesocket(attacker);
    closesocket(listener);
    unsigned short same_port = port;
    listener = netutils_init_socket(&same_port, ipv6, 0);
    CHECK(listener != -1 && same_port == port);
    closesocket(listener);
    return 0;
}

int main(void) {
    CHECK(netutils_init() == 0);
    CHECK(clearcast_network_scope_select(inet_addr("127.0.0.1")) == 0);
    if (family_test(0) || family_test(1)) return 1;
    clearcast_network_scope_clear();
    netutils_cleanup();
    printf("PASS %d socket security checks\n", checks);
    return 0;
}
