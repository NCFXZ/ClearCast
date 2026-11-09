/* Exercise the production HTTP source policy, including private HLS fetches.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include <objbase.h>
#include <gst/gst.h>
#include <gst/video/video.h>
#include <atomic>
#include <cstdio>
#include "clearcast_media_http.h"

static std::atomic<unsigned int> frames(0);
static void handoff(GstElement*, GstBuffer*, GstPad*, gpointer) { ++frames; }

struct AppBufferProbe {
    std::atomic<unsigned int> need_data{0}, received{0}, bytes{0}, sequence_mask{0}, errors{0};
    std::atomic<bool> supplied{false};
};

static void supply_app_buffers(GstElement* source, guint, gpointer pointer) {
    auto& probe = *static_cast<AppBufferProbe*>(pointer);
    ++probe.need_data;
    if (probe.supplied.exchange(true)) return;
    for (unsigned int sequence = 0; sequence < 3; ++sequence) {
        unsigned char bytes[64];
        for (unsigned int i = 0; i < sizeof(bytes); ++i) bytes[i] = static_cast<unsigned char>(i * 13 + sequence * 37);
        GstBuffer* buffer = gst_buffer_new_allocate(nullptr, sizeof(bytes), nullptr);
        if (!buffer) { ++probe.errors; break; }
        if (gst_buffer_fill(buffer, 0, bytes, sizeof(bytes)) != sizeof(bytes)) ++probe.errors;
        GST_BUFFER_OFFSET(buffer) = sequence;
        GST_BUFFER_PTS(buffer) = sequence * GST_MSECOND;
        GST_BUFFER_DURATION(buffer) = GST_MSECOND;
        GstFlowReturn flow = GST_FLOW_ERROR;
        g_signal_emit_by_name(source, "push-buffer", buffer, &flow);
        // The action signal retains its own reference, unlike the direct appsrc API.
        gst_buffer_unref(buffer);
        if (flow != GST_FLOW_OK) { ++probe.errors; break; }
    }
    GstFlowReturn eos = GST_FLOW_ERROR;
    g_signal_emit_by_name(source, "end-of-stream", &eos);
    if (eos != GST_FLOW_OK) ++probe.errors;
}

static void receive_app_buffer(GstElement*, GstBuffer* buffer, GstPad*, gpointer pointer) {
    auto& probe = *static_cast<AppBufferProbe*>(pointer);
    unsigned char bytes[64];
    const guint64 sequence = GST_BUFFER_OFFSET(buffer);
    bool valid = sequence < 3 && gst_buffer_get_size(buffer) == sizeof(bytes) &&
        gst_buffer_extract(buffer, 0, bytes, sizeof(bytes)) == sizeof(bytes);
    for (unsigned int i = 0; valid && i < sizeof(bytes); ++i)
        valid = bytes[i] == static_cast<unsigned char>(i * 13 + sequence * 37);
    if (valid) {
        unsigned int bit = 1u << static_cast<unsigned int>(sequence);
        if (probe.sequence_mask.fetch_or(bit) & bit) ++probe.errors;
        probe.bytes += sizeof(bytes);
    } else {
        ++probe.errors;
    }
    ++probe.received;
}

static bool appsrc_buffer_transport(AppBufferProbe& probe) {
    GstElement* pipeline = gst_pipeline_new(nullptr);
    GstElement* source = gst_element_factory_make("appsrc", nullptr);
    GstElement* sink = gst_element_factory_make("fakesink", nullptr);
    if (!pipeline || !source || !sink) {
        if (source) gst_object_unref(source);
        if (sink) gst_object_unref(sink);
        if (pipeline) gst_object_unref(pipeline);
        return false;
    }
    bool replaced = !strcmp(G_OBJECT_TYPE_NAME(source), "ClearCastAppSource");
    gst_bin_add_many(GST_BIN(pipeline), source, sink, nullptr);
    GstCaps* caps = gst_caps_from_string("audio/x-raw,format=S16LE,layout=interleaved,channels=2,rate=44100");
    g_object_set(source, "caps", caps, "format", GST_FORMAT_TIME, "emit-signals", TRUE, "block", FALSE, nullptr);
    gst_caps_unref(caps);
    g_object_set(sink, "sync", FALSE, "signal-handoffs", TRUE, nullptr);
    g_signal_connect(source, "need-data", G_CALLBACK(supply_app_buffers), &probe);
    g_signal_connect(sink, "handoff", G_CALLBACK(receive_app_buffer), &probe);
    GstBus* bus = gst_element_get_bus(pipeline);
    bool eos = false;
    if (gst_element_link(source, sink) && gst_element_set_state(pipeline, GST_STATE_PLAYING) != GST_STATE_CHANGE_FAILURE) {
        GstMessage* message = gst_bus_timed_pop_filtered(bus, 5 * GST_SECOND,
            static_cast<GstMessageType>(GST_MESSAGE_EOS | GST_MESSAGE_ERROR));
        eos = message && GST_MESSAGE_TYPE(message) == GST_MESSAGE_EOS;
        if (message) gst_message_unref(message);
    }
    gst_element_set_state(pipeline, GST_STATE_NULL);
    gst_object_unref(bus);
    gst_object_unref(pipeline);
    return replaced && eos && probe.need_data && probe.received == 3 && probe.bytes == 192 &&
        probe.sequence_mask == 7 && !probe.errors;
}

int main(int argc, char** argv) {
    gst_init(nullptr, nullptr);
    if (!clearcast_http_policy_initialize(argc > 3 ? argv[3] : nullptr)) return 2;
    if (argc < 2 || !strcmp(argv[1], "--policy")) {
        const char* invalid[] = {"file:///C:/secret", "ftp://127.0.0.1/a", "data:video/mp4,x", "http://user:password@127.0.0.1/a",
            "https://user%40host@localhost/a", "http://localhost/a#fragment", "http://localhost/a\r\nx", "http:///a", "appsrc://buffer", nullptr};
        for (const char** uri = invalid; *uri; ++uri) {
            if (clearcast_http_uri_allowed(*uri)) return 3;
            GError* error = nullptr;
            GstElement* source = gst_element_make_from_uri(GST_URI_SRC, *uri, nullptr, &error);
            g_clear_error(&error);
            if (source) { gst_object_unref(source); return 4; }
        }
        if (!clearcast_http_uri_allowed("http://127.0.0.1:1234/video.mp4") ||
            !clearcast_http_uri_allowed("https://example.com/master.m3u8?token=1")) return 5;
        GstElement* buffers = gst_element_factory_make("appsrc", nullptr);
        if (!buffers || !g_object_class_find_property(G_OBJECT_GET_CLASS(buffers), "caps")) return 9;
        gst_object_unref(buffers);
        AppBufferProbe probe;
        if (!appsrc_buffer_transport(probe)) return 10;
        // These FFmpeg input paths are deliberately absent from gst-libav's
        // registered demuxers; MP4/HLS use their native GStreamer equivalents.
        const char* excluded_demuxers[] = {"avdemux_mov", "avdemux_mov_mp4_m4a_3gp_3g2_mj2", "avdemux_hls",
            "avdemux_applehttp", "avdemux_dash", "avdemux_rtsp", "avdemux_sdp"};
        for (const char* name : excluded_demuxers) {
            GstElementFactory* factory = gst_element_factory_find(name);
            if (factory) { gst_object_unref(factory); return 11; }
        }
        printf("{\"policyCases\":13,\"excludedAvDemuxers\":7,\"appsrcBuffers\":%u,\"appsrcBytes\":%u,\"appsrcNeedData\":%u,\"passed\":true}\n",
            probe.received.load(), probe.bytes.load(), probe.need_data.load());
        return 0;
    }
    bool expect_rejection = argc > 2 && !strcmp(argv[2], "--reject");
    GstElement* player = gst_element_factory_make("playbin", nullptr);
    GstElement* video = gst_element_factory_make("fakesink", nullptr);
    GstElement* audio = gst_element_factory_make("fakesink", nullptr);
    if (!player || !video || !audio) return 6;
    gst_object_ref_sink(player);
    gst_object_ref_sink(video);
    gst_object_ref_sink(audio);
    g_object_set(video, "sync", TRUE, "signal-handoffs", TRUE, nullptr);
    g_object_set(audio, "sync", TRUE, nullptr);
    g_signal_connect(video, "handoff", G_CALLBACK(handoff), nullptr);
    g_object_set(player, "uri", argv[1], "video-sink", video, "audio-sink", audio, nullptr);
    GstBus* bus = gst_element_get_bus(player);
    bool error = gst_element_set_state(player, GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE;
    bool eos = false;
    gint64 deadline = g_get_monotonic_time() + 20 * G_TIME_SPAN_SECOND;
    while (!error && !eos && g_get_monotonic_time() < deadline) {
        GstMessage* message = gst_bus_timed_pop_filtered(bus, 100 * GST_MSECOND,
            static_cast<GstMessageType>(GST_MESSAGE_ERROR | GST_MESSAGE_EOS));
        if (!message) continue;
        error = GST_MESSAGE_TYPE(message) == GST_MESSAGE_ERROR;
        eos = GST_MESSAGE_TYPE(message) == GST_MESSAGE_EOS;
        if (error) {
            GError* failure = nullptr;
            gchar* debug = nullptr;
            gst_message_parse_error(message, &failure, &debug);
            fprintf(stderr, "media error domain=%s code=%d message=%s details=%s\n", g_quark_to_string(failure->domain),
                failure->code, failure->message, debug ? debug : "");
            g_clear_error(&failure);
            g_free(debug);
        }
        gst_message_unref(message);
    }
    GstPad* pad = gst_element_get_static_pad(video, "sink");
    GstCaps* caps = pad ? gst_pad_get_current_caps(pad) : nullptr;
    GstVideoInfo info;
    bool geometry = caps && gst_video_info_from_caps(&info, caps);
    printf("{\"frames\":%u,\"width\":%u,\"height\":%u,\"error\":%s,\"eos\":%s}\n",
        frames.load(), geometry ? info.width : 0, geometry ? info.height : 0, error ? "true" : "false", eos ? "true" : "false");
    if (caps) gst_caps_unref(caps);
    if (pad) gst_object_unref(pad);
    gst_element_set_state(player, GST_STATE_NULL);
    gst_object_unref(bus);
    gst_object_unref(player);
    gst_object_unref(video);
    gst_object_unref(audio);
    return expect_rejection ? (error && !frames ? 0 : 7) : (!error && eos && frames && geometry ? 0 : 8);
}
