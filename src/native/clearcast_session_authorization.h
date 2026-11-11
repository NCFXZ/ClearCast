// SPDX-License-Identifier: GPL-3.0-or-later
#ifndef CLEARCAST_SESSION_AUTHORIZATION_H
#define CLEARCAST_SESSION_AUTHORIZATION_H

#include <stdbool.h>
#include <stdint.h>
#include <string.h>

// A legacy AirPlay session ID is a bearer link between verified and HTTP
// transports, not a replacement for pair-verify or encrypted request integrity.
#define CLEARCAST_SESSION_LIMIT 16
#define CLEARCAST_SESSION_HANDOFF_MS 60000
typedef struct { unsigned slot; uint64_t generation; } clearcast_session_link;
typedef struct {
    unsigned char key[32], peer[16];
    unsigned length, zone, references;
    uint64_t generation, closed;
    bool active;
    char session[129];
} clearcast_session_grant;
typedef struct {
    clearcast_session_grant grants[CLEARCAST_SESSION_LIMIT];
    uint64_t generation;
} clearcast_session_policy;

static bool clearcast_session_valid(const char *session) {
    if (!session || !*session) return false;
    for (unsigned i = 0; i <= 128; ++i) {
        unsigned char c = (unsigned char)session[i];
        if (!c) return i <= 128;
        if (c <= 32 || c >= 127) return false;
    }
    return false;
}
static clearcast_session_grant *clearcast_session_lookup(clearcast_session_policy *policy,
    clearcast_session_link link) {
    if (!link.slot || link.slot > CLEARCAST_SESSION_LIMIT) return NULL;
    clearcast_session_grant *grant = &policy->grants[link.slot - 1];
    return grant->generation && grant->generation == link.generation ? grant : NULL;
}
static bool clearcast_session_alive(const clearcast_session_grant *grant, uint64_t now) {
    return grant->generation && (grant->references || grant->active ||
        (now >= grant->closed && now - grant->closed <= CLEARCAST_SESSION_HANDOFF_MS));
}
static bool clearcast_session_same_peer(const clearcast_session_grant *grant,
    const unsigned char *peer, unsigned length, unsigned zone) {
    return grant->length == length && grant->zone == zone && !memcmp(grant->peer, peer, length);
}
static bool clearcast_session_authorize(clearcast_session_policy *policy, clearcast_session_link *link,
    const unsigned char *verified_key, const unsigned char *peer, unsigned length, unsigned zone,
    const char *session, uint64_t now) {
    if (!peer || (length != 4 && length != 16) || !clearcast_session_valid(session)) return false;
    clearcast_session_grant *grant = clearcast_session_lookup(policy, *link);
    if (grant) {
        if (!clearcast_session_same_peer(grant, peer, length, zone) ||
            (verified_key && memcmp(grant->key, verified_key, 32))) return false;
        if (!strcmp(grant->session, session)) return true;
        if (!verified_key || grant->active) return false;
        // A verified transport may begin a fresh item session after its old
        // owner ended; a bearer-only transport cannot mint a replacement.
        if (grant->references) --grant->references;
        grant->closed = now;
        memset(link, 0, sizeof(*link));
    }
    // An attached transport cannot silently change identity/session after its
    // grant is revoked. It must establish a new verified transport instead.
    if (link->slot) return false;
    clearcast_session_grant *available = NULL, *reclaim = NULL, *match = NULL;
    for (unsigned i = 0; i < CLEARCAST_SESSION_LIMIT; ++i) {
        grant = &policy->grants[i];
        if (!clearcast_session_alive(grant, now)) {
            memset(grant, 0, sizeof(*grant));
            if (!available) available = grant;
            continue;
        }
        if (!grant->references && !grant->active && (!reclaim || grant->closed < reclaim->closed)) reclaim = grant;
        if (strcmp(grant->session, session)) continue;
        // Even a second paired device must not seize another device's token.
        if (verified_key && memcmp(grant->key, verified_key, 32)) return false;
        if (clearcast_session_same_peer(grant, peer, length, zone)) match = grant;
    }
    if (!match) {
        // Rapid item changes may exceed the short reconnect cache. Only a
        // verified transport can reclaim the oldest idle, unowned slot.
        if (!available && verified_key) available = reclaim;
        if (!verified_key || !available) return false;
        match = available;
        memset(match, 0, sizeof(*match));
        memcpy(match->key, verified_key, 32);
        memcpy(match->peer, peer, length);
        match->length = length; match->zone = zone;
        memcpy(match->session, session, strlen(session) + 1);
        if (!++policy->generation) ++policy->generation;
        match->generation = policy->generation;
    }
    ++match->references;
    link->slot = (unsigned)(match - policy->grants) + 1;
    link->generation = match->generation;
    return true;
}
static bool clearcast_session_same_owner(clearcast_session_policy *policy,
    clearcast_session_link owner, clearcast_session_link candidate, const char *session) {
    clearcast_session_grant *a = clearcast_session_lookup(policy, owner);
    clearcast_session_grant *b = clearcast_session_lookup(policy, candidate);
    return a && b && session && !strcmp(a->session, session) && !strcmp(b->session, session) &&
        !memcmp(a->key, b->key, 32);
}
static void clearcast_session_close(clearcast_session_policy *policy, clearcast_session_link *link,
    uint64_t now) {
    clearcast_session_grant *grant = clearcast_session_lookup(policy, *link);
    if (grant && grant->references) { --grant->references; grant->closed = now; }
    memset(link, 0, sizeof(*link));
}
static void clearcast_session_end(clearcast_session_policy *policy, clearcast_session_link *owner) {
    clearcast_session_grant *grant = clearcast_session_lookup(policy, *owner);
    if (grant) {
        unsigned char key[32]; char session[129];
        memcpy(key, grant->key, sizeof(key));
        memcpy(session, grant->session, sizeof(session));
        grant->active = false;
        // One device/session can use both IPv4 and IPv6 transports. Ending its
        // owner also revokes idle sibling grants, without clearing a different
        // grant that already became active during an ownership handoff.
        for (unsigned i = 0; i < CLEARCAST_SESSION_LIMIT; ++i) {
            grant = &policy->grants[i];
            if (grant->generation && !grant->references && !grant->active &&
                !memcmp(grant->key, key, sizeof(key)) && !strcmp(grant->session, session))
                memset(grant, 0, sizeof(*grant));
        }
    }
    memset(owner, 0, sizeof(*owner));
}

#endif
