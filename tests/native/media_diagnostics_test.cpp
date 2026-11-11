// SPDX-License-Identifier: GPL-3.0-or-later
#include "clearcast_media_diagnostics.h"
#include <cstdio>
#include <clocale>

static int checks = 0, failures = 0;
static void check(bool value, const char* message) {
    ++checks;
    if (!value) { ++failures; std::fprintf(stderr, "FAIL: %s\n", message); }
}
static void set_caps(GstElement* decoder, const char* name, const char* text) {
    GstPad* pad = gst_element_get_static_pad(decoder, name);
    gst_pad_set_active(pad, TRUE);
    GstCaps* caps = gst_caps_from_string(text);
    GstEvent* event = gst_event_new_caps(caps);
    check(gst_pad_store_sticky_event(pad, event) == GST_FLOW_OK, "fixture stores negotiated pad caps");
    gst_event_unref(event); gst_caps_unref(caps); gst_object_unref(pad);
}
int main(int argc, char** argv) {
    if (argc > 1) {
        gchar* plugins = g_build_filename(argv[1], "lib", "gstreamer-1.0", nullptr);
        g_setenv("GST_PLUGIN_PATH", plugins, TRUE);
        g_setenv("GST_PLUGIN_SYSTEM_PATH", "", TRUE);
        gst_registry_fork_set_enabled(FALSE); g_free(plugins);
    }
    gst_init(nullptr, nullptr);
    using namespace clearcast_diagnostics;
    Info empty = collect(nullptr, true, true);
    check(empty.width == 0 && empty.audio_bitrate == 0 && empty.frame_rate == 0, "missing stream stays unknown");
    GstElement* pipeline = gst_pipeline_new("diagnostics-fixture");
    GstElement* video = gst_element_factory_make("avdec_h264", nullptr);
    GstElement* audio = gst_element_factory_make("avdec_aac", nullptr);
    check(video && audio, "controlled decoders are available");
    if (!video || !audio) return 1;
    gst_bin_add_many(GST_BIN(pipeline), video, audio, nullptr);
    set_caps(video, "src", "video/x-raw,width=640,height=360,framerate=30/1");
    set_caps(video, "sink", "video/x-h264,width=1920,height=1080,framerate=30000/1001");
    set_caps(audio, "src", "audio/x-raw,rate=48000,channels=2");
    set_caps(audio, "sink", "audio/mpeg,mpegversion=4,rate=44100,channels=6");
    GstPad* pad = gst_element_get_static_pad(video, "src");
    GstTagList* list = gst_tag_list_new(GST_TAG_BITRATE, guint(4500000), nullptr);
    GstEvent* event = gst_event_new_tag(list);
    gst_pad_store_sticky_event(pad, event); gst_event_unref(event); gst_object_unref(pad);
    Info value = collect(pipeline, true, true);
    check(value.video_codec == "H.264" && value.audio_codec == "AAC", "codec follows negotiated encoded caps");
    check(value.width == 1920 && value.height == 1080 && std::abs(value.frame_rate - 30000.0 / 1001) < .00001,
        "encoded source geometry and fractional FPS take precedence over output format");
    check(value.sample_rate == 44100 && value.channels == 6, "audio source caps take precedence over converted output");
    check(value.video_bitrate == 4500000 && value.audio_bitrate == 0, "reported bitrate stays scoped to its stream and absent bitrate stays unknown");
    Info audio_only = collect(pipeline, false, true);
    check(audio_only.video_codec.empty() && audio_only.width == 0 && audio_only.audio_codec == "AAC", "music snapshots omit video statistics");
    GstCaps* invalid = gst_caps_from_string("video/x-h264,width=40000,height=1080,framerate=2000/1");
    Info bounded;
    caps(bounded, invalid, true, false, true); gst_caps_unref(invalid);
    check(bounded.width == 0 && bounded.height == 0 && bounded.frame_rate == 0, "unreasonable source dimensions and FPS stay unknown");
    std::setlocale(LC_NUMERIC, "de_DE.UTF-8");
    std::string encoded = json(value);
    check(encoded.find("\"frameRate\":29.970030") != std::string::npos && encoded.size() < 512,
        "diagnostics use locale-independent bounded JSON");
    gst_object_unref(pipeline);
    check(collect(nullptr, true, true).video_codec.empty(), "disposed stream cannot contribute previous diagnostics");
    std::printf("Media diagnostics: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
