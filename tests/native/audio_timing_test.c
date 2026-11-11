/* Real renderer scheduling with synthetic ALAC and a silent test sink.
 * SPDX-License-Identifier: GPL-3.0-or-later */
#include <gst/app/gstappsink.h>
#include "audio_renderer.c"

uint64_t clearcast_media_audio_activity_epoch(void) { return 0; }
uint64_t clearcast_media_audio_activity_epoch_for_source(uint64_t source) { return 0; }
void clearcast_media_audio_activity_publish(uint64_t epoch, double energy, double density) {}
void clearcast_media_audio_activity_beat(uint64_t epoch, double strength, uint64_t age) {}
void clearcast_media_audio_activity_invalidate(uint64_t epoch) {}
void clearcast_media_audio_activity_boundary(void) {}
void clearcast_media_audio_pipeline(GstElement* pipeline, uint64_t epoch) {}
gboolean clearcast_media_input_cancelled(void) { return FALSE; }

static gint rendered;
static GstClockTime rendered_at;
static GstClock* test_clock;
static int checks, failures;
static void check(gboolean value, const char* message) {
    ++checks;
    if (!value) { ++failures; g_printerr("FAIL: %s\n", message); }
}
static void received(GstElement* sink, GstBuffer* buffer, GstPad* pad, gpointer data) {
    rendered_at = gst_clock_get_time(test_clock);
    g_atomic_int_set(&rendered, 1);
}
static GstSample* encode_alac(void) {
    GError* error = NULL;
    GstElement* pipeline = gst_parse_launch("appsrc name=input ! audioconvert ! avenc_alac ! appsink name=encoded sync=false async=false", &error);
    check(pipeline && !error, "synthetic ALAC encoder is available");
    if (error) g_clear_error(&error);
    if (!pipeline) return NULL;
    GstElement* input = gst_bin_get_by_name(GST_BIN(pipeline), "input");
    GstElement* output = gst_bin_get_by_name(GST_BIN(pipeline), "encoded");
    GstCaps* caps = gst_caps_from_string("audio/x-raw,format=F32LE,rate=44100,channels=2,layout=interleaved");
    g_object_set(input, "caps", caps, "format", GST_FORMAT_TIME, NULL);
    gst_caps_unref(caps);
    gst_element_set_state(pipeline, GST_STATE_PLAYING);
    GstBuffer* raw = gst_buffer_new_allocate(NULL, 4096 * 2 * sizeof(float), NULL);
    gst_buffer_memset(raw, 0, 0, gst_buffer_get_size(raw));
    GST_BUFFER_PTS(raw) = 0;
    GST_BUFFER_DURATION(raw) = gst_util_uint64_scale(4096, GST_SECOND, 44100);
    gst_app_src_push_buffer(GST_APP_SRC(input), raw);
    gst_app_src_end_of_stream(GST_APP_SRC(input));
    GstSample* sample = gst_app_sink_try_pull_sample(GST_APP_SINK(output), 5 * GST_SECOND);
    check(sample != NULL, "real ALAC payload is encoded from synthetic silence");
    gst_element_set_state(pipeline, GST_STATE_NULL);
    gst_object_unref(input); gst_object_unref(output); gst_object_unref(pipeline);
    return sample;
}

static void run_case(GstSample* sample, gboolean scheduled) {
    bool audio_sync = scheduled, video_sync = true;
    logger_t* log = logger_init();
    logger_set_level(log, LOGGER_ERR);
    check(audio_renderer_init(log, "fakesink name=timing_sink signal-handoffs=true async=false", &audio_sync, &video_sync, ""),
        "production renderer initializes with an isolated silent sink");
    GstElement* sink = gst_bin_get_by_name(GST_BIN(renderer_type[1]->pipeline), "timing_sink");
    gboolean enabled = FALSE;
    g_object_get(sink, "sync", &enabled, NULL);
    check(enabled == scheduled, "ALAC sink follows the configured timestamp policy");
    unsigned char codec = 2;
    audio_renderer_start(&codec);
    g_object_set(renderer->appsrc, "caps", gst_sample_get_caps(sample), NULL);
    test_clock = gst_element_get_clock(renderer->pipeline);
    g_signal_connect(sink, "handoff", G_CALLBACK(received), NULL);
    g_atomic_int_set(&rendered, 0);
    GstMapInfo map;
    GstBuffer* encoded = gst_sample_get_buffer(sample);
    check(gst_buffer_map(encoded, &map, GST_MAP_READ), "encoded ALAC packet can be mapped");
    int length = (int)map.size;
    unsigned short sequence = 1;
    uint64_t target = gst_clock_get_time(test_clock) + 300 * GST_MSECOND;
    audio_renderer_render_buffer_with_epoch(map.data, &length, &sequence, &target, 0);
    gint64 deadline = g_get_monotonic_time() + 3000000;
    while (!g_atomic_int_get(&rendered) && g_get_monotonic_time() < deadline) g_usleep(1000);
    check(g_atomic_int_get(&rendered), "production ALAC renderer decodes and outputs the test frame");
    if (g_atomic_int_get(&rendered)) {
        gint64 offset = GST_CLOCK_DIFF(target, rendered_at);
        check(scheduled ? offset >= -20 * (gint64)GST_MSECOND && offset < 500 * (gint64)GST_MSECOND :
            offset < -100 * (gint64)GST_MSECOND, scheduled ? "scheduled music waits for sender presentation time" :
            "old unsynchronized policy reproduces audio leading the sender timeline");
        g_print("scheduled=%d presentationOffsetMs=%.2f\n", scheduled, (double)offset / GST_MSECOND);
    }
    gst_buffer_unmap(encoded, &map);
    audio_renderer_stop(); audio_renderer_destroy();
    gst_object_unref(sink); gst_object_unref(test_clock); logger_destroy(log);
}

static void pipeline_finalized(gpointer data, GObject* object) {
    g_atomic_int_inc((gint*)data);
}

static void inactive_renderer_teardown(void) {
    bool audio_sync = true, video_sync = true;
    logger_t* log = logger_init();
    logger_set_level(log, LOGGER_ERR);
    check(audio_renderer_init(log, "fakesink async=false", &audio_sync, &video_sync, ""),
        "all audio formats initialize for inactive teardown regression");
    GstElement* held[NFORMATS] = {0};
    gint finalized = 0;
    for (int i = 0; i < NFORMATS; ++i) {
        if (!renderer_type[i]) continue;
        held[i] = gst_object_ref(renderer_type[i]->pipeline);
        g_object_weak_ref(G_OBJECT(held[i]), pipeline_finalized, &finalized);
        check(gst_element_set_state(held[i], GST_STATE_READY) != GST_STATE_CHANGE_FAILURE,
            "an inactive codec pipeline can reach READY before receiver shutdown");
    }
    check(renderer == NULL, "no current codec is selected during inactive teardown");
    audio_renderer_destroy();
    for (int i = 0; i < NFORMATS; ++i) {
        if (!held[i]) continue;
        GstState current, pending;
        gst_element_get_state(held[i], &current, &pending, 0);
        check(current == GST_STATE_NULL && pending == GST_STATE_VOID_PENDING,
            "receiver destruction stops every inactive codec before releasing it");
        check(renderer_type[i] == NULL, "destroy clears each codec slot");
        // Keep a failing regression from itself disposing a non-NULL pipeline.
        gst_element_set_state(held[i], GST_STATE_NULL);
        gst_object_unref(held[i]);
    }
    check(finalized == NFORMATS, "destroy releases every codec pipeline without retained references");
    logger_destroy(log);
}

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    gchar* plugins = g_build_filename(argv[1], "lib", "gstreamer-1.0", NULL);
    g_setenv("GST_PLUGIN_SYSTEM_PATH_1_0", plugins, TRUE);
    g_setenv("GST_PLUGIN_PATH_1_0", "", TRUE);
    g_setenv("GST_REGISTRY", "clearcast-timing-fixture-registry.bin", TRUE);
    gst_registry_fork_set_enabled(FALSE);
    gst_init(NULL, NULL);
    g_free(plugins);
    GstSample* sample = encode_alac();
    if (sample) { run_case(sample, FALSE); run_case(sample, TRUE); gst_sample_unref(sample); }
    inactive_renderer_teardown();
    g_print("%s %d audio timing checks\n", failures ? "FAIL" : "PASS", checks);
    return failures ? 1 : 0;
}
