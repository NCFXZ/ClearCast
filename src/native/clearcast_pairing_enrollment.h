/* One-use, peer-bound permission to enroll a name after a successful PIN proof.
 * Callers verify the sender key/name and serialize access; this helper has no
 * clock, operating-system calls, allocation or authority to bypass pair-verify.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#ifndef CLEARCAST_PAIRING_ENROLLMENT_H
#define CLEARCAST_PAIRING_ENROLLMENT_H

#include <stdbool.h>
#include <stdint.h>
#include <string.h>

#define CLEARCAST_ENROLLMENT_LIMIT 16
#define CLEARCAST_ENROLLMENT_LIFETIME_MS 60000u

typedef bool (*clearcast_enrollment_peer_match)(const unsigned char *, int, unsigned int,
    const unsigned char *, int, unsigned int);

typedef struct {
    unsigned char key[32], peer[16];
    int length;
    unsigned int zone;
    uint64_t generation, issued, expires;
} clearcast_enrollment_ticket;

typedef struct {
    clearcast_enrollment_ticket tickets[CLEARCAST_ENROLLMENT_LIMIT];
    uint64_t latest_generation, observed_at;
} clearcast_enrollment_policy;

static inline bool clearcast_enrollment_nonzero(const unsigned char *bytes, unsigned int length) {
    if (!bytes) return false;
    unsigned char value = 0;
    for (unsigned int index = 0; index < length; ++index) value |= bytes[index];
    return value != 0;
}

static inline bool clearcast_enrollment_input_valid(const unsigned char *key,
    const unsigned char *peer, int length, unsigned int zone) {
    return (length == 4 || length == 16) && (length != 4 || !zone) &&
        clearcast_enrollment_nonzero(key, 32) && clearcast_enrollment_nonzero(peer, (unsigned int)length);
}

static inline void clearcast_enrollment_expire(clearcast_enrollment_policy *policy, uint64_t now) {
    for (unsigned int index = 0; index < CLEARCAST_ENROLLMENT_LIMIT; ++index) {
        clearcast_enrollment_ticket *ticket = &policy->tickets[index];
        if (ticket->generation && (now < ticket->issued || now >= ticket->expires))
            memset(ticket, 0, sizeof(*ticket));
    }
}

static inline bool clearcast_enrollment_issue(clearcast_enrollment_policy *policy,
    const unsigned char key[32], const unsigned char *peer, int length, unsigned int zone,
    uint64_t generation, uint64_t now, clearcast_enrollment_peer_match matches) {
    if (!policy || !matches || !generation ||
        !clearcast_enrollment_input_valid(key, peer, length, zone) ||
        now > UINT64_MAX - CLEARCAST_ENROLLMENT_LIFETIME_MS || now < policy->observed_at)
        return false;
    policy->observed_at = now;
    clearcast_enrollment_expire(policy, now);
    if (generation <= policy->latest_generation) return false;
    // Consume the generation even if capacity is unavailable. An older or
    // retried proof must not resurrect a consumed, expired or rejected ticket.
    policy->latest_generation = generation;
    clearcast_enrollment_ticket *available = NULL;
    for (unsigned int index = 0; index < CLEARCAST_ENROLLMENT_LIMIT; ++index) {
        clearcast_enrollment_ticket *ticket = &policy->tickets[index];
        if (!ticket->generation) {
            if (!available) available = ticket;
        } else if (!memcmp(ticket->key, key, 32) &&
            matches(ticket->peer, ticket->length, ticket->zone, peer, length, zone)) {
            // A newer successful proof replaces this peer's prior permission.
            // Remove aliases too if the supplied matcher now recognizes them.
            memset(ticket, 0, sizeof(*ticket));
            if (!available) available = ticket;
        }
    }
    if (!available) return false;
    memcpy(available->key, key, 32);
    memcpy(available->peer, peer, (size_t)length);
    available->length = length;
    available->zone = zone;
    available->generation = generation;
    available->issued = now;
    available->expires = now + CLEARCAST_ENROLLMENT_LIFETIME_MS;
    return true;
}

static inline bool clearcast_enrollment_claim(clearcast_enrollment_policy *policy,
    const unsigned char key[32], const unsigned char *peer, int length, unsigned int zone,
    uint64_t now, clearcast_enrollment_peer_match matches) {
    if (!policy || !matches || !clearcast_enrollment_input_valid(key, peer, length, zone) ||
        now < policy->observed_at) return false;
    policy->observed_at = now;
    clearcast_enrollment_expire(policy, now);
    bool claimed = false;
    for (unsigned int index = 0; index < CLEARCAST_ENROLLMENT_LIMIT; ++index) {
        clearcast_enrollment_ticket *ticket = &policy->tickets[index];
        if (ticket->generation && !memcmp(ticket->key, key, 32) &&
            matches(ticket->peer, ticket->length, ticket->zone, peer, length, zone)) {
            // Consume every matching alias so this proof cannot enroll a
            // second name through another transport or address family.
            memset(ticket, 0, sizeof(*ticket));
            claimed = true;
        }
    }
    return claimed;
}

#endif
