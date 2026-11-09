/* Independent, no-socket production-handler fixture. Neighbor injection exists
 * only in this test DLL, which is never copied to a receiver/app payload.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <stdlib.h>
#include <string.h>

static int fixture_mode, fixture_tables;
static const unsigned char fixture_v4[4] = {192, 0, 2, 10};
static const unsigned char fixture_v6[16] = {0xfe, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x12, 0x34};
static NETIO_STATUS NETIOAPI_API_ fixture_get_neighbors(ADDRESS_FAMILY family, PMIB_IPNET_TABLE2 *output) {
    if (fixture_mode == 16) { *output = NULL; return NO_ERROR; }
    MIB_IPNET_TABLE2 *table = calloc(1, sizeof(*table) + 3 * sizeof(MIB_IPNET_ROW2));
    if (!table) return ERROR_NOT_ENOUGH_MEMORY;
    ++fixture_tables;
    *output = table;
    table->NumEntries = 2;
    for (int i = 0; i < 2; ++i) {
        table->Table[i].InterfaceIndex = 7;
        table->Table[i].InterfaceLuid.Value = 0x123400;
        table->Table[i].PhysicalAddressLength = 6;
        memcpy(table->Table[i].PhysicalAddress, "\x02\x11\x22\x33\x44\x55", 6);
        table->Table[i].State = NlnsReachable;
    }
    table->Table[0].Address.Ipv4.sin_family = AF_INET;
    memcpy(&table->Table[0].Address.Ipv4.sin_addr, fixture_v4, 4);
    table->Table[1].Address.Ipv6.sin6_family = AF_INET6;
    table->Table[1].Address.Ipv6.sin6_scope_id = 7;
    memcpy(&table->Table[1].Address.Ipv6.sin6_addr, fixture_v6, 16);
    MIB_IPNET_ROW2 *v4 = &table->Table[0], *v6 = &table->Table[1];
    switch (fixture_mode) {
    case 1: v4->PhysicalAddress[5] ^= 1; break;
    case 2: v4->InterfaceLuid.Value ^= 1; break;
    case 3: v4->InterfaceIndex = 8; break;
    case 4: memset(v4->PhysicalAddress, 0, 6); break;
    case 5: v4->PhysicalAddress[0] |= 1; break;
    case 6: v4->IsRouter = 1; break;
    case 7: v4->IsUnreachable = 1; break;
    case 8: v4->State = NlnsIncomplete; break;
    case 9: return ERROR_ACCESS_DENIED; // Even an allocated error result is freed.
    case 10: v4->Address.Ipv4.sin_addr.s_addr = 0; break;
    case 11: v6->Address.Ipv6.sin6_addr.u.Byte[15] ^= 1; break;
    case 12: table->Table[2] = *v4; table->NumEntries = 3; break;
    case 13: case 14:
        table->Table[2] = *v6; table->NumEntries = 3;
        table->Table[2].InterfaceIndex = 8;
        table->Table[2].InterfaceLuid.Value = 0x999900;
        if (fixture_mode == 14) table->Table[2].State = NlnsUnreachable;
        break;
    case 15: v6->Address.Ipv6.sin6_scope_id = 8; break;
    case 17: table->NumEntries = 4097; break;
    case 18: v4->State = v6->State = NlnsStale; break;
    case 19: v4->State = v6->State = NlnsDelay; break;
    case 20: v4->State = v6->State = NlnsProbe; break;
    case 21: v4->State = v6->State = NlnsPermanent; break;
    case 22: v4->State = NlnsUnreachable; break;
    case 23: v4->InterfaceLuid.Value = 0; break;
    case 24: v4->InterfaceIndex = 0; break;
    case 25: v4->PhysicalAddressLength = 0; break;
    case 26: v4->PhysicalAddressLength = 32; break;
    default: break;
    }
    return NO_ERROR;
}
static VOID NETIOAPI_API_ fixture_free_neighbors(PVOID memory) { --fixture_tables; free(memory); }
#define GetIpNetTable2 fixture_get_neighbors
#define FreeMibTable fixture_free_neighbors
#include "clearcast_pairing_peer.h"
#include "raop.c"
#undef GetIpNetTable2
#undef FreeMibTable

static raop_t *fixture_raop;
static raop_conn_t *fixture_connections[16];
static int fixture_pin, fixture_generation;
static int fixture_feedback[3];
static char fixture_registered[45];
static void fixture_log(void *cls, int level, const char *message) { (void)cls; (void)level; (void)message; }
static bool fixture_check(void *cls, const char *key) { return !strcmp(fixture_registered, key); }
static void fixture_register(void *cls, const char *id, const char *key, const char *name) {
    if (strlen(key) == 44) memcpy(fixture_registered, key, 45);
}

void clearcast_media_verified(uint64_t id, const unsigned char *peer, int length, unsigned int zone) {}
void clearcast_media_closed(uint64_t id) {}
void clearcast_media_remote(uint64_t id, const char *dacp, const char *remote) {}
bool clearcast_media_peer_verified(const unsigned char *peer, int length, unsigned int zone) { return false; }
void clearcast_media_retain_peer(const unsigned char *peer, int length, unsigned int zone, bool retain) {}
bool clearcast_direct_video_uri_allowed(const char *uri) { return false; }
uint64_t clearcast_diagnostic_connected(const char *remote) { static uint64_t id; return ++id; }
void clearcast_diagnostic_closed(uint64_t id) {}
void clearcast_diagnostic_request(uint64_t id, const char *method, const char *path) {}
void clearcast_diagnostic_response(uint64_t id, int status, int generation, int feedback) {}
int clearcast_begin_pairing(const char *pin) { fixture_pin = atoi(pin); return ++fixture_generation; }
void clearcast_pairing_result(int status, int generation, int reason) {
    fixture_feedback[0] = status;
    fixture_feedback[1] = generation;
    fixture_feedback[2] = reason;
}
void clearcast_media_audio_setup(uint64_t id, const unsigned char *peer, int length, unsigned int zone,
    int screen, const char *dacp, const char *remote) {}
void clearcast_media_audio_parameter(uint64_t id, const char *type, const void *buffer, int length) {}
void clearcast_media_audio_stop(uint64_t id, int ended) {}

__declspec(dllexport) void clearcast_test_peer_close(int handle) {
    if (handle > 0 && handle <= 16 && fixture_connections[handle - 1]) {
        conn_destroy(fixture_connections[handle - 1]);
        fixture_connections[handle - 1] = NULL;
    }
}
__declspec(dllexport) void clearcast_test_peer_destroy(void) {
    for (int i = 1; i <= 16; ++i) clearcast_test_peer_close(i);
    if (fixture_raop) {
        pairing_destroy(fixture_raop->pairing);
        logger_destroy(fixture_raop->logger);
        free(fixture_raop);
        fixture_raop = NULL;
    }
}
__declspec(dllexport) int clearcast_test_peer_reset(const char *keyfile) {
    clearcast_test_peer_destroy();
    fixture_raop = calloc(1, sizeof(*fixture_raop));
    if (!fixture_raop) return -1;
    fixture_raop->logger = logger_init();
    logger_set_callback(fixture_raop->logger, fixture_log, NULL);
    logger_set_level(fixture_raop->logger, LOGGER_ERR);
    int created = 0;
    fixture_raop->pairing = pairing_init_generate("AA:BB:CC:DD:EE:01", keyfile, &created);
    fixture_raop->callbacks.check_register = fixture_check;
    fixture_raop->callbacks.register_client = fixture_register;
    fixture_raop->use_pin = true;
    fixture_registered[0] = 0;
    fixture_mode = fixture_generation = fixture_pin = 0;
    memset(fixture_feedback, 0, sizeof(fixture_feedback));
    return fixture_raop->pairing ? 0 : -1;
}
__declspec(dllexport) void clearcast_test_peer_mode(int mode) { fixture_mode = mode; }
__declspec(dllexport) int clearcast_test_peer_tables(void) { return fixture_tables; }
__declspec(dllexport) int clearcast_test_peer_pin(void) { return fixture_pin; }
__declspec(dllexport) int clearcast_test_peer_generation(void) { return fixture_generation; }
__declspec(dllexport) int clearcast_test_peer_feedback(int field) {
    return field >= 0 && field < 3 ? fixture_feedback[field] : -1;
}
__declspec(dllexport) uint64_t clearcast_test_peer_expiry(void) { return fixture_raop->pending_pin_expires_at; }
__declspec(dllexport) int clearcast_test_peer_match(const unsigned char *a, int alen, unsigned int azone,
    const unsigned char *b, int blen, unsigned int bzone) {
    return clearcast_pin_peer_matches(a, alen, azone, b, blen, bzone);
}
__declspec(dllexport) int clearcast_test_peer_open(const unsigned char *address, int length, unsigned int zone) {
    if (!fixture_raop || !address || (length != 4 && length != 16)) return 0;
    for (int i = 0; i < 16; ++i) if (!fixture_connections[i]) {
        fixture_connections[i] = conn_init(fixture_raop, (unsigned char *)address, length,
            (unsigned char *)address, length, zone);
        return fixture_connections[i] ? i + 1 : 0;
    }
    return 0;
}
__declspec(dllexport) int clearcast_test_peer_request(int handle, const char *raw, int length,
    char *output, int capacity) {
    if (handle < 1 || handle > 16 || !fixture_connections[handle - 1] || !raw ||
        length < 1 || length > 65536 || !output || capacity < 1) return -1;
    http_request_t *request = http_request_init();
    http_request_add_data(request, raw, length);
    if (!http_request_is_complete(request) || http_request_has_error(request)) {
        http_request_destroy(request); return -1;
    }
    raop_handler_t handler = NULL;
    const char *path = http_request_get_url(request);
    if (!strcmp(path, "/pair-pin-start")) handler = raop_handler_pairpinstart;
    else if (!strcmp(path, "/pair-setup-pin")) handler = raop_handler_pairsetup_pin;
    else if (!strcmp(path, "/pair-verify")) handler = raop_handler_pairverify;
    if (!handler) { http_request_destroy(request); return -1; }
    http_response_t *response = http_response_create();
    http_response_init(response, "RTSP/1.0", 200, "OK");
    char *body = NULL;
    int body_length = 0;
    handler(fixture_connections[handle - 1], request, response, &body, &body_length);
    http_response_finish(response, body, body_length);
    int size = 0;
    const char *data = http_response_get_data(response, &size);
    int result = size <= capacity ? size : -1;
    if (result >= 0) memcpy(output, data, size);
    free(body);
    http_response_destroy(response);
    http_request_destroy(request);
    return result;
}
