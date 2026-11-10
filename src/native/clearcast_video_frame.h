/* Bounded in-memory backdrop samples. SPDX-License-Identifier: GPL-3.0-or-later */
#pragma once
#include <gst/video/video.h>
#include <cstdint>
#include <cstring>

namespace clearcast_video_frame {
constexpr int side = 32;
constexpr int bytes = side * side * 4;

inline int copy(GstSample* sample, unsigned char* destination, int capacity) {
    if (!sample || !destination || capacity < bytes) return 0;
    GstVideoInfo input_info, output_info;
    GstCaps* caps = gst_sample_get_caps(sample);
    GstBuffer* input_buffer = gst_sample_get_buffer(sample);
    if (!caps || !input_buffer || !gst_video_info_from_caps(&input_info, caps) ||
        input_info.width <= 0 || input_info.height <= 0 || input_info.width > 8192 || input_info.height > 8192 ||
        uint64_t(input_info.width) * input_info.height > 16777216) return 0;
    GstVideoFrame input = {}, output = {};
    if (!gst_video_frame_map(&input, &input_info, input_buffer, GST_MAP_READ)) return 0;
    gst_video_info_set_format(&output_info, GST_VIDEO_FORMAT_BGRA, side, side);
    output_info.fps_n = input_info.fps_n; output_info.fps_d = input_info.fps_d;
    GstBuffer* buffer = gst_buffer_new_allocate(nullptr, output_info.size, nullptr);
    GstVideoConverter* converter = buffer ? gst_video_converter_new(&input_info, &output_info, nullptr) : nullptr;
    int copied = 0;
    if (converter && gst_video_frame_map(&output, &output_info, buffer, GST_MAP_WRITE)) {
        gst_video_converter_frame(converter, &input, &output);
        auto* pixels = static_cast<const unsigned char*>(GST_VIDEO_FRAME_PLANE_DATA(&output, 0));
        for (int y = 0; y < side; ++y)
            std::memcpy(destination + y * side * 4, pixels + y * GST_VIDEO_FRAME_PLANE_STRIDE(&output, 0), side * 4);
        copied = bytes;
        gst_video_frame_unmap(&output);
    }
    if (converter) gst_video_converter_free(converter);
    if (buffer) gst_buffer_unref(buffer);
    gst_video_frame_unmap(&input);
    return copied;
}
}
