/* Bounded PIN proof budget shared by production handlers and no-clock tests.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#pragma once
#include <stdint.h>

static inline uint64_t clearcast_pin_failure_deadline(uint64_t now, unsigned int *failures,
    unsigned int *level, uint64_t *last_failure) {
    if (*last_failure && now >= *last_failure && now - *last_failure >= 3600000u) {
        *failures = 0;
        *level = 0;
    }
    *last_failure = now;
    if (++*failures < 5u) return 0;
    *failures = 0;
    if (*level > 5u) *level = 5u;
    uint64_t delay = 60000u << *level;
    if (delay > 1800000u) delay = 1800000u;
    if (*level < 5u) ++*level;
    return UINT64_MAX - now < delay ? UINT64_MAX : now + delay;
}

static inline void clearcast_pin_failure_reset(unsigned int *failures, unsigned int *level, uint64_t *last_failure) {
    *failures = *level = 0;
    *last_failure = 0;
}
