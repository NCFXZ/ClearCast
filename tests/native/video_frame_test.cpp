// SPDX-License-Identifier: GPL-3.0-or-later
#include "clearcast_video_frame.h"
#include <array>
#include <cstdio>

int main() {
    gst_init(nullptr, nullptr);
    int checks = 0;
    auto check = [&](bool value) { if (!value) { std::fprintf(stderr, "frame check %d failed\n", checks + 1); return false; } ++checks; return true; };
    std::array<unsigned char, clearcast_video_frame::bytes + 1> output;
    output.fill(0xA5);
    if (!check(!clearcast_video_frame::copy(nullptr, output.data(), int(output.size())))) return 1;
    for (GstVideoFormat format : {GST_VIDEO_FORMAT_BGRA, GST_VIDEO_FORMAT_NV12}) {
        GstVideoInfo info;
        gst_video_info_set_format(&info, format, 16, 16);
        info.fps_n = 30; info.fps_d = 1;
        GstBuffer* buffer = gst_buffer_new_allocate(nullptr, info.size, nullptr);
        GstMapInfo map;
        gst_buffer_map(buffer, &map, GST_MAP_WRITE);
        if (format == GST_VIDEO_FORMAT_BGRA) {
            for (gsize index = 0; index + 3 < map.size; index += 4) {
                map.data[index] = 10; map.data[index + 1] = 100; map.data[index + 2] = 220; map.data[index + 3] = 255;
            }
        } else {
            std::memset(map.data, 128, map.size);
            std::memset(map.data, 235, info.offset[1]);
        }
        gst_buffer_unmap(buffer, &map);
        GstCaps* caps = gst_video_info_to_caps(&info);
        GstSample* sample = gst_sample_new(buffer, caps, nullptr, nullptr);
        bool ok = check(!clearcast_video_frame::copy(sample, output.data(), clearcast_video_frame::bytes - 1)) &&
            check(output.back() == 0xA5) &&
            check(clearcast_video_frame::copy(sample, output.data(), int(output.size())) == clearcast_video_frame::bytes) &&
            check(output.back() == 0xA5) && check(output[3] == 255);
        if (format == GST_VIDEO_FORMAT_BGRA) ok = ok && check(output[0] == 10 && output[1] == 100 && output[2] == 220);
        else ok = ok && check(output[0] >= 248 && output[1] >= 248 && output[2] >= 248);
        gst_sample_unref(sample); gst_caps_unref(caps); gst_buffer_unref(buffer);
        if (!ok) return 1;
    }
    std::printf("PASS %d frame checks\n", checks);
    return 0;
}
