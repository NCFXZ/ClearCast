/* The receiver's selected adapter is also its listener and discovery boundary.
 * SPDX-License-Identifier: GPL-3.0-or-later */
#pragma once
#include <stdint.h>

struct sockaddr;
#ifdef __cplusplus
extern "C" {
#endif
int clearcast_network_scope_select(uint32_t ipv4);
void clearcast_network_scope_clear(void);
uint32_t clearcast_network_scope_ipv4(void);
/* The returned scope is the adapter index (also used by IPv6 multicast). */
int clearcast_network_scope_ipv6(unsigned char address[16], unsigned int *scope);
int clearcast_network_scope_is_current(void);
int clearcast_network_scope_prepare_unicast(int socket_fd, int ipv6);
int clearcast_network_scope_prepare_datagram(int socket_fd, int ipv6);
int clearcast_network_scope_recvfrom(int socket_fd, char *buffer, int capacity,
    struct sockaddr *from, int *from_length, int ipv6);
#ifdef __cplusplus
}
#endif

#ifdef CLEARCAST_NETWORK_SCOPE_IMPLEMENTATION
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>
#include <mswsock.h>
#include <stdlib.h>
#include <string.h>

typedef struct {
    uint32_t ipv4;
    unsigned char ipv6[16];
    ULONG if4, if6;
    ULONG64 luid;
    int has_ipv6;
} ClearCastNetworkScope;

static SRWLOCK clearcast_scope_lock = SRWLOCK_INIT;
static ClearCastNetworkScope clearcast_scope;
static LPFN_WSARECVMSG clearcast_scope_recv4, clearcast_scope_recv6;

static int clearcast_scope_v4_usable(uint32_t value, int allow_loopback) {
    const unsigned char *bytes = (const unsigned char *)&value;
    return bytes[0] && bytes[0] < 224 && (allow_loopback || bytes[0] != 127);
}

static int clearcast_scope_dad_usable(IP_DAD_STATE value) {
    return value == IpDadStatePreferred || value == IpDadStateDeprecated;
}

static int clearcast_scope_v6_rank(const IP_ADAPTER_UNICAST_ADDRESS *unicast, int loopback) {
    if (!unicast->Address.lpSockaddr || unicast->Address.iSockaddrLength < (int)sizeof(struct sockaddr_in6) ||
        unicast->Address.lpSockaddr->sa_family != AF_INET6 || !clearcast_scope_dad_usable(unicast->DadState)) return 0;
    const IN6_ADDR *value = &((const struct sockaddr_in6 *)unicast->Address.lpSockaddr)->sin6_addr;
    if (IN6_IS_ADDR_UNSPECIFIED(value) || IN6_IS_ADDR_MULTICAST(value) || IN6_IS_ADDR_V4MAPPED(value) ||
        (!loopback && IN6_IS_ADDR_LOOPBACK(value))) return 0;
    return (unicast->DadState == IpDadStatePreferred ? 2 : 0) +
        (IN6_IS_ADDR_LINKLOCAL(value) || IN6_IS_ADDR_LOOPBACK(value) ? 2 : 1);
}

/* Separate selection from OS enumeration so ambiguity and multi-adapter cases
 * exercise the production selector without altering the machine's network. */
static int clearcast_scope_from_adapters(const IP_ADAPTER_ADDRESSES *adapters,
    uint32_t requested, ClearCastNetworkScope *result) {
    memset(result, 0, sizeof(*result));
    const IP_ADAPTER_ADDRESSES *chosen = NULL;
    uint32_t chosen_v4 = 0;
    int best_score = -1;
    const int loopback = requested && ((const unsigned char *)&requested)[0] == 127;
    for (const IP_ADAPTER_ADDRESSES *adapter = adapters; adapter; adapter = adapter->Next) {
        if (adapter->OperStatus != IfOperStatusUp || !adapter->IfIndex || !adapter->Luid.Value ||
            (!loopback && (adapter->IfType == IF_TYPE_SOFTWARE_LOOPBACK || (adapter->Flags & IP_ADAPTER_NO_MULTICAST)))) continue;
        for (const IP_ADAPTER_UNICAST_ADDRESS *unicast = adapter->FirstUnicastAddress; unicast; unicast = unicast->Next) {
            if (!unicast->Address.lpSockaddr || unicast->Address.iSockaddrLength < (int)sizeof(struct sockaddr_in) ||
                unicast->Address.lpSockaddr->sa_family != AF_INET || !clearcast_scope_dad_usable(unicast->DadState)) continue;
            const uint32_t candidate = ((const struct sockaddr_in *)unicast->Address.lpSockaddr)->sin_addr.s_addr;
            if (!clearcast_scope_v4_usable(candidate, loopback) || (requested && candidate != requested)) continue;
            if (requested) {
                if (chosen && chosen != adapter) return WSAEADDRNOTAVAIL;
                chosen = adapter; chosen_v4 = candidate;
            } else {
                const int score = (adapter->FirstGatewayAddress ? 4 : 0) +
                    (adapter->IfType == IF_TYPE_ETHERNET_CSMACD || adapter->IfType == IF_TYPE_IEEE80211 ? 2 : 0);
                if (score > best_score) { chosen = adapter; chosen_v4 = candidate; best_score = score; }
            }
        }
    }
    if (!chosen) return WSAEADDRNOTAVAIL;
    result->ipv4 = chosen_v4;
    result->if4 = chosen->IfIndex; result->if6 = chosen->Ipv6IfIndex; result->luid = chosen->Luid.Value;
    int best_rank = 0;
    if (result->if6) for (const IP_ADAPTER_UNICAST_ADDRESS *unicast = chosen->FirstUnicastAddress; unicast; unicast = unicast->Next) {
        const int rank = clearcast_scope_v6_rank(unicast, loopback);
        if (rank > best_rank) {
            const struct sockaddr_in6 *value = (const struct sockaddr_in6 *)unicast->Address.lpSockaddr;
            memcpy(result->ipv6, &value->sin6_addr, 16); result->has_ipv6 = 1; best_rank = rank;
        }
    }
    return 0;
}

static IP_ADAPTER_ADDRESSES *clearcast_scope_adapters(int *error) {
    ULONG bytes = 16 * 1024;
    IP_ADAPTER_ADDRESSES *adapters = NULL;
    const ULONG flags = GAA_FLAG_INCLUDE_GATEWAYS | GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER;
    for (int attempt = 0; attempt < 3 && bytes <= 4 * 1024 * 1024; ++attempt) {
        adapters = (IP_ADAPTER_ADDRESSES *)malloc(bytes);
        if (!adapters) { *error = ERROR_NOT_ENOUGH_MEMORY; return NULL; }
        *error = GetAdaptersAddresses(AF_UNSPEC, flags, NULL, adapters, &bytes);
        if (!*error) return adapters;
        free(adapters); adapters = NULL;
        if (*error != ERROR_BUFFER_OVERFLOW) return NULL;
    }
    *error = WSAEADDRNOTAVAIL;
    return NULL;
}

int clearcast_network_scope_select(uint32_t ipv4) {
    ClearCastNetworkScope selected;
    int error = 0;
    IP_ADAPTER_ADDRESSES *adapters = clearcast_scope_adapters(&error);
    if (adapters) { error = clearcast_scope_from_adapters(adapters, ipv4, &selected); free(adapters); }
    AcquireSRWLockExclusive(&clearcast_scope_lock);
    memset(&clearcast_scope, 0, sizeof(clearcast_scope));
    clearcast_scope_recv4 = clearcast_scope_recv6 = NULL;
    if (!error) clearcast_scope = selected;
    ReleaseSRWLockExclusive(&clearcast_scope_lock);
    return error;
}

void clearcast_network_scope_clear(void) {
    AcquireSRWLockExclusive(&clearcast_scope_lock);
    memset(&clearcast_scope, 0, sizeof(clearcast_scope));
    clearcast_scope_recv4 = clearcast_scope_recv6 = NULL;
    ReleaseSRWLockExclusive(&clearcast_scope_lock);
}

uint32_t clearcast_network_scope_ipv4(void) {
    AcquireSRWLockShared(&clearcast_scope_lock);
    const uint32_t result = clearcast_scope.ipv4;
    ReleaseSRWLockShared(&clearcast_scope_lock);
    return result;
}

int clearcast_network_scope_ipv6(unsigned char address[16], unsigned int *scope) {
    if (!address || !scope) return 0;
    AcquireSRWLockShared(&clearcast_scope_lock);
    memcpy(address, clearcast_scope.ipv6, 16); *scope = clearcast_scope.has_ipv6 ? clearcast_scope.if6 : 0;
    const int result = clearcast_scope.has_ipv6;
    ReleaseSRWLockShared(&clearcast_scope_lock);
    return result;
}

static int clearcast_scope_matches_adapters(const ClearCastNetworkScope *current,
    const IP_ADAPTER_ADDRESSES *adapters) {
    if (!current->ipv4) return 0;
    int valid = 0;
    ClearCastNetworkScope selected;
    if (!clearcast_scope_from_adapters(adapters, current->ipv4, &selected) && selected.luid == current->luid &&
        selected.if4 == current->if4 && (!current->has_ipv6 || selected.if6 == current->if6)) {
        // Do not reconnect an active stream merely because an additional privacy
        // address appeared. The exact address actually in use must still exist.
        if (!current->has_ipv6) valid = 1;
        else for (const IP_ADAPTER_ADDRESSES *adapter = adapters; adapter; adapter = adapter->Next) {
            if (adapter->Luid.Value != current->luid) continue;
            for (const IP_ADAPTER_UNICAST_ADDRESS *unicast = adapter->FirstUnicastAddress; unicast; unicast = unicast->Next)
                if (clearcast_scope_v6_rank(unicast, ((const unsigned char *)&current->ipv4)[0] == 127) &&
                    !memcmp(&((const struct sockaddr_in6 *)unicast->Address.lpSockaddr)->sin6_addr, current->ipv6, 16)) valid = 1;
        }
    }
    return valid;
}

int clearcast_network_scope_is_current(void) {
    AcquireSRWLockShared(&clearcast_scope_lock);
    const ClearCastNetworkScope current = clearcast_scope;
    ReleaseSRWLockShared(&clearcast_scope_lock);
    if (!current.ipv4) return 0;
    int error = 0;
    IP_ADAPTER_ADDRESSES *adapters = clearcast_scope_adapters(&error);
    if (!adapters) return 0;
    const int valid = clearcast_scope_matches_adapters(&current, adapters);
    free(adapters);
    return valid;
}

int clearcast_network_scope_prepare_unicast(int socket_fd, int ipv6) {
    AcquireSRWLockShared(&clearcast_scope_lock);
    const ULONG index = ipv6 ? clearcast_scope.if6 : clearcast_scope.if4;
    ReleaseSRWLockShared(&clearcast_scope_lock);
    if (!index) { WSASetLastError(WSAENETDOWN); return -1; }
    /* Pin commands and discovery responses instead of using a default route. */
    const DWORD unicast_index = ipv6 ? index : htonl(index);
    return setsockopt((SOCKET)socket_fd, ipv6 ? IPPROTO_IPV6 : IPPROTO_IP,
        ipv6 ? IPV6_UNICAST_IF : IP_UNICAST_IF, (const char *)&unicast_index, sizeof(unicast_index));
}

int clearcast_network_scope_prepare_datagram(int socket_fd, int ipv6) {
    if (clearcast_network_scope_prepare_unicast(socket_fd, ipv6) == SOCKET_ERROR) return -1;
    const DWORD enabled = 1;
    if (setsockopt((SOCKET)socket_fd, ipv6 ? IPPROTO_IPV6 : IPPROTO_IP,
        ipv6 ? IPV6_PKTINFO : IP_PKTINFO, (const char *)&enabled, sizeof(enabled)) == SOCKET_ERROR) return -1;
    GUID id = WSAID_WSARECVMSG;
    LPFN_WSARECVMSG function = NULL;
    DWORD bytes = 0;
    if (WSAIoctl((SOCKET)socket_fd, SIO_GET_EXTENSION_FUNCTION_POINTER, &id, sizeof(id),
        &function, sizeof(function), &bytes, NULL, NULL) == SOCKET_ERROR) return -1;
    if (!function) { WSASetLastError(WSAEOPNOTSUPP); return -1; }
    AcquireSRWLockExclusive(&clearcast_scope_lock);
    if (ipv6) clearcast_scope_recv6 = function; else clearcast_scope_recv4 = function;
    ReleaseSRWLockExclusive(&clearcast_scope_lock);
    return 0;
}

int clearcast_network_scope_recvfrom(int socket_fd, char *buffer, int capacity,
    struct sockaddr *from, int *from_length, int ipv6) {
    if (!buffer || capacity <= 0 || !from || !from_length) { WSASetLastError(WSAEINVAL); return -1; }
    AcquireSRWLockShared(&clearcast_scope_lock);
    LPFN_WSARECVMSG function = ipv6 ? clearcast_scope_recv6 : clearcast_scope_recv4;
    ULONG expected = ipv6 ? clearcast_scope.if6 : clearcast_scope.if4;
    ReleaseSRWLockShared(&clearcast_scope_lock);
    if (!function || !expected) { WSASetLastError(WSAENETDOWN); return -1; }
    WSABUF data = {(ULONG)capacity, buffer};
    char control[WSA_CMSG_SPACE(sizeof(IN6_PKTINFO))];
    WSAMSG message;
    memset(&message, 0, sizeof(message));
    message.name = from; message.namelen = *from_length;
    message.lpBuffers = &data; message.dwBufferCount = 1;
    message.Control.buf = control; message.Control.len = sizeof(control);
    DWORD received = 0;
    if (function((SOCKET)socket_fd, &message, &received, NULL, NULL) == SOCKET_ERROR) return -1;
    *from_length = message.namelen;
    if (message.dwFlags & MSG_CTRUNC) return 0;
    for (WSACMSGHDR *item = WSA_CMSG_FIRSTHDR(&message); item; item = WSA_CMSG_NXTHDR(&message, item)) {
        if (!ipv6 && item->cmsg_level == IPPROTO_IP && item->cmsg_type == IP_PKTINFO && item->cmsg_len >= WSA_CMSG_LEN(sizeof(IN_PKTINFO))) {
            const IN_PKTINFO *info = (const IN_PKTINFO *)WSA_CMSG_DATA(item);
            return info->ipi_ifindex == expected ? (int)received : 0;
        }
        if (ipv6 && item->cmsg_level == IPPROTO_IPV6 && item->cmsg_type == IPV6_PKTINFO && item->cmsg_len >= WSA_CMSG_LEN(sizeof(IN6_PKTINFO))) {
            const IN6_PKTINFO *info = (const IN6_PKTINFO *)WSA_CMSG_DATA(item);
            return info->ipi6_ifindex == expected ? (int)received : 0;
        }
    }
    return 0; // Missing kernel interface metadata is not authority to reply.
}
#endif
