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

int main(void) {
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
