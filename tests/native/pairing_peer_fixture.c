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
#define httpd_count_connection_type fixture_count_connection_type
#define httpd_get_connection_by_type fixture_get_connection_by_type
#define httpd_set_connection_type fixture_set_connection_type
#define httpd_nohold fixture_nohold
#define raop_rtp_stop fixture_audio_transport_stop
#define raop_rtp_mirror_stop fixture_mirror_transport_stop
#define raop_ntp_stop fixture_timing_transport_stop
#ifdef CLEARCAST_HTTP_INPUT_FIXTURE
#include "crypto.h"
#include "pairing.h"
// This no-I/O handler fixture injects one verified public identity. The
// independent pairing/dispatch fixture continues using real pair-verify.
static bool fixture_verified_key(const pairing_session_t *session, unsigned char key[ED25519_KEY_SIZE]) {
    (void)session; memset(key, 0x5a, ED25519_KEY_SIZE); return true;
}
#define pairing_session_get_verified_key fixture_verified_key
#endif
#include "clearcast_pairing_peer.h"
#include "raop.c"
#ifdef CLEARCAST_HTTP_INPUT_FIXTURE
#undef pairing_session_get_verified_key
#endif
#undef GetIpNetTable2
#undef FreeMibTable
#undef httpd_count_connection_type
#undef httpd_get_connection_by_type
#undef httpd_set_connection_type
#undef httpd_nohold
#undef raop_rtp_stop
#undef raop_rtp_mirror_stop
#undef raop_ntp_stop

static raop_t *fixture_raop;
static raop_conn_t *fixture_connections[16];
static int fixture_pin, fixture_generation;
static int fixture_feedback[3];
static char fixture_registered[45];
static raop_conn_t fixture_active_owner;
static int fixture_classifications, fixture_transport_stops;
static int fixture_video_stop_calls, fixture_video_rate_calls, fixture_video_scrub_calls;
static const char *fixture_route_error, *fixture_route_error_description;
int fixture_count_connection_type(httpd_t *httpd, connection_type_t type) { return type == CONNECTION_TYPE_RAOP; }
void *fixture_get_connection_by_type(httpd_t *httpd, connection_type_t type, int instance) {
    return type == CONNECTION_TYPE_RAOP ? &fixture_active_owner : NULL;
}
int fixture_set_connection_type(httpd_t *httpd, void *connection, connection_type_t type) {
    ++fixture_classifications; return 0;
}
bool fixture_nohold(httpd_t *httpd) { return false; }
void fixture_audio_transport_stop(raop_rtp_t *transport) { if (transport) ++fixture_transport_stops; }
void fixture_mirror_transport_stop(raop_rtp_mirror_t *transport) { if (transport) ++fixture_transport_stops; }
void fixture_timing_transport_stop(raop_ntp_t *transport) { if (transport) ++fixture_transport_stops; }
static void fixture_log(void *cls, int level, const char *message) { (void)cls; (void)level; (void)message; }
static bool fixture_check(void *cls, const char *key) { return !strcmp(fixture_registered, key); }
static void fixture_register(void *cls, const char *id, const char *key, const char *name) {
    if (strlen(key) == 44) memcpy(fixture_registered, key, 45);
}
static void fixture_video_stop(void *cls) {
    (void)cls; ++fixture_video_stop_calls; raop_clear_video_owner(fixture_raop);
}
static void fixture_video_rate(void *cls, float value) { (void)cls; (void)value; ++fixture_video_rate_calls; }
static void fixture_video_scrub(void *cls, float value) { (void)cls; (void)value; ++fixture_video_scrub_calls; }

void clearcast_media_verified(uint64_t id, const unsigned char *peer, int length, unsigned int zone) {}
void clearcast_media_closed(uint64_t id) {}
void clearcast_media_remote(uint64_t id, const char *dacp, const char *remote) {}
void clearcast_media_client_name(uint64_t id, const char *name) {}
void clearcast_media_retain_peer(const unsigned char *peer, int length, unsigned int zone, bool retain) {}
bool clearcast_direct_video_uri_allowed(const char *uri) {
#ifdef CLEARCAST_HTTP_INPUT_FIXTURE
    // The production URI policy has its own real GUri fixture. This injects
    // one accepted source to verify handler dispatch without any media I/O.
    return uri && (!strcmp(uri, "http://127.0.0.1/video.mp4") || !strcmp(uri, "https://127.0.0.1/stream.m3u8"));
#else
    return false;
#endif
}
bool clearcast_direct_video_is_active(void) { return false; }
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
    fixture_raop->callbacks.on_video_stop = fixture_video_stop;
    fixture_raop->callbacks.on_video_rate = fixture_video_rate;
    fixture_raop->callbacks.on_video_scrub = fixture_video_scrub;
    fixture_video_stop_calls = fixture_video_rate_calls = fixture_video_scrub_calls = 0;
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

/* Real production dispatch with a synthetic existing RAOP owner. Only its
 * transport-stop effects are spied; no transport or receiver socket is opened. */
__declspec(dllexport) int clearcast_test_peer_dispatch(int handle, const char *raw, int length,
    char *output, int capacity) {
    fixture_classifications = fixture_transport_stops = 0;
    fixture_route_error = fixture_route_error_description = NULL;
    if (handle < 1 || handle > 16 || !fixture_connections[handle - 1] || !raw ||
        length < 1 || length > 65536 || !output || capacity < 1) return -1;
    http_request_t *request = http_request_init();
    if (!request) return -1;
    http_request_add_data(request, raw, length);
    if (http_request_has_error(request)) {
        fixture_route_error = http_request_get_error_name(request);
        fixture_route_error_description = http_request_get_error_description(request);
        http_request_destroy(request); return -2;
    }
    if (!http_request_is_complete(request)) { http_request_destroy(request); return -3; }
    memset(&fixture_active_owner, 0, sizeof(fixture_active_owner));
    fixture_active_owner.raop_rtp = (raop_rtp_t *)(uintptr_t)1;
    fixture_active_owner.raop_rtp_mirror = (raop_rtp_mirror_t *)(uintptr_t)2;
    fixture_active_owner.raop_ntp = (raop_ntp_t *)(uintptr_t)3;
    fixture_raop->hls_support = true;
    fixture_raop->httpd = (httpd_t *)(uintptr_t)1;
    fixture_raop->dnssd = (dnssd_t *)(uintptr_t)1;
    http_response_t *response = NULL;
    conn_request(fixture_connections[handle - 1], request, &response);
    int size = 0, result = -1;
    if (response) {
        const char *data = http_response_get_data(response, &size);
        if (data && size <= capacity) { memcpy(output, data, size); result = size; }
        http_response_destroy(response);
    }
    fixture_raop->httpd = NULL;
    fixture_raop->dnssd = NULL;
    http_request_destroy(request);
    return result;
}
__declspec(dllexport) int clearcast_test_peer_route_effects(int field) {
    return field == 0 ? fixture_classifications : field == 1 ? fixture_transport_stops : -1;
}
__declspec(dllexport) const char *clearcast_test_peer_route_error(int description) {
    return description ? fixture_route_error_description : fixture_route_error;
}
__declspec(dllexport) int clearcast_test_peer_claim_owner(int handle, const char *session) {
    return handle > 0 && handle <= 16 && fixture_connections[handle-1] &&
        clearcast_video_owner_begin(fixture_connections[handle-1], session);
}
__declspec(dllexport) int clearcast_test_peer_video_effects(int field) {
    return field == 0 ? fixture_video_stop_calls : field == 1 ? fixture_video_rate_calls :
        field == 2 ? fixture_video_scrub_calls : -1;
}
