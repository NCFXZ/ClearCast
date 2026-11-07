#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "http_request.h"

int main(void) {
    const char *content_fields[] = {"Content-Type", "content-type", "CONTENT-TYPE", "cOnTeNt-TyPe"};
    const char *sequence_fields[] = {"CSeq", "cseq", "CSEQ", "cSeQ"};
    const char *content_lookups[] = {"Content-Type", "content-type", "CONTENT-TYPE", "cOnTeNt-TyPe"};
    const char *sequence_lookups[] = {"CSeq", "cseq", "CSEQ", "cSeQ"};
    unsigned checked = 0, failures = 0;
    for (unsigned i = 0; i < 4; ++i) {
        for (unsigned j = 0; j < 4; ++j) {
            char message[512];
            snprintf(message, sizeof(message),
                "POST /pair-setup-pin RTSP/1.0\r\n%s: application/x-apple-binary-plist\r\n%s: 17\r\nContent-Length: 0\r\n\r\n",
                content_fields[i], sequence_fields[j]);
            http_request_t *request = http_request_init();
            if (!request || http_request_add_data(request, message, strlen(message)) != 0 ||
                !http_request_is_complete(request)) {
                fprintf(stderr, "Could not parse input %u/%u\n", i, j);
                return 2;
            }
            for (unsigned k = 0; k < 4; ++k) {
                const char *content = http_request_get_header(request, content_lookups[k]);
                const char *sequence = http_request_get_header(request, sequence_lookups[k]);
                checked += 2;
                if (!content || strcmp(content, "application/x-apple-binary-plist")) ++failures;
                if (!sequence || strcmp(sequence, "17")) ++failures;
            }
            ++checked;
            if (http_request_get_header(request, "Content-Typ")) ++failures;
            http_request_destroy(request);
        }
    }
    printf("Header getter checks: %u; failures: %u\n", checked, failures);
    return failures ? 1 : 0;
}
