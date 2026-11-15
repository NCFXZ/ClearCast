/* Real appsrc/bus slow-consumer checks with silent sinks; no receiver ports.
 * SPDX-License-Identifier: GPL-3.0-or-later */
#include "clearcast_appsrc_backpressure.h"
#include "clearcast_bus_backpressure.h"
#include "clearcast_event_queue.h"
#include <atomic>
#include <thread>
#include <vector>
#include <mutex>
#include <array>
#include <cstdio>
#include <cstdlib>

static gint cancelled;
extern "C" gboolean clearcast_media_input_cancelled(void) { return g_atomic_int_get(&cancelled); }
static unsigned checks;
#define CHECK(value) do { ++checks; if (!(value)) { fprintf(stderr, "FAIL %u line %d: %s\n", checks, __LINE__, #value); exit(1); } } while (0)

struct Fixture {
    struct Packet { guint64 number; GstClockTime pts, dts, duration; GstBufferFlags flags; bool payload; };
    GstElement *pipeline, *source, *sink;
    GstPad *pad;
    gulong block;
    std::atomic<bool> entered{false};
    std::mutex lock;
    std::vector<Packet> received;
    static GstPadProbeReturn stopped(GstPad*, GstPadProbeInfo*, gpointer context) {
        static_cast<Fixture*>(context)->entered = true;
        return GST_PAD_PROBE_OK;
    }
    static void handoff(GstElement*, GstBuffer *buffer, GstPad*, gpointer context) {
        auto *fixture = static_cast<Fixture*>(context);
        bool payload = true;
        if (GST_CLOCK_TIME_IS_VALID(GST_BUFFER_DURATION(buffer))) {
            GstMapInfo map;
            payload = gst_buffer_map(buffer, &map, GST_MAP_READ);
            if (payload) {
                for (gsize i = 0; i < map.size; ++i)
                    if (map.data[i] != guint8(GST_BUFFER_OFFSET(buffer))) { payload = false; break; }
                gst_buffer_unmap(buffer, &map);
            }
        }
        std::lock_guard<std::mutex> lock(fixture->lock);
        fixture->received.push_back({GST_BUFFER_OFFSET(buffer), GST_BUFFER_PTS(buffer), GST_BUFFER_DTS(buffer),
            GST_BUFFER_DURATION(buffer), static_cast<GstBufferFlags>(GST_BUFFER_FLAGS(buffer)), payload});
    }
    explicit Fixture(bool video) {
        pipeline = gst_parse_launch("appsrc name=input is-live=true format=time ! fakesink name=output sync=false async=false signal-handoffs=true", nullptr);
        CHECK(pipeline);
        source = gst_bin_get_by_name(GST_BIN(pipeline), "input");
        sink = gst_bin_get_by_name(GST_BIN(pipeline), "output");
        clearcast_appsrc_configure(source, video);
        CHECK(gst_app_src_get_max_bytes(GST_APP_SRC(source)) == guint64(video ? 68 : 4) * 1024 * 1024);
        CHECK(gst_app_src_get_max_buffers(GST_APP_SRC(source)) == guint64(video ? 120 : 256));
        CHECK(gst_app_src_get_max_time(GST_APP_SRC(source)) == 2 * GST_SECOND);
        CHECK(gst_app_src_get_leaky_type(GST_APP_SRC(source)) == GST_APP_LEAKY_TYPE_NONE);
        pad = gst_element_get_static_pad(sink, "sink");
        block = gst_pad_add_probe(pad, GstPadProbeType(GST_PAD_PROBE_TYPE_BLOCK | GST_PAD_PROBE_TYPE_BUFFER), stopped, this, nullptr);
        g_signal_connect(sink, "handoff", G_CALLBACK(handoff), this);
        CHECK(gst_element_set_state(pipeline, GST_STATE_PLAYING) != GST_STATE_CHANGE_FAILURE);
        CHECK(push(0) == GST_FLOW_OK);
        gint64 until = g_get_monotonic_time() + G_TIME_SPAN_SECOND;
        while (!entered && g_get_monotonic_time() < until) g_usleep(1000);
        CHECK(entered);
    }
    static GstClockTime pts(guint64 number) { return 10 * GST_SECOND + number * GST_MSECOND; }
    GstFlowReturn push(guint64 number, gsize size = 4096, bool timestamped = false,
        GstClockTime duration = GST_MSECOND) {
        GstBuffer *buffer = gst_buffer_new_allocate(nullptr, size, nullptr);
        GST_BUFFER_OFFSET(buffer) = number;
        if (timestamped) {
            gst_buffer_memset(buffer, 0, guint8(number), size);
            GST_BUFFER_PTS(buffer) = pts(number);
            GST_BUFFER_DTS(buffer) = pts(number) - GST_MSECOND / 2;
            GST_BUFFER_DURATION(buffer) = duration;
            if (number & 1) GST_BUFFER_FLAG_SET(buffer, GST_BUFFER_FLAG_DELTA_UNIT);
            if (number & 2) GST_BUFFER_FLAG_SET(buffer, GST_BUFFER_FLAG_MARKER);
        }
        return clearcast_appsrc_push_buffer(source, buffer);
    }
    void fill(bool timestamped = false) {
        guint64 limit = gst_app_src_get_max_buffers(GST_APP_SRC(source));
        for (guint64 i = 1; i <= limit; ++i) CHECK(push(i, 4096, timestamped) == GST_FLOW_OK);
        CHECK(gst_app_src_get_current_level_buffers(GST_APP_SRC(source)) == limit);
    }
    void unblock() { if (block) { gst_pad_remove_probe(pad, block); block = 0; } }
    ~Fixture() {
        unblock(); gst_element_set_state(pipeline, GST_STATE_NULL);
        gst_object_unref(pad); gst_object_unref(source); gst_object_unref(sink); gst_object_unref(pipeline);
        g_atomic_int_set(&cancelled, 0);
    }
};

static void packet_unchanged(const Fixture::Packet &packet, guint64 expected) {
    CHECK(packet.number == expected);
    CHECK(packet.payload);
    CHECK(packet.pts == Fixture::pts(expected));
    CHECK(packet.dts == Fixture::pts(expected) - GST_MSECOND / 2);
    CHECK(packet.duration == GST_MSECOND);
    CHECK(bool(packet.flags & GST_BUFFER_FLAG_DELTA_UNIT) == bool(expected & 1));
    CHECK(bool(packet.flags & GST_BUFFER_FLAG_MARKER) == bool(expected & 2));
}

static void normal_flow(bool video) {
    Fixture f(video); f.unblock();
    for (guint64 i = 1; i <= 64; ++i) CHECK(f.push(i, 4096, true) == GST_FLOW_OK);
    const gint64 until = g_get_monotonic_time() + G_TIME_SPAN_SECOND;
    for (;;) {
        { std::lock_guard<std::mutex> lock(f.lock); if (f.received.size() == 65) break; }
        CHECK(g_get_monotonic_time() < until); g_usleep(1000);
    }
    std::lock_guard<std::mutex> lock(f.lock);
    for (guint64 i = 1; i <= 64; ++i) packet_unchanged(f.received[i], i);
}

static void recovered_stall(bool video) {
    Fixture f(video); f.fill(true);
    std::atomic<bool> done{false}; GstFlowReturn result = GST_FLOW_ERROR;
    const guint64 last = gst_app_src_get_max_buffers(GST_APP_SRC(f.source)) + 1;
    std::thread producer([&] { result = f.push(last, 4096, true); done = true; });
    g_usleep(60000);
    CHECK(!done);
    CHECK(gst_app_src_get_current_level_buffers(GST_APP_SRC(f.source)) == last - 1);
    f.unblock(); producer.join(); CHECK(result == GST_FLOW_OK);
    gint64 until = g_get_monotonic_time() + 2 * G_TIME_SPAN_SECOND;
    for (;;) {
        { std::lock_guard<std::mutex> lock(f.lock); if (f.received.size() == last + 1) break; }
        CHECK(g_get_monotonic_time() < until); g_usleep(1000);
    }
    std::lock_guard<std::mutex> lock(f.lock);
    CHECK(f.received.size() == last + 1);
    for (guint64 i = 0; i <= last; ++i) {
        const auto &packet = f.received[i];
        CHECK(packet.number == i);
        if (!i) continue; // The stream's initial untimed buffer may acquire DISCONT.
        packet_unchanged(packet, i);
    }
}

static void independent_queue_limits(bool video, bool bytes) {
    Fixture f(video);
    const gsize limit = gst_app_src_get_max_bytes(GST_APP_SRC(f.source));
    CHECK(f.push(1, bytes ? limit : 4096, !bytes, 2 * GST_SECOND) == GST_FLOW_OK);
    CHECK(gst_app_src_get_current_level_buffers(GST_APP_SRC(f.source)) == 1);
    CHECK(bytes ? gst_app_src_get_current_level_bytes(GST_APP_SRC(f.source)) == limit :
        gst_app_src_get_current_level_time(GST_APP_SRC(f.source)) >= 2 * GST_SECOND);
    std::atomic<bool> done{false}; GstFlowReturn result = GST_FLOW_ERROR;
    std::thread producer([&] { result = f.push(2); done = true; });
    g_usleep(30000);
    CHECK(!done); // Neither free buffer slots nor a small payload bypass this limit.
    CHECK(gst_app_src_get_current_level_bytes(GST_APP_SRC(f.source)) <= limit);
    CHECK(gst_app_src_get_current_level_buffers(GST_APP_SRC(f.source)) == 1);
    const gint64 start = g_get_monotonic_time();
    g_atomic_int_set(&cancelled, 1);
    producer.join();
    CHECK(result == GST_FLOW_FLUSHING);
    CHECK(g_get_monotonic_time() - start < 500000);
}

static void cancelled_producers_and_restart(bool eos) {
    Fixture f(true); f.fill(true);
    std::array<GstFlowReturn, 4> results;
    std::vector<std::thread> producers;
    std::atomic<unsigned> done{0};
    for (unsigned i = 0; i < results.size(); ++i)
        producers.emplace_back([&, i] { results[i] = f.push(121 + i, 4096, true); ++done; });
    g_usleep(30000); CHECK(done == 0);
    const gint64 start = g_get_monotonic_time();
    if (eos) clearcast_appsrc_end_of_stream(GST_APP_SRC(f.source));
    else g_atomic_int_set(&cancelled, 1);
    for (auto &producer : producers) producer.join();
    CHECK(g_get_monotonic_time() - start < 500000);
    for (auto result : results) CHECK(result == GST_FLOW_FLUSHING);
    CHECK(gst_app_src_get_current_level_buffers(GST_APP_SRC(f.source)) == 120);
    f.unblock();
    CHECK(gst_element_set_state(f.pipeline, GST_STATE_NULL) != GST_STATE_CHANGE_FAILURE);
    clearcast_appsrc_reset(f.source);
    g_atomic_int_set(&cancelled, 0);
    CHECK(gst_element_set_state(f.pipeline, GST_STATE_PLAYING) != GST_STATE_CHANGE_FAILURE);
    CHECK(f.push(1000, 4096, true) == GST_FLOW_OK);
    const gint64 until = g_get_monotonic_time() + G_TIME_SPAN_SECOND;
    for (;;) {
        { std::lock_guard<std::mutex> lock(f.lock); if (!f.received.empty() && f.received.back().number == 1000) break; }
        CHECK(g_get_monotonic_time() < until); g_usleep(1000);
    }
    std::lock_guard<std::mutex> lock(f.lock);
    packet_unchanged(f.received.back(), 1000);
}

static void cancelled_stall(int mode) {
    Fixture f(true); f.fill();
    std::atomic<bool> done{false}; GstFlowReturn result = GST_FLOW_ERROR;
    std::thread producer([&] { result = f.push(121); done = true; });
    g_usleep(30000); CHECK(!done);
    gint64 start = g_get_monotonic_time();
    if (mode == 0) g_atomic_int_set(&cancelled, 1);
    else if (mode == 1) clearcast_appsrc_end_of_stream(GST_APP_SRC(f.source));
    else {
        std::thread stop([&] { gst_element_set_state(f.pipeline, GST_STATE_NULL); });
        // Our artificial pad stall also blocks NULL; releasing that test-only
        // gate allows the real source stop to flush its own queued buffers.
        f.unblock(); stop.join();
    }
    producer.join();
    CHECK(result == GST_FLOW_FLUSHING || (mode == 2 && result == GST_FLOW_OK));
    CHECK(g_get_monotonic_time() - start < 500000);
}

static void permanent_stall() {
    Fixture f(true); f.fill();
    GstBus *bus = gst_element_get_bus(f.pipeline);
    gint64 start = g_get_monotonic_time();
    CHECK(f.push(121) == GST_FLOW_ERROR);
    CHECK(g_get_monotonic_time() - start >= 4900000 && g_get_monotonic_time() - start < 6500000);
    CHECK(gst_app_src_get_current_level_buffers(GST_APP_SRC(f.source)) == 120);
    CHECK(f.push(122) == GST_FLOW_ERROR);
    unsigned errors = 0;
    while (GstMessage *message = gst_bus_pop(bus)) {
        if (GST_MESSAGE_TYPE(message) == GST_MESSAGE_ERROR) ++errors;
        gst_message_unref(message);
    }
    CHECK(errors == 1);
    f.unblock(); gst_element_set_state(f.pipeline, GST_STATE_NULL);
    clearcast_appsrc_reset(f.source);
    CHECK(gst_element_set_state(f.pipeline, GST_STATE_PLAYING) != GST_STATE_CHANGE_FAILURE);
    CHECK(f.push(123) == GST_FLOW_OK);
    gst_object_unref(bus);
}

static void legal_large_access_unit() {
    Fixture f(true);
    CHECK(f.push(1, 64 * 1024 * 1024 + 3 * 128 * 1024) == GST_FLOW_OK);
    CHECK(gst_app_src_get_current_level_bytes(GST_APP_SRC(f.source)) <= 68ULL * 1024 * 1024);
}

static void bus_flood(int mode) {
    GstBus *bus = gst_bus_new(); clearcast_bus_prepare(bus);
    gst_bus_set_sync_handler(bus, clearcast_bus_sync, GINT_TO_POINTER(mode), nullptr);
    GstElement *sink = gst_element_factory_make("fakesink", nullptr);
    for (int i = 0; i < 10000; ++i) {
        gst_bus_post(bus, gst_message_new_application(GST_OBJECT(sink), gst_structure_new_empty("noise")));
        GError *error = g_error_new_literal(GST_RESOURCE_ERROR, GST_RESOURCE_ERROR_NO_SPACE_LEFT, "test");
        gst_bus_post(bus, gst_message_new_error(GST_OBJECT(sink), error, nullptr)); g_error_free(error);
        gst_bus_post(bus, gst_message_new_eos(GST_OBJECT(sink)));
        gst_bus_post(bus, gst_message_new_async_done(GST_OBJECT(sink), GST_CLOCK_TIME_NONE));
        gst_bus_post(bus, gst_message_new_state_changed(GST_OBJECT(sink), GST_STATE_NULL, GST_STATE_READY, GST_STATE_VOID_PENDING));
    }
    unsigned count = 0; bool error = false, eos = false, async = false, state = false;
    while (GstMessage *message = gst_bus_pop(bus)) {
        ++count; clearcast_bus_popped(bus, message);
        error |= GST_MESSAGE_TYPE(message) == GST_MESSAGE_ERROR; eos |= GST_MESSAGE_TYPE(message) == GST_MESSAGE_EOS;
        async |= GST_MESSAGE_TYPE(message) == GST_MESSAGE_ASYNC_DONE; state |= GST_MESSAGE_TYPE(message) == GST_MESSAGE_STATE_CHANGED;
        gst_message_unref(message);
    }
    CHECK(error && eos && async == (mode == 1) && state == (mode == 2));
    CHECK(count == unsigned(mode ? 3 : 2));
    CHECK(gst_bus_post(bus, gst_message_new_eos(GST_OBJECT(sink))));
    gst_bus_set_flushing(bus, TRUE); gst_bus_set_flushing(bus, FALSE); clearcast_bus_reset(bus);
    CHECK(gst_bus_post(bus, gst_message_new_eos(GST_OBJECT(sink))));
    GstMessage *message = gst_bus_pop(bus); CHECK(message); gst_message_unref(message);
    gst_object_unref(sink); gst_object_unref(bus);
}

static void receiver_event_flood() {
    ClearCastEventQueue queue;
    queue.push({Fatal, 0, 0, 0, 55}); queue.push({DecoderChanged, 1, 0, 0, 0});
    for (int i = 0; i < 10000; ++i) queue.push({PairingRequired, i, i, 0, 0});
    queue.push({DecoderChangeFailed, 2, 0, 0, 44});
    ReceiverEvent event; unsigned count = 0, fatal = 0, decoder = 0;
    while (queue.pop(event)) {
        ++count;
        if (event.type == Fatal) { ++fatal; CHECK(event.error == 55); }
        if (event.type == DecoderChangeFailed) { ++decoder; CHECK(event.width == 2 && event.error == 44); }
        CHECK(event.type != DecoderChanged);
    }
    CHECK(count == 64 && fatal == 1 && decoder == 1);
    for (int i = 0; i < 10000; ++i) queue.push({Video, 1280, 720, 264, 0});
    CHECK(queue.pop(event) && event.type == Video); CHECK(!queue.pop(event));
    queue.push({Ready, 0, 0, 0, 0}); queue.clear(); CHECK(!queue.pop(event));
}

int main(int argc, char **argv) {
    if (argc != 2) return 2;
    gchar *plugins = g_build_filename(argv[1], "lib", "gstreamer-1.0", nullptr);
    g_setenv("GST_PLUGIN_SYSTEM_PATH_1_0", plugins, TRUE); g_free(plugins);
    g_setenv("GST_PLUGIN_PATH_1_0", "", TRUE);
    g_setenv("GST_REGISTRY", "clearcast-backpressure-registry.bin", TRUE);
    gst_registry_fork_set_enabled(FALSE); gst_init(nullptr, nullptr);
    normal_flow(false); normal_flow(true); recovered_stall(false); recovered_stall(true);
    independent_queue_limits(false, true); independent_queue_limits(true, true);
    independent_queue_limits(false, false); independent_queue_limits(true, false);
    cancelled_stall(0); cancelled_stall(1); cancelled_stall(2);
    for (int i = 0; i < 8; ++i) cancelled_producers_and_restart(i & 1);
    permanent_stall(); legal_large_access_unit();
    bus_flood(0); bus_flood(1); bus_flood(2); receiver_event_flood();
    printf("PASS %u backpressure checks\n", checks);
}
