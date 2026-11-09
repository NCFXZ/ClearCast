/* Bounded, read-only Windows neighbor identity for a pending PIN handoff.
 * This only recognizes a local device's IPv4/link-local IPv6 address pair;
 * the normal SRP proof, claim, expiry and cooldown checks remain mandatory.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#ifndef CLEARCAST_PAIRING_PEER_H
#define CLEARCAST_PAIRING_PEER_H

#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <stdbool.h>
#include <string.h>

static bool clearcast_pin_neighbor_valid(const MIB_IPNET_ROW2 *row) {
    if (!row->InterfaceLuid.Value || !row->InterfaceIndex || row->PhysicalAddressLength != 6 ||
        (row->PhysicalAddress[0] & 1) || row->IsRouter || row->IsUnreachable) return false;
    unsigned char nonzero = 0;
    for (int i = 0; i < 6; ++i) nonzero |= row->PhysicalAddress[i];
    return nonzero && (row->State == NlnsReachable || row->State == NlnsStale ||
        row->State == NlnsDelay || row->State == NlnsProbe || row->State == NlnsPermanent);
}

static bool clearcast_pin_neighbor_address(const MIB_IPNET_ROW2 *row,
    const unsigned char *address, int length) {
    if (!clearcast_pin_neighbor_valid(row)) return false;
    if (length == 4) return row->Address.si_family == AF_INET &&
        !memcmp(&row->Address.Ipv4.sin_addr, address, 4);
    return row->Address.si_family == AF_INET6 &&
        !memcmp(&row->Address.Ipv6.sin6_addr, address, 16);
}

static bool clearcast_pin_peer_matches(const unsigned char *original, int original_length,
    unsigned int original_zone, const unsigned char *candidate, int candidate_length,
    unsigned int candidate_zone) {
    if (!original || !candidate || (original_length != 4 && original_length != 16) ||
        (candidate_length != 4 && candidate_length != 16)) return false;
    if (original_length == candidate_length) return original_zone == candidate_zone &&
        !memcmp(original, candidate, original_length);

    const unsigned char *ipv6 = original_length == 16 ? original : candidate;
    unsigned int zone = original_length == 16 ? original_zone : candidate_zone;
    // A scoped, on-link IPv6 neighbor is required. No routed/global-IP aliases.
    if (!zone || ipv6[0] != 0xfe || (ipv6[1] & 0xc0) != 0x80) return false;
    MIB_IPNET_TABLE2 *table = NULL;
    if (GetIpNetTable2(AF_UNSPEC, &table) != NO_ERROR || !table) {
        if (table) FreeMibTable(table);
        return false;
    }
    const MIB_IPNET_ROW2 *first = NULL, *second = NULL;
    bool ambiguous = false;
    if (table->NumEntries <= 4096) {
        for (ULONG i = 0; i < table->NumEntries; ++i) {
            const MIB_IPNET_ROW2 *row = &table->Table[i];
            if (clearcast_pin_neighbor_address(row, original, original_length)) {
                if (first) { ambiguous = true; break; }
                first = row;
            }
            if (clearcast_pin_neighbor_address(row, candidate, candidate_length)) {
                if (second) { ambiguous = true; break; }
                second = row;
            }
        }
    }
    const MIB_IPNET_ROW2 *v6_row = original_length == 16 ? first : second;
    bool matches = !ambiguous && first && second && v6_row->InterfaceIndex == zone &&
        (!v6_row->Address.Ipv6.sin6_scope_id || v6_row->Address.Ipv6.sin6_scope_id == zone) &&
        first->InterfaceIndex == second->InterfaceIndex &&
        first->InterfaceLuid.Value == second->InterfaceLuid.Value &&
        !memcmp(first->PhysicalAddress, second->PhysicalAddress, 6);
    FreeMibTable(table);
    return matches;
}
#endif
