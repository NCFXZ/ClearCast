/* Negotiated stream information. Never infer source bitrate from decoded PCM
 * or pixels, or describe the sender's advertised display size as media size.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#pragma once
#include <gst/gst.h>
#include <cmath>
#include <cstdint>
#include <string>

namespace clearcast_diagnostics {
struct Info {
    std::string video_codec, audio_codec;
    int width = 0, height = 0, sample_rate = 0, channels = 0;
    double frame_rate = 0;
    uint64_t video_bitrate = 0, audio_bitrate = 0;
};
inline int integer(const GstStructure* format, const char* name, int maximum) {
    int value = 0;
    return gst_structure_get_int(format, name, &value) && value > 0 && value <= maximum ? value : 0;
}
inline void caps(Info& result, GstCaps* value, bool video, bool audio, bool encoded) {
    if (!value || gst_caps_get_size(value) != 1) return;
    const GstStructure* format = gst_caps_get_structure(value, 0);
    const char* type = gst_structure_get_name(format);
    if (video && g_str_has_prefix(type, "video/")) {
        if (encoded) {
            if (!strcmp(type, "video/x-h264")) result.video_codec = "H.264";
            else if (!strcmp(type, "video/x-h265")) result.video_codec = "H.265";
            else if (!strcmp(type, "video/x-vp8")) result.video_codec = "VP8";
            else if (!strcmp(type, "video/x-vp9")) result.video_codec = "VP9";
            else if (!strcmp(type, "video/x-av1")) result.video_codec = "AV1";
            else if (!strcmp(type, "video/mpeg")) result.video_codec = "MPEG";
        }
        const int width = integer(format, "width", 32768), height = integer(format, "height", 32768);
        if (width && height) { result.width = width; result.height = height; }
        int numerator = 0, denominator = 0;
        if (gst_structure_get_fraction(format, "framerate", &numerator, &denominator) && numerator > 0 && denominator > 0) {
            const double rate = double(numerator) / denominator;
            if (std::isfinite(rate) && rate <= 1000) result.frame_rate = rate;
        }
    }
    if (audio && g_str_has_prefix(type, "audio/")) {
        if (encoded) {
            if (!strcmp(type, "audio/x-alac")) result.audio_codec = "ALAC";
            else if (!strcmp(type, "audio/mpeg")) {
                const int version = integer(format, "mpegversion", 4);
                result.audio_codec = version == 2 || version == 4 ? "AAC" : "MPEG Audio";
            } else if (!strcmp(type, "audio/x-flac")) result.audio_codec = "FLAC";
            else if (!strcmp(type, "audio/x-opus")) result.audio_codec = "Opus";
            else if (!strcmp(type, "audio/x-vorbis")) result.audio_codec = "Vorbis";
            else if (!strcmp(type, "audio/x-ac3")) result.audio_codec = "AC-3";
            else if (!strcmp(type, "audio/x-eac3")) result.audio_codec = "E-AC-3";
            else if (!strcmp(type, "audio/x-raw")) result.audio_codec = "PCM";
        }
        const int rate = integer(format, "rate", 768000), channels = integer(format, "channels", 64);
        if (rate) result.sample_rate = rate;
        if (channels) result.channels = channels;
    }
}
inline void tags(Info& result, const GstTagList* list, bool video, bool audio) {
    if (!list) return;
    guint bitrate = 0;
    // Both are stream metadata, never a measurement of network throughput.
    if (!gst_tag_list_get_uint(list, GST_TAG_BITRATE, &bitrate))
        gst_tag_list_get_uint(list, GST_TAG_NOMINAL_BITRATE, &bitrate);
    if (bitrate) {
        if (video) result.video_bitrate = bitrate;
        if (audio) result.audio_bitrate = bitrate;
    }
}
inline void pad(Info& result, GstElement* element, const char* name, bool video, bool audio, bool encoded) {
    GstPad* source = gst_element_get_static_pad(element, name);
    if (!source) return;
    GstCaps* format = gst_pad_get_current_caps(source);
    caps(result, format, video, audio, encoded);
    if (format) gst_caps_unref(format);
    for (guint i = 0; i < 16; ++i) {
        GstEvent* event = gst_pad_get_sticky_event(source, GST_EVENT_TAG, i);
        if (!event) break;
        GstTagList* list = nullptr;
        gst_event_parse_tag(event, &list);
        tags(result, list, video, audio);
        gst_event_unref(event);
    }
    gst_object_unref(source);
}
inline Info collect(GstElement* pipeline, bool video, bool audio) {
    Info result;
    if (!pipeline || !GST_IS_BIN(pipeline)) return result;
    GstIterator* iterator = gst_bin_iterate_recurse(GST_BIN(pipeline));
    GValue item = G_VALUE_INIT;
    unsigned int count = 0, retries = 0;
    while (count < 256) {
        const GstIteratorResult next = gst_iterator_next(iterator, &item);
        if (next == GST_ITERATOR_RESYNC && ++retries <= 3) {
            result = Info(); count = 0; gst_iterator_resync(iterator); continue;
        }
        if (next != GST_ITERATOR_OK) break;
        ++count;
        GstElement* element = GST_ELEMENT(g_value_get_object(&item));
        GstElementFactory* factory = gst_element_get_factory(element);
        if (factory && gst_element_factory_list_is_type(factory, GST_ELEMENT_FACTORY_TYPE_DECODER)) {
            // Read output before input: source caps retain the encoded stream's
            // geometry and sample rate when downstream converters resize it.
            const bool is_video = video && gst_element_factory_list_is_type(factory, GST_ELEMENT_FACTORY_TYPE_MEDIA_VIDEO);
            const bool is_audio = audio && gst_element_factory_list_is_type(factory, GST_ELEMENT_FACTORY_TYPE_MEDIA_AUDIO);
            pad(result, element, "src", is_video, is_audio, false);
            pad(result, element, "sink", is_video, is_audio, true);
        }
        g_value_reset(&item);
    }
    g_value_unset(&item);
    gst_iterator_free(iterator);
    // Playbin exposes tags for the selected streams even when a decoder does
    // not forward a bitrate tag on its own pads (common for MP4/HLS sources).
    for (const char* kind : {"video", "audio"}) {
        const bool is_video = !strcmp(kind, "video");
        if (!(is_video ? video : audio)) continue;
        const std::string action = std::string("get-") + kind + "-tags";
        const std::string property = std::string("current-") + kind;
        if (!g_signal_lookup(action.c_str(), G_OBJECT_TYPE(pipeline)) ||
            !g_object_class_find_property(G_OBJECT_GET_CLASS(pipeline), property.c_str())) continue;
        gint selected = -1;
        g_object_get(pipeline, property.c_str(), &selected, nullptr);
        if (selected < 0) continue;
        GstTagList* list = nullptr;
        g_signal_emit_by_name(pipeline, action.c_str(), selected, &list);
        tags(result, list, is_video, !is_video);
        if (list) gst_tag_list_unref(list);
    }
    return result;
}
inline std::string json(const Info& value) {
    char rate[32];
    g_ascii_formatd(rate, sizeof(rate), "%.6f", value.frame_rate);
    return "{\"videoCodec\":\"" + value.video_codec + "\",\"width\":" + std::to_string(value.width) +
        ",\"height\":" + std::to_string(value.height) + ",\"frameRate\":" + rate +
        ",\"videoBitrate\":" + std::to_string(value.video_bitrate) +
        ",\"audioCodec\":\"" + value.audio_codec + "\",\"sampleRate\":" + std::to_string(value.sample_rate) +
        ",\"channels\":" + std::to_string(value.channels) + ",\"audioBitrate\":" + std::to_string(value.audio_bitrate) + "}";
}
}
