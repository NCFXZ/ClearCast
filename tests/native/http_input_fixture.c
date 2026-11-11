/* Actual production HTTP handlers, no sockets or media output. Dependency
 * stubs are shared with the independent pairing fixture, never the payload.
 * SPDX-License-Identifier: GPL-3.0-or-later */
#include <plist/plist.h>
#include <string.h>
#include <math.h>

static unsigned input_uuid_allocations, input_uuid_releases;
static char *input_uuid_pointer;
static int input_fail_location_allocation;
static void input_get_string_val(plist_t node, char **value) {
    plist_get_string_val(node, value);
    if (*value && !strcmp(*value, "missing-source-fixture")) {
        ++input_uuid_allocations;
        input_uuid_pointer = *value;
    } else if (*value && input_fail_location_allocation && !strcmp(*value, "http://127.0.0.1/video.mp4")) {
        plist_mem_free(*value);
        *value = NULL;
    }
}
static void input_mem_free(void *value) {
    if (value && value == input_uuid_pointer) {
        ++input_uuid_releases;
        input_uuid_pointer = NULL;
    }
    plist_mem_free(value);
}
#define plist_get_string_val input_get_string_val
#define plist_mem_free input_mem_free
#define CLEARCAST_HTTP_INPUT_FIXTURE 1
#include "pairing_peer_fixture.c"
#undef plist_get_string_val
#undef plist_mem_free

static unsigned input_checks, input_resets, input_plays;
static char input_directory[MAX_PATH], input_keyfile[MAX_PATH];
static playback_info_t input_video_info;
static plist_t input_video_response;
static void input_acquire_playback_info(void *cls, playback_info_t *info) {
    (void)cls; *info = input_video_info;
}
static void input_reset(void *cls, int reason) { (void)cls; (void)reason; ++input_resets; }
static void input_play(void *cls, const char *uri, float position) {
    (void)cls; (void)position;
    if (!strcmp(uri, "http://127.0.0.1/video.mp4") || !strcmp(uri, "https://127.0.0.1/stream.m3u8")) ++input_plays;
}
static void input_cleanup(void) {
    if (input_video_response) plist_free(input_video_response);
    if (fixture_raop) for (int i = 0; i < MAX_AIRPLAY_VIDEO; ++i)
        if (fixture_raop->airplay_video[i]) raop_destroy_airplay_video(fixture_raop, i);
    clearcast_test_peer_destroy();
    if (*input_keyfile) DeleteFileA(input_keyfile);
    if (*input_directory) RemoveDirectoryA(input_directory);
}
#define INPUT_CHECK(value) do { ++input_checks; if (!(value)) { fprintf(stderr,"FAIL input check %u line %d: %s\n",input_checks,__LINE__,#value); exit(1); } } while (0)

static unsigned input_playlist_count(void) {
    unsigned count = 0;
    for (int i = 0; i < MAX_AIRPLAY_VIDEO; ++i) count += fixture_raop->airplay_video[i] != NULL;
    return count;
}

static int input_request(const char *path, const void *body, int length) {
    char header[1024];
    const int playback = !strcmp(path, "/playback-info");
    int size = snprintf(header, sizeof(header), "%s %s HTTP/1.1\r\nHost: localhost\r\nX-Apple-Session-ID: fixture-session\r\nContent-Type: application/x-apple-binary-plist\r\nContent-Length: %d\r\n\r\n", playback ? "GET" : "POST", path, length);
    http_request_t *request = http_request_init();
    INPUT_CHECK(request && !http_request_add_data(request, header, size));
    if (length) INPUT_CHECK(!http_request_add_data(request, body, length));
    INPUT_CHECK(http_request_is_complete(request) && !http_request_has_error(request));
    http_response_t *response = http_response_create();
    http_response_init(response, "HTTP/1.1", 200, "OK");
    char *output = NULL; int output_length = 0;
    raop_conn_t *connection = fixture_connections[0];
    if (!strcmp(path, "/action")) http_handler_action(connection, request, response, &output, &output_length);
    else if (!strcmp(path, "/play")) http_handler_play(connection, request, response, &output, &output_length);
    else if (!strcmp(path, "/fp-setup2")) http_handler_fpsetup2(connection, request, response, &output, &output_length);
    else if (playback) http_handler_playback_info(connection, request, response, &output, &output_length);
    else exit(2);
    if (playback) {
        INPUT_CHECK(output && output_length > 0);
        if (input_video_response) plist_free(input_video_response);
        input_video_response = NULL;
        plist_from_xml(output, (uint32_t)output_length, &input_video_response);
        INPUT_CHECK(input_video_response && plist_get_node_type(input_video_response) == PLIST_DICT);
    }
    http_response_finish(response, output, output_length);
    int response_length = 0, status = 0;
    const char *raw = http_response_get_data(response, &response_length);
    INPUT_CHECK(raw && sscanf(raw, "%*s %d", &status) == 1);
    free(output);
    http_response_destroy(response);
    http_request_destroy(request);
    return status;
}

static double input_video_real(plist_t object, const char *name) {
    plist_t node = plist_dict_get_item(object, name);
    INPUT_CHECK(node && plist_get_node_type(node) == PLIST_REAL);
    double value = 0; plist_get_real_val(node, &value);
    return value;
}

static void input_playback(double duration, double position, unsigned loaded_count) {
    memset(&input_video_info, 0, sizeof(input_video_info));
    input_video_info.duration = input_video_info.seek_duration = duration;
    input_video_info.position = position;
    input_video_info.ready_to_play = true;
    INPUT_CHECK(input_request("/playback-info", NULL, 0) == 200);
    INPUT_CHECK(fabs(input_video_real(input_video_response, "duration") - duration) < 1e-9);
    INPUT_CHECK(fabs(input_video_real(input_video_response, "position") - position) < 1e-9);
    plist_t loaded = plist_dict_get_item(input_video_response, "loadedTimeRanges");
    INPUT_CHECK(loaded && plist_get_node_type(loaded) == PLIST_ARRAY);
    INPUT_CHECK(plist_array_get_size(loaded) == loaded_count);
    if (loaded_count) {
        plist_t range = plist_array_get_item(loaded, 0);
        INPUT_CHECK(fabs(input_video_real(range, "start") - position) < 1e-9);
        INPUT_CHECK(fabs(input_video_real(range, "duration") - (duration - position)) < 1e-9);
    }
    plist_t seekable = plist_dict_get_item(input_video_response, "seekableTimeRanges");
    INPUT_CHECK(seekable && plist_get_node_type(seekable) == PLIST_ARRAY && plist_array_get_size(seekable) == 1);
    plist_t seek = plist_array_get_item(seekable, 0);
    INPUT_CHECK(input_video_real(seek, "start") == 0.0 && input_video_real(seek, "duration") >= 0.0);
}

static int input_send_plist(const char *path, plist_t root) {
    char *body = NULL; uint32_t length = 0;
    plist_to_bin(root, &body, &length);
    INPUT_CHECK(body && length);
    int result = input_request(path, body, length);
    plist_mem_free(body); plist_free(root);
    return result;
}

static int input_plist(const char *path, const char *location, const char *action) {
    plist_t root = plist_new_dict();
    if (location) {
        plist_dict_set_item(root, "Content-Location", plist_new_string(location));
        plist_dict_set_item(root, "uuid", plist_new_string(!strncmp(location, "https:", 6)
            ? "22222222-2222-2222-2222-222222222222" : "11111111-1111-1111-1111-111111111111"));
    }
    if (action) {
        plist_dict_set_item(root, "type", plist_new_string(action));
        plist_t params = plist_new_dict();
        // Missing item.uuid is the Release-build NULL strcmp regression.
        plist_dict_set_item(params, "item", plist_new_dict());
        plist_dict_set_item(root, "params", params);
    }
    return input_send_plist(path, root);
}

static int input_resume(const char *location) {
    plist_t root = plist_new_dict();
    plist_dict_set_item(root, "uuid", plist_new_string("11111111-1111-1111-1111-111111111111"));
    if (location) plist_dict_set_item(root, "Content-Location", plist_new_string(location));
    return input_send_plist("/play", root);
}

int main(void) {
    char temporary[MAX_PATH];
    INPUT_CHECK(GetTempPathA(MAX_PATH, temporary));
    INPUT_CHECK(GetTempFileNameA(temporary, "CCI", 0, input_directory));
    INPUT_CHECK(DeleteFileA(input_directory) && CreateDirectoryA(input_directory, NULL));
    snprintf(input_keyfile, sizeof(input_keyfile), "%s\\identity.key", input_directory);
    atexit(input_cleanup);
    INPUT_CHECK(clearcast_test_peer_reset(input_keyfile) == 0);
    fixture_raop->callbacks.conn_reset = input_reset;
    fixture_raop->callbacks.on_video_play = input_play;
    fixture_raop->callbacks.on_video_acquire_playback_info = input_acquire_playback_info;
    fixture_raop->current_video = 3;
    INPUT_CHECK(clearcast_test_peer_open(fixture_v4, 4, 7) == 1);
    INPUT_CHECK(get_playlist_by_uuid(NULL, "x") == -1 && get_playlist_by_uuid(fixture_raop, NULL) == -1);

    unsigned char short_body[5] = {0, 1, 2, 3, 3};
    for (int length = 0; length < 5; ++length) INPUT_CHECK(input_request("/fp-setup2", short_body, length) == 400);
    INPUT_CHECK(input_request("/fp-setup2", short_body, 5) == 421);
    INPUT_CHECK(input_request("/action", NULL, 0) == 501);
    INPUT_CHECK(input_plist("/action", NULL, "playlistRemove") == 501);
    INPUT_CHECK(input_plist("/action", NULL, "unhandledURLResponse") == 501);
    INPUT_CHECK(input_playlist_count() == 0 && fixture_raop->current_video == 3 && input_resets == 0 && input_plays == 0);
    INPUT_CHECK(input_plist("/play", "mlhls://host/master.m3u8", NULL) == 400);
    INPUT_CHECK(input_playlist_count() == 0 && fixture_raop->current_video == 3 &&
        fixture_raop->media_owner_peer_len == 0 && input_resets == 1 && input_plays == 0);
    INPUT_CHECK(input_plist("/play", "file:///master.m3u8", NULL) == 400);
    INPUT_CHECK(input_playlist_count() == 0 && fixture_raop->current_video == 3 &&
        fixture_raop->media_owner_peer_len == 0 && input_resets == 2 && input_plays == 0);
    INPUT_CHECK(input_plist("/play", "http://127.0.0.1/video.mp4", NULL) == 200);
    INPUT_CHECK(input_plays == 1 && input_playlist_count() == 1 && fixture_raop->media_owner_peer_len == 4);
    int cached_id = fixture_raop->current_video;
    airplay_video_t *cached = fixture_raop->airplay_video[cached_id];
    INPUT_CHECK(input_resume("http://127.0.0.1/video.mp4") == 200);
    INPUT_CHECK(input_plays == 2 && input_playlist_count() == 1 && fixture_raop->current_video == cached_id &&
        fixture_raop->airplay_video[cached_id] == cached);
    INPUT_CHECK(input_resume(NULL) == 200);
    INPUT_CHECK(input_plays == 3 && input_playlist_count() == 1 && fixture_raop->current_video == cached_id &&
        fixture_raop->airplay_video[cached_id] == cached);
    INPUT_CHECK(input_resume("mlhls://host/master.m3u8") == 400);
    INPUT_CHECK(input_plays == 3 && input_resets == 3 && input_playlist_count() == 1 &&
        fixture_raop->current_video == cached_id && fixture_raop->airplay_video[cached_id] == cached);
    INPUT_CHECK(input_plist("/play", "https://127.0.0.1/stream.m3u8", NULL) == 200);
    INPUT_CHECK(input_plays == 4 && input_resets == 3);
    for (unsigned i = 0; i < 256; ++i) {
        plist_t root = plist_new_dict();
        plist_dict_set_item(root, "uuid", plist_new_string("missing-source-fixture"));
        INPUT_CHECK(input_send_plist("/play", root) == 400);
        INPUT_CHECK(input_uuid_allocations == i + 1 && input_uuid_releases == input_uuid_allocations && !input_uuid_pointer);
    }
    input_fail_location_allocation = 1;
    plist_t failed_location = plist_new_dict();
    plist_dict_set_item(failed_location, "uuid", plist_new_string("missing-source-fixture"));
    plist_dict_set_item(failed_location, "Content-Location", plist_new_string("http://127.0.0.1/video.mp4"));
    INPUT_CHECK(input_send_plist("/play", failed_location) == 400);
    INPUT_CHECK(input_uuid_allocations == 257 && input_uuid_releases == input_uuid_allocations && !input_uuid_pointer);
    INPUT_CHECK(input_plays == 4);
    input_playback(0.0, 56.33, 0); // A live stream has no known finite loaded range.
    input_playback(0.0, 0.0, 0);
    input_playback(200.0, 0.0, 1);
    input_playback(200.0, 56.33, 1);
    input_playback(200.0, 200.0, 1);
    input_playback(200.0, 200.5, 0); // Late position samples cannot produce negative ranges.
    printf("PASS %u actual HTTP handler input checks (no sockets/media output)\n", input_checks);
    return 0;
}
