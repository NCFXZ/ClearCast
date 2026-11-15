/* Pure PIN enrollment handoff tests: no sockets, clocks, user state or secrets.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include "clearcast_pairing_enrollment.h"
#include "clearcast_pairing_name.h"
#include <stdio.h>

#define REQUIRE(test) do { if (!(test)) { fprintf(stderr, "PIN enrollment failure at line %d\n", __LINE__); return 1; } } while (0)

static const unsigned char peer_a[4] = {192, 0, 2, 10};
static const unsigned char peer_b[4] = {192, 0, 2, 11};
static const unsigned char peer_a6[16] = {0xfe, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 10};

static bool same_peer(const unsigned char *original, int original_length, unsigned int original_zone,
    const unsigned char *candidate, int candidate_length, unsigned int candidate_zone) {
    return original_length == candidate_length && original_zone == candidate_zone &&
        !memcmp(original, candidate, (size_t)original_length);
}

static bool dual_stack_peer(const unsigned char *original, int original_length, unsigned int original_zone,
    const unsigned char *candidate, int candidate_length, unsigned int candidate_zone) {
    if (same_peer(original, original_length, original_zone, candidate, candidate_length, candidate_zone)) return true;
    return (original_length == 4 && !original_zone && !memcmp(original, peer_a, 4) &&
            candidate_length == 16 && candidate_zone == 7 && !memcmp(candidate, peer_a6, 16)) ||
        (candidate_length == 4 && !candidate_zone && !memcmp(candidate, peer_a, 4) &&
            original_length == 16 && original_zone == 7 && !memcmp(original, peer_a6, 16));
}

int main(void) {
    char normalized[513], other_name[513];
    REQUIRE(!clearcast_pairing_normalize_name(NULL, normalized));
    REQUIRE(!clearcast_pairing_normalize_name("Fixture", NULL));
    REQUIRE(clearcast_pairing_normalize_name("", normalized) && !*normalized);
    REQUIRE(clearcast_pairing_normalize_name("\xC2\xA0\xE3\x80\x80" "Fixture Phone" "\xE2\x80\xAF", normalized));
    REQUIRE(!strcmp(normalized, "Fixture Phone"));
    REQUIRE(clearcast_pairing_normalize_name("Fixture\rPhone", normalized) && !strcmp(normalized, "Fixture Phone"));
    REQUIRE(clearcast_pairing_normalize_name("\t\r\n \xC2\xA0", normalized) && !*normalized);
    REQUIRE(!clearcast_pairing_normalize_name("Fixture\nPhone", normalized));
    REQUIRE(!clearcast_pairing_normalize_name("Fixture\tPhone", normalized));
    REQUIRE(!clearcast_pairing_normalize_name("Fixture\x7F" "Phone", normalized));
    REQUIRE(!clearcast_pairing_normalize_name("\xC0\xAF", normalized));
    REQUIRE(!clearcast_pairing_normalize_name("\xE2\x82", normalized));
    REQUIRE(!clearcast_pairing_normalize_name("\xED\xA0\x80", normalized));
    char maximum_name[514];
    memset(maximum_name, 'x', sizeof(maximum_name));
    maximum_name[512] = 0;
    REQUIRE(clearcast_pairing_normalize_name(maximum_name, normalized) && strlen(normalized) == 512);
    maximum_name[512] = 'x'; maximum_name[513] = 0;
    REQUIRE(!clearcast_pairing_normalize_name(maximum_name, normalized));
    REQUIRE(clearcast_pairing_normalize_name("\xC3\x89" "Phone A", normalized) &&
        !strcmp(normalized, "\xC3\x89" "Phone A"));
    REQUIRE(clearcast_pairing_normalize_name("\xC3\x89" "phone A", other_name) && strcmp(normalized, other_name));
    REQUIRE(clearcast_pairing_normalize_name("e\xCC\x81", normalized) &&
        clearcast_pairing_normalize_name("\xC3\xA9", other_name) && strcmp(normalized, other_name));
    REQUIRE(clearcast_pairing_normalize_name("Fixture  Phone", normalized) && !strcmp(normalized, "Fixture  Phone"));
    unsigned char key_a[32] = {1}, key_b[32] = {2}, zero[32] = {0};
    clearcast_enrollment_policy policy = {0};
    REQUIRE(!clearcast_enrollment_issue(NULL, key_a, peer_a, 4, 0, 1, 1000, same_peer));
    REQUIRE(!clearcast_enrollment_issue(&policy, NULL, peer_a, 4, 0, 1, 1000, same_peer));
    REQUIRE(!clearcast_enrollment_issue(&policy, zero, peer_a, 4, 0, 1, 1000, same_peer));
    REQUIRE(!clearcast_enrollment_issue(&policy, key_a, NULL, 4, 0, 1, 1000, same_peer));
    REQUIRE(!clearcast_enrollment_issue(&policy, key_a, zero, 4, 0, 1, 1000, same_peer));
    REQUIRE(!clearcast_enrollment_issue(&policy, key_a, peer_a, 5, 0, 1, 1000, same_peer));
    REQUIRE(!clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 3, 1, 1000, same_peer));
    REQUIRE(!clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 0, 0, 1000, same_peer));
    REQUIRE(!clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 0, 1, 1000, NULL));
    REQUIRE(clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 0, 1, 1000, same_peer));
    REQUIRE(!clearcast_enrollment_claim(&policy, key_a, peer_b, 4, 0, 1001, same_peer));
    REQUIRE(!clearcast_enrollment_claim(&policy, key_b, peer_a, 4, 0, 1001, same_peer));
    // No TCP identifier is stored: a fresh verified transport from this peer
    // can consume the completed PIN proof once, and only once.
    REQUIRE(clearcast_enrollment_claim(&policy, key_a, peer_a, 4, 0, 1002, same_peer));
    REQUIRE(!clearcast_enrollment_claim(&policy, key_a, peer_a, 4, 0, 1002, same_peer));
    REQUIRE(!clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 0, 1, 1003, same_peer));
    REQUIRE(clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 0, 2, 1004, same_peer));
    REQUIRE(!clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 0, 3, 1003, same_peer));
    REQUIRE(!clearcast_enrollment_claim(&policy, key_a, peer_a, 4, 0, 1003, same_peer));
    REQUIRE(clearcast_enrollment_claim(&policy, key_a, peer_a, 4, 0, 1005, same_peer));

    // A later peer's proof does not revoke an earlier peer's live permission.
    REQUIRE(clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 0, 3, 1100, same_peer));
    REQUIRE(clearcast_enrollment_issue(&policy, key_a, peer_b, 4, 0, 4, 1101, same_peer));
    REQUIRE(clearcast_enrollment_claim(&policy, key_a, peer_a, 4, 0, 1102, same_peer));
    REQUIRE(clearcast_enrollment_claim(&policy, key_a, peer_b, 4, 0, 1102, same_peer));

    REQUIRE(clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 0, 5, 2000, dual_stack_peer));
    REQUIRE(!clearcast_enrollment_claim(&policy, key_a, peer_a6, 16, 8, 2001, dual_stack_peer));
    REQUIRE(clearcast_enrollment_claim(&policy, key_a, peer_a6, 16, 7, 2002, dual_stack_peer));
    REQUIRE(!clearcast_enrollment_claim(&policy, key_a, peer_a, 4, 0, 2003, dual_stack_peer));
    REQUIRE(clearcast_enrollment_issue(&policy, key_a, peer_a6, 16, 7, 6, 2100, dual_stack_peer));
    REQUIRE(clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 0, 7, 2101, dual_stack_peer));
    unsigned int live = 0;
    for (unsigned int i = 0; i < CLEARCAST_ENROLLMENT_LIMIT; ++i) live += policy.tickets[i].generation != 0;
    REQUIRE(live == 1);
    REQUIRE(clearcast_enrollment_claim(&policy, key_a, peer_a6, 16, 7, 2102, dual_stack_peer));

    REQUIRE(clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 0, 8, 3000, same_peer));
    REQUIRE(clearcast_enrollment_claim(&policy, key_a, peer_a, 4, 0, 62999, same_peer));
    REQUIRE(clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 0, 9, 63000, same_peer));
    REQUIRE(!clearcast_enrollment_claim(&policy, key_a, peer_a, 4, 0, 123000, same_peer));
    REQUIRE(!clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 0, 9, 123001, same_peer));

    policy = (clearcast_enrollment_policy){0};
    for (unsigned int i = 0; i < CLEARCAST_ENROLLMENT_LIMIT; ++i) {
        unsigned char peer[4] = {192, 0, 2, (unsigned char)(i + 1)};
        REQUIRE(clearcast_enrollment_issue(&policy, key_a, peer, 4, 0, i + 1, 1000 + i, same_peer));
    }
    REQUIRE(!clearcast_enrollment_issue(&policy, key_b, peer_a, 4, 0, 17, 1100, same_peer));
    REQUIRE(!clearcast_enrollment_claim(&policy, key_b, peer_a, 4, 0, 1101, same_peer));
    REQUIRE(clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 0, 18, 1102, same_peer));
    REQUIRE(clearcast_enrollment_claim(&policy, key_a, peer_a, 4, 0, 1103, same_peer));
    REQUIRE(!clearcast_enrollment_issue(&policy, key_b, peer_a, 4, 0, 17, 1104, same_peer));
    REQUIRE(clearcast_enrollment_issue(&policy, key_b, peer_a, 4, 0, 19, 1105, same_peer));
    REQUIRE(clearcast_enrollment_claim(&policy, key_b, peer_a, 4, 0, 1106, same_peer));
    REQUIRE(clearcast_enrollment_issue(&policy, key_b, peer_a, 4, 0, 20, 62000, same_peer));
    REQUIRE(clearcast_enrollment_claim(&policy, key_b, peer_a, 4, 0, 62001, same_peer));
    REQUIRE(!clearcast_enrollment_issue(&policy, key_a, peer_a, 4, 0, 21,
        UINT64_MAX - CLEARCAST_ENROLLMENT_LIFETIME_MS + 1, same_peer));
    puts("PASS: bounded one-use PIN enrollment, key/peer binding, fresh-TCP and dual-stack handoff, expiry and stale-proof rejection");
    return 0;
}
