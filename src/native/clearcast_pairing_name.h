/* Names add a local admission condition; pair-verify signatures remain
 * mandatory. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef CLEARCAST_PAIRING_NAME_H
#define CLEARCAST_PAIRING_NAME_H
#include <winsock2.h>
#include <windows.h>
#include <stdbool.h>
#include <string.h>

static bool clearcast_pairing_space(wchar_t c) {
    return (c >= 9 && c <= 13) || c == 0x20 || c == 0x85 || c == 0xa0 || c == 0x1680 ||
        (c >= 0x2000 && c <= 0x200a) || c == 0x2028 || c == 0x2029 || c == 0x202f || c == 0x205f || c == 0x3000;
}

/* Same trim/CR normalization as the managed registry, bounded valid UTF-8. */
static bool clearcast_pairing_normalize_name(const char *input, char output[513]) {
    if (!input || !output) return false;
    size_t bytes = strnlen(input, 513);
    if (bytes > 512) return false;
    if (!bytes) { output[0] = 0; return true; }
    wchar_t wide[513];
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input, (int)bytes, wide, 513);
    if (!count) return false;
    int begin = 0, end = count;
    while (begin < end && clearcast_pairing_space(wide[begin])) ++begin;
    while (end > begin && clearcast_pairing_space(wide[end - 1])) --end;
    for (int index = begin; index < end; ++index) {
        if (wide[index] == '\r') wide[index] = ' ';
        else if (wide[index] < 32 || wide[index] == 127) return false;
    }
    if (begin == end) { output[0] = 0; return true; }
    int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide + begin, end - begin, output, 512, NULL, NULL);
    if (!size) return false;
    output[size] = 0;
    return true;
}
#endif
