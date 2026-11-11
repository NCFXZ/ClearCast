/* Pure production PIN budget tests: no sockets, clocks, user state or secrets.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include "clearcast_pairing_policy.h"
#include <stdio.h>

#define REQUIRE(test) do { if (!(test)) { fprintf(stderr, "PIN policy failure at line %d\n", __LINE__); return 1; } } while (0)

int main(void) {
    unsigned int failures = 0, level = 0;
    uint64_t last_failure = 0, now = 1000;
    const uint64_t delays[] = {60000, 120000, 240000, 480000, 960000, 1800000, 1800000, 1800000};
    for (unsigned int group = 0; group < sizeof(delays) / sizeof(delays[0]); ++group) {
        for (unsigned int attempt = 0; attempt < 4; ++attempt)
            REQUIRE(clearcast_pin_failure_deadline(++now, &failures, &level, &last_failure) == 0);
        uint64_t deadline = clearcast_pin_failure_deadline(++now, &failures, &level, &last_failure);
        REQUIRE(deadline - now == delays[group]);
        REQUIRE(failures == 0 && level <= 5);
        now = deadline + 1;
    }
    clearcast_pin_failure_reset(&failures, &level, &last_failure);
    REQUIRE(failures == 0 && level == 0 && last_failure == 0);
    for (int attempt = 0; attempt < 4; ++attempt)
        REQUIRE(clearcast_pin_failure_deadline(++now, &failures, &level, &last_failure) == 0);
    uint64_t deadline = clearcast_pin_failure_deadline(++now, &failures, &level, &last_failure);
    REQUIRE(deadline - now == 60000);
    now += 3600000;
    REQUIRE(clearcast_pin_failure_deadline(now, &failures, &level, &last_failure) == 0);
    REQUIRE(failures == 1 && level == 0);
    failures = 4; level = 5; last_failure = UINT64_MAX - 10;
    REQUIRE(clearcast_pin_failure_deadline(UINT64_MAX - 5, &failures, &level, &last_failure) == UINT64_MAX);
    puts("PASS: escalating bounded PIN failure budget, reset, idle recovery and saturated deadlines");
    return 0;
}
