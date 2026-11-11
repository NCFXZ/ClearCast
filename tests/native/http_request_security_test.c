/* Real-parser boundary, fragmentation and deterministic adversarial regression. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include "http_request.h"

static unsigned checks = 0;
static unsigned failures = 0;

static void expect(int pass, const char *name) {
    ++checks;
    if (!pass) {
        ++failures;
        fprintf(stderr, "FAIL: %s\n", name);
    }
}

static int parse(const char *data, size_t length, size_t fragment, int complete) {
    http_request_t *request = http_request_init();
    if (!request) return 0;
    for (size_t offset = 0; offset < length && !http_request_has_error(request);) {
        size_t count = length - offset;
        if (count > fragment) count = fragment;
        http_request_add_data(request, data + offset, count);
        offset += count;
    }
    int result = complete ? !http_request_has_error(request) && http_request_is_complete(request)
                          : http_request_has_error(request);
    http_request_destroy(request);
    return result;
}

static void text_case(const char *data, int valid, const char *name) {
    expect(parse(data, strlen(data), strlen(data), valid), name);
    expect(parse(data, strlen(data), 1, valid), name);
}

static void event_case(const char *data, size_t length, size_t fragment, int valid, const char *name) {
    http_request_t *request = http_request_init();
    if (!request || http_request_upgrade_data(request)) exit(2);
    unsigned parsed = 0;
    int rejected = 0;
    for (size_t offset = 0; offset < length;) {
        size_t count = length - offset;
        if (count > fragment) count = fragment;
        if (http_request_add_event_data(request, data + offset, count)) { rejected = 1; break; }
        offset += count;
        char *header = NULL, *body = NULL;
        size_t header_len, body_len;
        while (http_request_parse_event_data(request, &header, &header_len, &body, &body_len)) {
            ++parsed;
            expect(header && header_len && !http_request_has_error(request), "parsed EVENT header stays valid");
            free(header); free(body);
        }
        if (http_request_has_error(request)) { rejected = 1; break; }
    }
    expect(valid ? parsed == 1 && !rejected : !parsed && rejected, name);
    http_request_destroy(request);
}

static void event_text_case(const char *data, int valid, const char *name) {
    event_case(data, strlen(data), strlen(data), valid, name);
    event_case(data, strlen(data), 1, valid, name);
}

static void reverse_event_checks(void) {
    event_text_case("a\r\n\r\n", 0, "short EVENT header never underflows Content-Length search");
    event_text_case("HTTP/1.1 200 OK\r\n\r\n", 1, "reverse response without body");
    event_text_case("EVENT/1.0 200 OK\r\ncontent-length:\t3 \r\n\r\nabc", 1, "fragmented case-insensitive EVENT body");
    event_text_case("HTTP/1.1 200 OK\r\nX-Content-Length: 1000000\r\n\r\n", 1, "Content-Length must be a complete header field");
    event_text_case("HTTP/1.1 200 OK\r\nContent-Length: -1\r\n\r\n", 0, "negative EVENT length");
    event_text_case("HTTP/1.1 200 OK\r\nContent-Length: +1\r\n\r\na", 0, "signed EVENT length");
    event_text_case("HTTP/1.1 200 OK\r\nContent-Length: 18446744073709551616\r\n\r\n", 0, "overflow EVENT length");
    event_text_case("HTTP/1.1 200 OK\r\nContent-Length: 4095\r\n\r\n", 0, "EVENT header and body share the bounded capacity");
    event_text_case("HTTP/1.1 200 OK\r\nContent-Length: 1junk\r\n\r\na", 0, "EVENT length trailing text");
    event_text_case("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nContent-Length: 0\r\n\r\n", 0, "duplicate EVENT length rejected");
    const char nul[] = "HTTP/1.1 200 OK\r\nX: a\0b\r\n\r\n";
    event_case(nul, sizeof(nul)-1, 1, 0, "embedded NUL EVENT header");

    http_request_t *request = http_request_init();
    expect(request && !http_request_upgrade_data(request), "EVENT buffer initialized");
    expect(http_request_add_event_data(request, "x", SIZE_MAX) == -1, "EVENT append rejects size_t wrap without reading input");
    const char combined[] = "\0 \r\nHTTP/1.1 200 OK\r\nContent-Length: 3\r\n\r\nabcEVENT/1.0 200 OK\r\n\r\n";
    expect(!http_request_add_event_data(request, combined, sizeof(combined)-1), "combined reverse messages append");
    char *header = NULL, *body = NULL;
    size_t header_len, body_len;
    expect(http_request_parse_event_data(request, &header, &header_len, &body, &body_len) &&
        !strncmp(header, "HTTP/1.1", 8) && body_len == 3 && !memcmp(body, "abc", 3),
        "leading junk is excluded from header and binary body offsets");
    free(header); free(body);
    expect(http_request_parse_event_data(request, &header, &header_len, &body, &body_len) &&
        !strncmp(header, "EVENT/1.0", 9) && body_len == 0 && !body,
        "combined trailing EVENT remains correctly framed");
    free(header); free(body);
    expect(!http_request_parse_event_data(request, &header, &header_len, &body, &body_len) &&
        !http_request_has_error(request), "drained reverse channel waits without error");
    http_request_destroy(request);
}

int main(void) {
    reverse_event_checks();
    text_case("OPTIONS * RTSP/1.0\r\nCSeq: 1\r\nContent-Length: 0\r\n\r\n", 1, "valid options");
    text_case("POST /pair-setup-pin RTSP/1.0\r\ncseq: 1\r\ncontent-length: 3\r\n\r\nabc", 1, "fragmented lower-case headers");
    text_case("POST /x RTSP/1.0\r\nContent-Length: -1\r\n\r\n", 0, "negative content length");
    text_case("POST /x RTSP/1.0\r\nContent-Length: 1048577\r\n\r\n", 0, "oversized content length");
    text_case("POST /x RTSP/1.0\r\nContent-Length: 18446744073709551616\r\n\r\n", 0, "integer overflow content length");
    text_case("POST /x RTSP/1.0\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\na", 0, "conflicting lengths");
    text_case("OPTIONS * RTSP/1.0\r\n\r\nOPTIONS * RTSP/1.0\r\n\r\n", 0, "unsupported pipelining cannot overwrite request");

    size_t capacity = 1100000;
    char *buffer = malloc(capacity);
    if (!buffer) return 2;
    for (size_t count = 8192; count <= 8193; ++count) {
        memcpy(buffer, "GET ", 4);
        memset(buffer + 4, 'a', count);
        buffer[4] = '/';
        const char tail[] = " RTSP/1.0\r\nCSeq: 1\r\n\r\n";
        memcpy(buffer + 4 + count, tail, sizeof(tail) - 1);
        expect(parse(buffer, 4 + count + sizeof(tail) - 1, 7, count == 8192), "URL boundary");
    }
    const char body_header[] = "POST /x RTSP/1.0\r\nContent-Length: 1048576\r\n\r\n";
    memcpy(buffer, body_header, sizeof(body_header)-1);
    memset(buffer+sizeof(body_header)-1, 0xa5, 1048576);
    expect(parse(buffer, sizeof(body_header)-1+1048576, 1024, 1), "exact 1 MiB binary body");
    const char header_prefix[] = "OPTIONS * RTSP/1.0\r\nX:";
    memcpy(buffer, header_prefix, sizeof(header_prefix)-1);
    memset(buffer+sizeof(header_prefix)-1, 'a', 32768);
    memcpy(buffer+sizeof(header_prefix)-1+32768, "\r\n\r\n", 4);
    expect(parse(buffer, sizeof(header_prefix)-1+32768+4, 64, 0), "header budget");
    const char nul_header[] = "OPTIONS * RTSP/1.0\r\nX: a\0b\r\n\r\n";
    expect(parse(nul_header, sizeof(nul_header)-1, 3, 0), "embedded NUL header");

    const char seed[] = "POST /pair-setup-pin RTSP/1.0\r\nCSeq: 1\r\nContent-Length: 16\r\nContent-Type: application/x-apple-binary-plist\r\n\r\nbplist00testdata!";
    uint32_t random = 0x43535431u;
    for (unsigned iteration = 0; iteration < 10000; ++iteration) {
        size_t length = sizeof(seed)-1;
        memcpy(buffer, seed, length);
        random = random * 1664525u + 1013904223u;
        size_t mutations = 1 + random % 12;
        for (size_t j = 0; j < mutations; ++j) {
            random = random * 1664525u + 1013904223u;
            size_t offset = random % length;
            random = random * 1664525u + 1013904223u;
            buffer[offset] = (char) (random >> 24);
        }
        http_request_t *request = http_request_init();
        if (!request) return 2;
        size_t fragment = 1 + random % 17;
        for (size_t offset = 0; offset < length && !http_request_has_error(request);) {
            size_t count = length-offset;
            if (count > fragment) count = fragment;
            http_request_add_data(request, buffer+offset, count);
            offset += count;
        }
        http_request_destroy(request);
        ++checks;
    }
    free(buffer);
    printf("Parser security: %u checks, %u failures\n", checks, failures);
    return failures ? 1 : 0;
}
