// SPDX-License-Identifier: GPL-3.0-or-later
#include <stdio.h>
#include <stdlib.h>
#include "clearcast_session_authorization.h"
static unsigned checks;
#define CHECK(value) do { ++checks; if (!(value)) { fprintf(stderr, "FAIL %u line %d: %s\n", checks, __LINE__, #value); exit(1); } } while (0)
int main(void) {
    clearcast_session_policy policy = {0};
    clearcast_session_link verified = {0}, http = {0}, other = {0}, owner = {0};
    unsigned char key[32] = {1}, different_key[32] = {2};
    unsigned char peer[4] = {192,0,2,10}, different_peer[4] = {192,0,2,11};
    CHECK(!clearcast_session_authorize(&policy,&http,NULL,peer,4,0,"invented",1));
    CHECK(clearcast_session_authorize(&policy,&verified,key,peer,4,0,"session",1));
    CHECK(!clearcast_session_authorize(&policy,&http,NULL,peer,4,0,"invented",1));
    CHECK(!clearcast_session_authorize(&policy,&other,different_key,peer,4,0,"session",1));
    CHECK(!clearcast_session_authorize(&policy,&other,NULL,different_peer,4,0,"session",1));
    CHECK(!clearcast_session_authorize(&policy,&other,NULL,peer,4,1,"session",1));
    CHECK(clearcast_session_authorize(&policy,&http,NULL,peer,4,0,"session",1));
    CHECK(!clearcast_session_authorize(&policy,&http,NULL,peer,4,0,"substituted",1));
    CHECK(!clearcast_session_authorize(&policy,&http,different_key,peer,4,0,"session",1));
    owner = verified; clearcast_session_lookup(&policy,owner)->active = true;
    CHECK(clearcast_session_same_owner(&policy,owner,http,"session"));
    CHECK(!clearcast_session_same_owner(&policy,owner,http,"other"));
    clearcast_session_close(&policy,&verified,2);
    clearcast_session_close(&policy,&http,2);
    CHECK(clearcast_session_authorize(&policy,&http,NULL,peer,4,0,"session",900000));
    CHECK(clearcast_session_same_owner(&policy,owner,http,"session"));
    clearcast_session_end(&policy,&owner);
    CHECK(clearcast_session_authorize(&policy,&http,NULL,peer,4,0,"session",900001));
    CHECK(clearcast_session_authorize(&policy,&other,key,peer,4,0,"session",900001));
    CHECK(clearcast_session_authorize(&policy,&other,key,peer,4,0,"replacement",900001));
    CHECK(!clearcast_session_authorize(&policy,&http,NULL,peer,4,0,"replacement",900001));
    clearcast_session_close(&policy,&other,900001);
    clearcast_session_close(&policy,&http,900002);
    CHECK(clearcast_session_authorize(&policy,&http,NULL,peer,4,0,"session",960002));
    clearcast_session_close(&policy,&http,960003);
    CHECK(!clearcast_session_authorize(&policy,&http,NULL,peer,4,0,"session",1020004));
    CHECK(clearcast_session_authorize(&policy,&verified,key,peer,4,0,"new",1020004));
    owner = verified; clearcast_session_lookup(&policy,owner)->active = true;
    clearcast_session_link stale = verified;
    clearcast_session_close(&policy,&verified,1020005);
    clearcast_session_end(&policy,&owner);
    CHECK(!clearcast_session_authorize(&policy,&http,NULL,peer,4,0,"new",1020005));
    CHECK(clearcast_session_authorize(&policy,&verified,key,peer,4,0,"fresh",1020006));
    CHECK(!clearcast_session_lookup(&policy,stale));
    CHECK(!clearcast_session_authorize(&policy,&stale,NULL,peer,4,0,"fresh",1020006));
    CHECK(!clearcast_session_authorize(&policy,&other,key,peer,4,0,"bad\rheader",1020006));
    char long_id[130]; memset(long_id,'x',sizeof(long_id)); long_id[129]=0;
    CHECK(!clearcast_session_authorize(&policy,&other,key,peer,4,0,long_id,1020006));
    long_id[128]=0;
    CHECK(clearcast_session_authorize(&policy,&other,key,peer,4,0,long_id,1020006));
    clearcast_session_policy bounded = {0};
    clearcast_session_link links[CLEARCAST_SESSION_LIMIT + 1] = {{0}};
    char session[32];
    for (unsigned i=0; i<CLEARCAST_SESSION_LIMIT; ++i) {
        snprintf(session,sizeof(session),"bounded-%u",i);
        CHECK(clearcast_session_authorize(&bounded,&links[i],key,peer,4,0,session,1));
    }
    CHECK(!clearcast_session_authorize(&bounded,&links[CLEARCAST_SESSION_LIMIT],key,peer,4,0,"overflow",1));
    clearcast_session_close(&bounded,&links[0],1);
    CHECK(clearcast_session_authorize(&bounded,&links[0],key,peer,4,0,"reused",60002));
    clearcast_session_close(&bounded,&links[1],60003);
    CHECK(!clearcast_session_authorize(&bounded,&links[CLEARCAST_SESSION_LIMIT],NULL,peer,4,0,"new-item",60004));
    CHECK(clearcast_session_authorize(&bounded,&links[CLEARCAST_SESSION_LIMIT],key,peer,4,0,"new-item",60004));
    clearcast_session_link reclaimed = {0};
    CHECK(!clearcast_session_authorize(&bounded,&reclaimed,NULL,peer,4,0,"bounded-1",60004));
    clearcast_session_policy rapid = {0};
    clearcast_session_link player = {0}, intruder = {0};
    for (unsigned i=0; i<CLEARCAST_SESSION_LIMIT*4; ++i) {
        snprintf(session,sizeof(session),"rapid-%u",i);
        CHECK(clearcast_session_authorize(&rapid,&player,key,peer,4,0,session,1));
        owner = player; clearcast_session_lookup(&rapid,owner)->active = true;
        const uint64_t generation = rapid.generation;
        CHECK(!clearcast_session_authorize(&rapid,&intruder,NULL,peer,4,0,"invented",1));
        CHECK(rapid.generation == generation && clearcast_session_lookup(&rapid,owner)->active);
        clearcast_session_end(&rapid,&owner);
    }
    unsigned char v6[16] = {0xfe,0x80};
    clearcast_session_policy scoped = {0}; verified=(clearcast_session_link){0}; http=(clearcast_session_link){0};
    CHECK(clearcast_session_authorize(&scoped,&verified,key,v6,16,7,"scoped",1));
    CHECK(!clearcast_session_authorize(&scoped,&http,NULL,v6,16,8,"scoped",1));
    CHECK(clearcast_session_authorize(&scoped,&http,NULL,v6,16,7,"scoped",1));
    clearcast_session_policy family = {0};
    clearcast_session_link a = {0}, b = {0};
    CHECK(clearcast_session_authorize(&family,&a,key,peer,4,0,"family",1));
    CHECK(clearcast_session_authorize(&family,&b,key,v6,16,7,"family",1));
    owner = a; clearcast_session_lookup(&family,a)->active = true;
    clearcast_session_link old_owner = a;
    clearcast_session_end(&family,&owner);
    owner = b; clearcast_session_lookup(&family,b)->active = true;
    clearcast_session_close(&family,&a,2);
    clearcast_session_end(&family,&old_owner);
    CHECK(clearcast_session_lookup(&family,b)->active);
    CHECK(clearcast_session_authorize(&family,&a,key,peer,4,0,"family",3));
    clearcast_session_close(&family,&a,4);
    clearcast_session_close(&family,&b,4);
    clearcast_session_end(&family,&owner);
    CHECK(!clearcast_session_authorize(&family,&a,NULL,peer,4,0,"family",5));
    CHECK(!clearcast_session_authorize(&family,&b,NULL,v6,16,7,"family",5));
    printf("%u session authorization checks passed\n",checks);
    return 0;
}
