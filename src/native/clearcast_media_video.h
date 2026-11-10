/* Direct AirPlay URL/HLS playback on ClearCast's existing video host.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#pragma once

#include <cmath>
#include <mutex>
#include <gst/video/videooverlay.h>
#include "clearcast_media_http.h"
std::mutex direct_video_lock;
std::mutex direct_video_pipeline_lock;
GstElement* direct_video_pipeline = nullptr;
std::string direct_video_uri;
std::atomic<bool> direct_video_active(false);
std::atomic<unsigned int> direct_video_generation(0);
std::atomic<int> direct_video_width(0), direct_video_height(0);
bool direct_video_start_pending = false;
bool direct_video_stop_pending = false;
bool direct_video_playing = true;
float direct_video_start_position = 0;
int64_t direct_video_position = 0, direct_video_duration = 0;
uintptr_t direct_video_window = 0;
std::atomic<int> direct_video_decoder(0);
int direct_video_decoder_pending = -1;
int direct_video_decoder_previous = 0;
float direct_video_decoder_position = 0;
gint64 direct_video_decoder_deadline = 0;
std::atomic<double> direct_video_volume(1.0);
uint64_t direct_video_session = 0, direct_video_session_counter = 0;
bool direct_video_seekable = false;
bool direct_video_live = false;
int64_t direct_video_seek_start = 0, direct_video_seek_end = 0;

extern "C" bool clearcast_direct_video_is_active() { return direct_video_active.load(); }

static bool clearcast_direct_video_controls(uint64_t& session, int& controls, int64_t& start, int64_t& end, bool& live) {
    std::lock_guard<std::mutex> lock(direct_video_lock);
    session = direct_video_session;
    controls = direct_video_active && direct_video_pipeline && direct_video_width > 0 ? 1 | (direct_video_seekable ? 8 : 0) : 0;
    start = direct_video_seek_start; end = direct_video_seek_end;
    live = direct_video_live;
    return direct_video_active;
}

static GstElement* clearcast_direct_video_ref() {
    std::lock_guard<std::mutex> lock(direct_video_lock);
    return direct_video_pipeline ? GST_ELEMENT(gst_object_ref(direct_video_pipeline)) : nullptr;
}

static bool clearcast_direct_video_snapshot(bool& playing, int64_t& position, int64_t& duration) {
    std::lock_guard<std::mutex> lock(direct_video_lock);
    playing = direct_video_playing;
    position = direct_video_position;
    duration = direct_video_duration;
    return direct_video_active;
}

static void clearcast_direct_video_dispose() {
    std::lock_guard<std::mutex> lifecycle(direct_video_pipeline_lock);
    GstElement* pipeline;
    {
        std::lock_guard<std::mutex> lock(direct_video_lock);
        pipeline = direct_video_pipeline;
        direct_video_pipeline = nullptr;
    }
    if (!pipeline) return;
    gst_element_set_state(pipeline, GST_STATE_NULL);
    gst_object_unref(pipeline);
}

static void clearcast_direct_video_clear() {
    bool active = direct_video_active.exchange(false);
    ++direct_video_generation;
    {
        std::lock_guard<std::mutex> lock(direct_video_lock);
        direct_video_uri.clear();
        direct_video_start_pending = direct_video_stop_pending = false;
        direct_video_position = direct_video_duration = 0;
        direct_video_seekable = false;
        direct_video_live = false;
        direct_video_seek_start = direct_video_seek_end = 0;
        direct_video_session = 0;
        direct_video_decoder_pending = -1;
    }
    clearcast_direct_video_dispose();
    if (raop) raop_clear_video_owner(raop);
    if (active) {
        write_diagnostic("direct-video ended");
        mirror_active = false;
        set_stats_sink(nullptr);
        enqueue_event({MirrorEnded, 0, 0, 0, 0});
    }
}

static GstBusSyncReply clearcast_direct_video_sync(GstBus*, GstMessage* message, gpointer) {
    clearcast_video_handle_context(message);
    if (gst_is_video_overlay_prepare_window_handle_message(message)) {
        if (direct_video_window) gst_video_overlay_set_window_handle(
            GST_VIDEO_OVERLAY(GST_MESSAGE_SRC(message)), static_cast<guintptr>(direct_video_window));
        gst_message_unref(message);
        return GST_BUS_DROP;
    }
    return GST_BUS_PASS;
}

static GstPadProbeReturn clearcast_direct_video_caps(GstPad*, GstPadProbeInfo* info, gpointer generation) {
    if (!direct_video_active || GPOINTER_TO_UINT(generation) != direct_video_generation) return GST_PAD_PROBE_OK;
    GstEvent* event = GST_PAD_PROBE_INFO_EVENT(info);
    if (!event || GST_EVENT_TYPE(event) != GST_EVENT_CAPS) return GST_PAD_PROBE_OK;
    GstCaps* caps = nullptr;
    gst_event_parse_caps(event, &caps);
    GstVideoInfo video;
    if (!caps || !gst_video_info_from_caps(&video, caps) || video.width <= 0 || video.height <= 0) return GST_PAD_PROBE_OK;
    int previous_width = direct_video_width.exchange(video.width);
    int previous_height = direct_video_height.exchange(video.height);
    if (previous_width != video.width || previous_height != video.height) {
        enqueue_event({Video, video.width, video.height, 0, 0});
    }
    return GST_PAD_PROBE_OK;
}

static gint clearcast_direct_video_select(GstElement*, GstPad*, GstCaps*, GstElementFactory* factory, gpointer) {
    if (!gst_element_factory_list_is_type(factory, GST_ELEMENT_FACTORY_TYPE_DECODER | GST_ELEMENT_FACTORY_TYPE_MEDIA_VIDEO)) return 0;
    int decoder = direct_video_decoder;
    bool hardware = gst_element_factory_list_is_type(factory, GST_ELEMENT_FACTORY_TYPE_HARDWARE);
    return decoder == CLEARCAST_DECODER_AUTOMATIC || (decoder == CLEARCAST_DECODER_HARDWARE ? hardware : !hardware) ? 0 : 2;
}

static void clearcast_direct_video_element(GstBin*, GstBin*, GstElement* element, gpointer) {
    if (g_signal_lookup("autoplug-select", G_OBJECT_TYPE(element)) &&
        !g_object_get_data(G_OBJECT(element), "clearcast-direct-policy")) {
        g_object_set_data(G_OBJECT(element), "clearcast-direct-policy", GINT_TO_POINTER(1));
        g_signal_connect(element, "autoplug-select", G_CALLBACK(clearcast_direct_video_select), nullptr);
    }
}

static bool clearcast_direct_video_build(const std::string& uri, float position, unsigned int generation) {
    std::lock_guard<std::mutex> lifecycle(direct_video_pipeline_lock);
    if (!direct_video_active || direct_video_generation != generation) return false;
    if (!clearcast_http_uri_allowed(uri.c_str()) || !direct_video_window) return false;
    GstElement* pipeline = gst_element_factory_make("playbin", "clearcast-direct-player");
    GstElement* sink = gst_element_factory_make("d3d11videosink", "clearcast-direct-video");
    GstElement* audio = gst_element_factory_make(audiosink.c_str(), "clearcast-direct-audio");
    if (!pipeline || !sink || !audio) {
        if (pipeline) gst_object_unref(pipeline);
        if (sink) gst_object_unref(sink);
        if (audio) gst_object_unref(audio);
        return false;
    }
    gst_object_ref_sink(pipeline);
    gst_object_ref_sink(sink);
    gst_object_ref_sink(audio);
    g_object_set(sink, "force-aspect-ratio", TRUE, "enable-navigation-events", FALSE, nullptr);
    gst_video_overlay_set_window_handle(GST_VIDEO_OVERLAY(sink), static_cast<guintptr>(direct_video_window));
    bool playing;
    { std::lock_guard<std::mutex> lock(direct_video_lock); playing = direct_video_playing; }
    g_object_set(pipeline, "uri", uri.c_str(), "video-sink", sink, "audio-sink", audio,
        "volume", direct_video_volume.load(), nullptr);
    clearcast_video_watch_pipeline(pipeline);
    g_signal_connect(pipeline, "deep-element-added", G_CALLBACK(clearcast_direct_video_element), nullptr);
    GstPad* pad = gst_element_get_static_pad(sink, "sink");
    if (pad) {
        gst_pad_add_probe(pad, GST_PAD_PROBE_TYPE_EVENT_DOWNSTREAM, clearcast_direct_video_caps,
            GUINT_TO_POINTER(generation), nullptr);
        gst_object_unref(pad);
    }
    GstBus* bus = gst_element_get_bus(pipeline);
    gst_bus_set_sync_handler(bus, clearcast_direct_video_sync, nullptr, nullptr);
    gst_object_unref(bus);
    {
        std::lock_guard<std::mutex> lock(direct_video_lock);
        if (!direct_video_active || direct_video_generation != generation) {
            gst_object_unref(pipeline);
            gst_object_unref(sink);
            gst_object_unref(audio);
            return false;
        }
        direct_video_pipeline = pipeline;
        direct_video_position = static_cast<int64_t>(position * 1000.0);
    }
    mirror_active = true;
    set_stats_sink(sink);
    gst_object_unref(sink);
    gst_object_unref(audio);
    return gst_element_set_state(pipeline, playing ? GST_STATE_PLAYING : GST_STATE_PAUSED) != GST_STATE_CHANGE_FAILURE;
}

static bool clearcast_direct_video_set_window(uintptr_t window, int decoder) {
    direct_video_window = window;
    direct_video_decoder = decoder;
    if (!window) return true;
    HMODULE module = nullptr;
    wchar_t path[32768];
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&clearcast_direct_video_set_window), &module)) return false;
    DWORD length = GetModuleFileNameW(module, path, ARRAYSIZE(path));
    if (!length || length == ARRAYSIZE(path)) return false;
    std::wstring directory(path, length);
    directory.resize(directory.find_last_of(L"\\/"));
    std::unique_ptr<gchar, decltype(&g_free)> modules(
        g_utf16_to_utf8(reinterpret_cast<const gunichar2*>((directory + L"\\lib\\gio\\modules").c_str()), -1, nullptr, nullptr, nullptr), &g_free);
    std::unique_ptr<gchar, decltype(&g_free)> certificates(
        g_utf16_to_utf8(reinterpret_cast<const gunichar2*>((directory + L"\\certs\\ca-bundle.crt").c_str()), -1, nullptr, nullptr, nullptr), &g_free);
    if (!modules || !certificates) return false;
    g_setenv("GIO_EXTRA_MODULES", modules.get(), TRUE);
    return clearcast_http_policy_initialize(certificates.get());
}

extern "C" void clearcast_direct_video_play(const char* uri, float position) {
    if (!clearcast_http_uri_allowed(uri) || !std::isfinite(position) || position < 0 || position > 7 * 86400) return;
    std::lock_guard<std::mutex> lock(direct_video_lock);
    direct_video_uri = uri;
    direct_video_start_position = position;
    direct_video_start_pending = true;
    direct_video_stop_pending = false;
    direct_video_playing = true;
    direct_video_active = true;
    direct_video_session = (uint64_t(1) << 63) | ++direct_video_session_counter;
    direct_video_seekable = false;
    direct_video_live = false;
    ++direct_video_generation;
}

extern "C" bool clearcast_direct_video_uri_allowed(const char* uri) {
    return clearcast_http_uri_allowed(uri);
}

extern "C" void clearcast_direct_video_stop() {
    if (direct_video_active) write_diagnostic("direct-video stop requested");
    std::lock_guard<std::mutex> lock(direct_video_lock);
    direct_video_stop_pending = true;
}

extern "C" void clearcast_direct_video_rate(float rate) {
    if (!std::isfinite(rate) || (rate != 0.0f && rate != 1.0f)) return;
    {
        std::lock_guard<std::mutex> lock(direct_video_lock);
        direct_video_playing = rate != 0;
    }
    GstElement* pipeline = clearcast_direct_video_ref();
    if (pipeline) {
        gst_element_set_state(pipeline, rate ? GST_STATE_PLAYING : GST_STATE_PAUSED);
        gst_object_unref(pipeline);
    }
}

extern "C" void clearcast_direct_video_seek(float position) {
    if (!std::isfinite(position) || position < 0 || position > 7 * 86400) return;
    GstElement* pipeline = clearcast_direct_video_ref();
    if (pipeline) {
        gst_element_seek_simple(pipeline, GST_FORMAT_TIME, static_cast<GstSeekFlags>(GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_KEY_UNIT),
            static_cast<gint64>(position * GST_SECOND));
        gst_object_unref(pipeline);
    }
}

extern "C" void clearcast_direct_video_info(playback_info_t* result) {
    if (!result) return;
    memset(result, 0, sizeof(*result));
    bool playing;
    int64_t position, duration;
    bool active = clearcast_direct_video_snapshot(playing, position, duration);
    result->duration = active ? duration / 1000.0 : -1.0;
    result->position = active ? position / 1000.0 : -1.0;
    result->seek_duration = result->duration;
    result->rate = playing ? 1.0f : 0.0f;
    result->ready_to_play = active && direct_video_width > 0;
    result->playback_likely_to_keep_up = result->ready_to_play;
    result->playback_buffer_full = result->ready_to_play;
}

extern "C" float clearcast_direct_video_playlist_remove() {
    bool playing;
    int64_t position, duration;
    clearcast_direct_video_snapshot(playing, position, duration);
    clearcast_direct_video_rate(0);
    return static_cast<float>(position / 1000.0);
}

static bool clearcast_direct_video_set_decoder(int decoder) {
    if (!direct_video_active) return false;
    std::lock_guard<std::mutex> lock(direct_video_lock);
    if (direct_video_decoder_pending >= 0) return false;
    direct_video_decoder_previous = direct_video_decoder;
    direct_video_decoder_position = static_cast<float>(direct_video_position / 1000.0);
    direct_video_decoder = decoder;
    direct_video_decoder_pending = decoder;
    direct_video_start_position = static_cast<float>(direct_video_position / 1000.0);
    direct_video_start_pending = true;
    ++direct_video_generation;
    return true;
}

static bool clearcast_direct_video_decoder_complete(bool failed) {
    int decoder;
    {
        std::lock_guard<std::mutex> lock(direct_video_lock);
        decoder = direct_video_decoder_pending;
        if (decoder < 0) return false;
        direct_video_decoder_pending = -1;
        if (failed) {
            direct_video_decoder = direct_video_decoder_previous;
            direct_video_start_position = direct_video_decoder_position;
            direct_video_start_pending = true;
        }
    }
    if (!failed) clearcast_video_initialize_decoder(decoder);
    clearcast_decoder_result(decoder, failed ? ERROR_NOT_SUPPORTED : 0);
    return failed;
}

static void clearcast_direct_video_set_volume(double volume) {
    if (!std::isfinite(volume)) return;
    direct_video_volume = std::max(0.0, std::min(1.0, volume));
    GstElement* pipeline = clearcast_direct_video_ref();
    if (pipeline) {
        g_object_set(pipeline, "volume", direct_video_volume.load(), nullptr);
        gst_object_unref(pipeline);
    }
}

static void clearcast_direct_video_poll() {
    bool stop, start;
    std::string uri;
    float position;
    unsigned int generation;
    {
        std::lock_guard<std::mutex> lock(direct_video_lock);
        stop = direct_video_stop_pending;
        start = direct_video_start_pending;
        uri = direct_video_uri;
        position = direct_video_start_position;
        generation = direct_video_generation;
        direct_video_start_pending = direct_video_stop_pending = false;
    }
    if (stop) { clearcast_direct_video_clear(); return; }
    if (start) {
        clearcast_direct_video_dispose();
        direct_video_width = direct_video_height = 0;
        {
            clearcast_media::Lock lock(clearcast_media::state_lock);
            clearcast_media::clear_audio_locked();
        }
        audio_renderer_stop();
        enqueue_event({Connecting, 0, 0, 0, 0});
        if (!clearcast_direct_video_build(uri, position, generation)) {
            if (direct_video_generation != generation) return;
            write_diagnostic("direct-video failed stage=initialize");
            if (clearcast_direct_video_decoder_complete(true)) return;
            clearcast_direct_video_clear();
            return;
        }
        direct_video_decoder_deadline = g_get_monotonic_time() + 5 * G_TIME_SPAN_SECOND;
    }
    GstElement* pipeline = clearcast_direct_video_ref();
    if (!pipeline) return;
    GstBus* bus = gst_element_get_bus(pipeline);
    bool ended = false, failed = false;
    while (GstMessage* message = gst_bus_pop(bus)) {
        if (GST_MESSAGE_TYPE(message) == GST_MESSAGE_EOS) ended = true;
        if (GST_MESSAGE_TYPE(message) == GST_MESSAGE_ERROR) { ended = failed = true; write_diagnostic("direct-video failed stage=playback"); }
        if (GST_MESSAGE_TYPE(message) == GST_MESSAGE_ASYNC_DONE && position > 0) {
            clearcast_direct_video_seek(position);
            position = 0;
            std::lock_guard<std::mutex> lock(direct_video_lock);
            direct_video_start_position = 0;
        }
        gst_message_unref(message);
    }
    gst_object_unref(bus);
    gint64 current = 0, duration = 0;
    gst_element_query_position(pipeline, GST_FORMAT_TIME, &current);
    gst_element_query_duration(pipeline, GST_FORMAT_TIME, &duration);
    GstQuery* seeking = gst_query_new_seeking(GST_FORMAT_TIME);
    gboolean seekable = FALSE; gint64 seek_start = 0, seek_end = 0;
    if (gst_element_query(pipeline, seeking)) gst_query_parse_seeking(seeking, nullptr, &seekable, &seek_start, &seek_end);
    gst_query_unref(seeking);
    GstQuery* latency = gst_query_new_latency();
    gboolean live = FALSE;
    if (gst_element_query(pipeline, latency)) gst_query_parse_latency(latency, &live, nullptr, nullptr);
    gst_query_unref(latency);
    gst_object_unref(pipeline);
    bool decoder_complete = false;
    {
        std::lock_guard<std::mutex> lock(direct_video_lock);
        if (direct_video_generation != generation) return;
        direct_video_position = std::max<gint64>(0, current / GST_MSECOND);
        direct_video_duration = std::max<gint64>(0, duration / GST_MSECOND);
        direct_video_seek_start = std::max<gint64>(0, seek_start / GST_MSECOND);
        direct_video_seek_end = std::max<gint64>(0, (seek_end >= 0 ? seek_end : duration) / GST_MSECOND);
        direct_video_seekable = seekable && direct_video_seek_end > direct_video_seek_start;
        // Audio sinks can contribute live latency to finite files too.
        // An open-ended timeline or rolling seek window distinguishes live media.
        direct_video_live = live && (duration <= 0 || seek_start > 0);
        if (direct_video_decoder_pending >= 0 && (direct_video_width > 0 || ended || g_get_monotonic_time() > direct_video_decoder_deadline)) {
            decoder_complete = true;
            if (direct_video_width <= 0) failed = true;
        }
    }
    if (decoder_complete && clearcast_direct_video_decoder_complete(failed)) return;
    if (ended && direct_video_generation == generation) clearcast_direct_video_clear();
}

extern "C" __declspec(dllexport) int clearcast_receiver_media_control(uint64_t session, uint64_t content, int command, int64_t value) try {
    if (!session || command < 1 || command > 4 || value < 0 || value > 7LL * 86400000) return ERROR_INVALID_PARAMETER;
    if (!(session & (uint64_t(1) << 63))) {
        if (clearcast_direct_video_is_active()) return ERROR_CANCELLED;
        if (command == 4) return clearcast_media_seek_audio(session, content, value);
        {
            clearcast_media::Lock lock(clearcast_media::state_lock);
            if (clearcast_media::state.owner != session || clearcast_media::state.content_revision != content) return ERROR_CANCELLED;
        }
        return clearcast_receiver_media_command(command);
    }
    std::lock_guard<std::mutex> lifecycle(direct_video_pipeline_lock);
    GstElement* pipeline; bool playing;
    {
        std::lock_guard<std::mutex> lock(direct_video_lock);
        if (!direct_video_active || session != direct_video_session || content != session) return ERROR_CANCELLED;
        if (!direct_video_pipeline || direct_video_decoder_pending >= 0) return ERROR_BUSY;
        if (command != 1 && command != 4) return ERROR_NOT_SUPPORTED;
        if (command == 4 && (!direct_video_seekable || value < direct_video_seek_start || value > direct_video_seek_end)) return ERROR_NOT_SUPPORTED;
        pipeline = GST_ELEMENT(gst_object_ref(direct_video_pipeline)); playing = direct_video_playing;
    }
    bool success = command == 1 ? gst_element_set_state(pipeline, playing ? GST_STATE_PAUSED : GST_STATE_PLAYING) != GST_STATE_CHANGE_FAILURE :
        gst_element_seek_simple(pipeline, GST_FORMAT_TIME, static_cast<GstSeekFlags>(GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_KEY_UNIT), value * GST_MSECOND);
    gst_object_unref(pipeline);
    if (!success) return ERROR_NOT_SUPPORTED;
    std::lock_guard<std::mutex> lock(direct_video_lock);
    if (direct_video_session != session) return ERROR_CANCELLED;
    if (command == 1) direct_video_playing = !playing;
    return 0;
} catch (...) { return ERROR_OUTOFMEMORY; }
