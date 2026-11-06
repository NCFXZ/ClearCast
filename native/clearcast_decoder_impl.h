/*
 * Decoder selection for ClearCast's in-process GStreamer renderer.
 * Included by video_renderer.c after its renderer state declarations.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#pragma once

#include <gst/base/gstbasesink.h>
#include <windows.h>

#define CLEARCAST_DECODER_LOCK() \
    g_autoptr(GRecMutexLocker) decoder_lock = g_rec_mutex_locker_new(&clearcast_renderer_lock)
#define CLEARCAST_DECODER_TIMEOUT (5 * G_TIME_SPAN_SECOND)
#define CLEARCAST_MAX_PARAMETER_SET (128 * 1024)

extern void clearcast_decoder_result(int decoder, int error);

static GRecMutex clearcast_renderer_lock;
static gint clearcast_decoder_requested = -1;
static int clearcast_decoder_current;
static int clearcast_decoder_switching = -1;
static gint64 clearcast_decoder_deadline;
static bool clearcast_video_rebased;
static GBytes *clearcast_decoder_parameters[3];
static GstBuffer *clearcast_decoder_keyframe;
static GstClockTime clearcast_decoder_keyframe_time;

static void clearcast_decoder_forget_keyframe(void) {
    if (clearcast_decoder_keyframe) {
        gst_buffer_unref(clearcast_decoder_keyframe);
        clearcast_decoder_keyframe = NULL;
    }
}

static void clearcast_decoder_clear_stream(void) {
    for (int i = 0; i < 3; ++i) {
        g_clear_pointer(&clearcast_decoder_parameters[i], g_bytes_unref);
    }
    clearcast_decoder_forget_keyframe();
    clearcast_decoder_switching = -1;
    clearcast_decoder_deadline = 0;
    clearcast_video_rebased = false;
}

void clearcast_video_initialize_decoder(int decoder) {
    CLEARCAST_DECODER_LOCK();
    clearcast_decoder_clear_stream();
    clearcast_decoder_current = decoder;
    g_atomic_int_set(&clearcast_decoder_requested, -1);
}

bool clearcast_video_request_decoder(int decoder) {
    return g_atomic_int_compare_and_exchange(&clearcast_decoder_requested, -1, decoder);
}

static void clearcast_decoder_link_output(GstElement *decodebin, GstPad *src, gpointer output) {
    (void)decodebin;
    GstPad *sink = gst_element_get_static_pad(GST_ELEMENT(output), "sink");
    if (sink) {
        if (!gst_pad_is_linked(src) && !gst_pad_is_linked(sink)) {
            gst_pad_link(src, sink);
        }
        gst_object_unref(sink);
    }
}

static void clearcast_decoder_release_output(gpointer output, GClosure *closure) {
    (void)closure;
    gst_object_unref(output);
}

static bool clearcast_decoder_factory_is_hardware(GstElementFactory *factory) {
    return gst_element_factory_list_is_type(factory, GST_ELEMENT_FACTORY_TYPE_HARDWARE);
}

bool clearcast_video_has_hardware_decoder(bool hevc) {
    g_autoptr(GstCaps) caps = gst_caps_new_empty_simple(hevc ? "video/x-h265" : "video/x-h264");
    GList *factories = gst_element_factory_list_get_elements(
        GST_ELEMENT_FACTORY_TYPE_DECODER | GST_ELEMENT_FACTORY_TYPE_MEDIA_VIDEO, GST_RANK_MARGINAL);
    bool found = false;
    for (GList *entry = factories; entry; entry = entry->next) {
        GstElementFactory *factory = GST_ELEMENT_FACTORY(entry->data);
        if (clearcast_decoder_factory_is_hardware(factory) &&
            gst_element_factory_can_sink_any_caps(factory, caps)) {
            found = true;
            break;
        }
    }
    gst_plugin_feature_list_free(factories);
    return found;
}

static bool clearcast_decoder_has_hardware_for(video_renderer_t *instance) {
    if (!instance || !instance->appsrc ||
        (strcmp(instance->codec, h264) && strcmp(instance->codec, h265))) {
        return true;
    }
    return clearcast_video_has_hardware_decoder(!strcmp(instance->codec, h265));
}

static gint clearcast_decoder_select_factory(GstElement *decodebin, GstPad *pad, GstCaps *caps,
                                            GstElementFactory *factory, gpointer unused) {
    (void)pad;
    (void)caps;
    (void)unused;
    // GstDecodeBin's autoplug-select signal uses 0 (TRY) and 2 (SKIP).
    int decoder = GPOINTER_TO_INT(g_object_get_data(G_OBJECT(decodebin), "clearcast-decode-mode"));
    if (decoder == CLEARCAST_DECODER_AUTOMATIC ||
        !gst_element_factory_list_is_type(factory,
            GST_ELEMENT_FACTORY_TYPE_DECODER | GST_ELEMENT_FACTORY_TYPE_MEDIA_VIDEO)) {
        return 0;
    }
    if (decoder == CLEARCAST_DECODER_HARDWARE) {
        return clearcast_decoder_factory_is_hardware(factory) ? 0 : 2;
    }
    const char *name = GST_OBJECT_NAME(factory);
    return !strcmp(name, "avdec_h264") || !strcmp(name, "avdec_h265") ? 0 : 2;
}

static bool clearcast_decoder_link_pipeline(video_renderer_t *instance) {
    if (strcmp(instance->codec, h264) && strcmp(instance->codec, h265)) {
        return true;
    }
    GstElement *decodebin = gst_bin_get_by_name(GST_BIN(instance->pipeline), "clearcast_decoder");
    GstElement *output = gst_bin_get_by_name(GST_BIN(instance->pipeline), "clearcast_decoded");
    bool ready = decodebin && output;
    if (ready) {
        // gst_parse_launch's delayed link runs once. Keep reconnecting new decodebin
        // pads after resetting the pipeline to READY.
        g_signal_connect_data(decodebin, "pad-added", G_CALLBACK(clearcast_decoder_link_output),
                              output, clearcast_decoder_release_output, 0);
        g_signal_connect(decodebin, "autoplug-select", G_CALLBACK(clearcast_decoder_select_factory), NULL);
    } else if (output) {
        gst_object_unref(output);
    }
    if (decodebin) {
        gst_object_unref(decodebin);
    }
    return ready;
}

static bool clearcast_decoder_configure(video_renderer_t *instance, int decoder) {
    if (!instance || !instance->appsrc ||
        (strcmp(instance->codec, h264) && strcmp(instance->codec, h265))) {
        return true;
    }
    GstElement *decodebin = gst_bin_get_by_name(GST_BIN(instance->pipeline), "clearcast_decoder");
    if (!decodebin) {
        return false;
    }
    // Decodebin keeps a cached factory list across READY transitions. Filter each
    // new autoplug attempt instead of changing force-sw-decoders on an existing bin.
    g_object_set_data(G_OBJECT(decodebin), "clearcast-decode-mode", GINT_TO_POINTER(decoder));
    gst_object_unref(decodebin);
    return true;
}

static void clearcast_decoder_complete(int decoder, int error) {
    clearcast_decoder_switching = -1;
    clearcast_decoder_deadline = 0;
    clearcast_decoder_forget_keyframe();
    if (!error) {
        clearcast_decoder_current = decoder;
    }
    g_atomic_int_set(&clearcast_decoder_requested, -1);
    clearcast_decoder_result(decoder, error);
}

static bool clearcast_decoder_reset_pipeline(int decoder) {
    GstElement *pipeline = renderer->pipeline;
    gst_bus_set_flushing(renderer->bus, TRUE);
    bool ready = gst_element_set_state(pipeline, GST_STATE_READY) != GST_STATE_CHANGE_FAILURE &&
                 gst_element_get_state(pipeline, NULL, NULL, 250 * GST_MSECOND) == GST_STATE_CHANGE_SUCCESS;
    bool configured = ready && clearcast_decoder_configure(renderer, decoder);
    gst_bus_set_flushing(renderer->bus, FALSE);
    if (!configured || gst_element_set_state(pipeline, GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE) {
        return false;
    }
    gst_video_pipeline_base_time = gst_element_get_base_time(renderer->appsrc);
    clearcast_video_rebased = true;
    return true;
}

static void clearcast_decoder_revert(int error) {
    const int failed = clearcast_decoder_switching;
    bool restored = clearcast_decoder_reset_pipeline(clearcast_decoder_current);
    if (restored && clearcast_decoder_keyframe) {
        GstBuffer *keyframe = gst_buffer_copy(clearcast_decoder_keyframe);
        if (keyframe) {
            if (sync) {
                GstClockTime base = gst_video_pipeline_base_time;
                GST_BUFFER_PTS(keyframe) = clearcast_decoder_keyframe_time >= base
                    ? clearcast_decoder_keyframe_time - base : 0;
            }
            gst_app_src_push_buffer(GST_APP_SRC(renderer->appsrc), keyframe);
        }
    }
    clearcast_decoder_complete(failed, error);
    if (!restored) {
        clearcast_pipeline_error();
    }
}

static bool clearcast_decoder_rendered_frame(void) {
    if (!renderer->videosink || !GST_IS_BASE_SINK(renderer->videosink)) {
        return false;
    }
    GstStructure *stats = gst_base_sink_get_stats(GST_BASE_SINK(renderer->videosink));
    guint64 rendered_frames = 0;
    if (stats) {
        gst_structure_get_uint64(stats, "rendered", &rendered_frames);
        gst_structure_free(stats);
    }
    return rendered_frames > 0;
}

void clearcast_video_poll_decoder(void) {
    if (g_atomic_int_get(&clearcast_decoder_requested) < 0 ||
        !g_rec_mutex_trylock(&clearcast_renderer_lock)) {
        return;
    }
    int requested = g_atomic_int_get(&clearcast_decoder_requested);
    if (requested < 0) {
        g_rec_mutex_unlock(&clearcast_renderer_lock);
        return;
    }
    gint64 now = g_get_monotonic_time();
    if (clearcast_decoder_switching >= 0) {
        if (clearcast_decoder_rendered_frame()) {
            clearcast_decoder_complete(requested, 0);
        } else if (now >= clearcast_decoder_deadline) {
            clearcast_decoder_revert(ERROR_TIMEOUT);
        }
    } else if (!renderer || requested == clearcast_decoder_current) {
        bool configured = true;
        if (requested != clearcast_decoder_current) {
            configured = requested != CLEARCAST_DECODER_HARDWARE ||
                         clearcast_video_has_hardware_decoder(false);
            for (int i = 0; i < n_renderers && configured; ++i) {
                configured = clearcast_decoder_configure(renderer_type[i], requested);
            }
        }
        clearcast_decoder_complete(requested, configured ? 0 : ERROR_NOT_SUPPORTED);
    } else if (!clearcast_decoder_deadline) {
        clearcast_decoder_deadline = now + CLEARCAST_DECODER_TIMEOUT;
    } else if (now >= clearcast_decoder_deadline) {
        clearcast_decoder_complete(requested, ERROR_TIMEOUT);
    }
    g_rec_mutex_unlock(&clearcast_renderer_lock);
}

// UxPlay's RTP receiver converts NAL lengths to four-byte Annex B start codes.
static const guint8 *clearcast_nal_start(const guint8 *data, const guint8 *end) {
    while (end - data >= 4) {
        const guint8 *zero = memchr(data, 0, (size_t)(end - data - 3));
        if (!zero) {
            break;
        }
        if (zero[1] == 0 && zero[2] == 0 && zero[3] == 1) {
            return zero;
        }
        data = zero + 1;
    }
    return end;
}

static GstBuffer *clearcast_decoder_inspect_packet(const guint8 *data, int length, GstClockTime time) {
    if (!renderer || length < 5 || data[0] ||
        (strcmp(renderer->codec, h264) && strcmp(renderer->codec, h265))) {
        return NULL;
    }
    const bool hevc = !strcmp(renderer->codec, h265);
    const int parameter_count = hevc ? 3 : 2;
    const guint8 *end = data + length;
    bool random_access = false;
    unsigned int included = 0;
    for (const guint8 *nal = clearcast_nal_start(data, end); end - nal >= 5;) {
        const guint8 *next = clearcast_nal_start(nal + 4, end);
        int type = hevc ? (nal[4] >> 1) & 0x3f : nal[4] & 0x1f;
        int parameter = hevc ? type - 32 : type - 7;
        if (parameter >= 0 && parameter < parameter_count && next - nal <= CLEARCAST_MAX_PARAMETER_SET) {
            g_clear_pointer(&clearcast_decoder_parameters[parameter], g_bytes_unref);
            clearcast_decoder_parameters[parameter] = g_bytes_new(nal, (gsize)(next - nal));
            included |= 1u << parameter;
        }
        if (hevc ? type >= 16 && type <= 21 : type == 5) {
            random_access = true;
        }
        nal = next;
    }

    const int requested = g_atomic_int_get(&clearcast_decoder_requested);
    if (!random_access || requested < 0 || requested == clearcast_decoder_current ||
        clearcast_decoder_switching >= 0) {
        return NULL;
    }
    if (requested == CLEARCAST_DECODER_HARDWARE && !clearcast_decoder_has_hardware_for(renderer)) {
        clearcast_decoder_complete(requested, ERROR_NOT_SUPPORTED);
        return NULL;
    }

    gsize headers_size = 0;
    bool need_headers = included != (1u << parameter_count) - 1;
    for (int i = 0; i < parameter_count; ++i) {
        if (!clearcast_decoder_parameters[i]) {
            return NULL;
        }
        if (need_headers) {
            headers_size += g_bytes_get_size(clearcast_decoder_parameters[i]);
        }
    }
    GstBuffer *keyframe = gst_buffer_new_allocate(NULL, headers_size + (gsize) length, NULL);
    if (!keyframe) {
        return NULL;
    }
    gsize offset = 0;
    for (int i = 0; i < parameter_count; ++i) {
        if (need_headers) {
            gsize size;
            const void *bytes = g_bytes_get_data(clearcast_decoder_parameters[i], &size);
            gst_buffer_fill(keyframe, offset, bytes, size);
            offset += size;
        }
    }
    gst_buffer_fill(keyframe, offset, data, (gsize) length);
    GST_BUFFER_FLAG_SET(keyframe, GST_BUFFER_FLAG_DISCONT);
    clearcast_decoder_switching = requested;
    clearcast_decoder_deadline = g_get_monotonic_time() + CLEARCAST_DECODER_TIMEOUT;
    clearcast_decoder_keyframe = gst_buffer_copy(keyframe);
    clearcast_decoder_keyframe_time = time;
    if (!clearcast_decoder_reset_pipeline(requested)) {
        gst_buffer_unref(keyframe);
        clearcast_decoder_revert(ERROR_NOT_SUPPORTED);
        return NULL;
    }
    return keyframe;
}
