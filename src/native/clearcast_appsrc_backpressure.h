/* Finite application-source admission without leaking encoded dependencies.
 * SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef CLEARCAST_APPSRC_BACKPRESSURE_H
#define CLEARCAST_APPSRC_BACKPRESSURE_H
#include <gst/app/gstappsrc.h>

#ifdef __cplusplus
extern "C" {
#endif
gboolean clearcast_media_input_cancelled(void);
#ifdef __cplusplus
}
#endif

typedef struct {
    GMutex lock;
    guint64 bytes, buffers, time;
    gboolean failed;
    gint cancelled;
} ClearCastAppSource;

static void clearcast_appsrc_free(gpointer value) {
    ClearCastAppSource *state = (ClearCastAppSource *)value;
    g_mutex_clear(&state->lock);
    g_free(state);
}

static void clearcast_appsrc_configure(GstElement *source, gboolean video) {
    ClearCastAppSource *state = g_new0(ClearCastAppSource, 1);
    g_mutex_init(&state->lock);
    // The existing mirror parser permits a 64 MiB access unit. Keep room for
    // its three bounded parameter sets; frame/time limits handle normal media.
    state->bytes = (guint64)(video ? 68 : 4) * 1024 * 1024;
    state->buffers = video ? 120 : 256;
    state->time = 2 * GST_SECOND;
    g_object_set_data_full(G_OBJECT(source), "clearcast-appsrc-budget", state, clearcast_appsrc_free);
    g_object_set(source, "max-bytes", state->bytes, "max-buffers", state->buffers,
        "max-time", state->time, "block", FALSE, "leaky-type", GST_APP_LEAKY_TYPE_NONE,
        "emit-signals", FALSE, NULL);
}

static void clearcast_appsrc_reset(GstElement *source) {
    ClearCastAppSource *state = (ClearCastAppSource *)g_object_get_data(G_OBJECT(source), "clearcast-appsrc-budget");
    if (!state) return;
    g_mutex_lock(&state->lock);
    state->failed = FALSE;
    g_atomic_int_set(&state->cancelled, 0);
    g_mutex_unlock(&state->lock);
}

static GstFlowReturn clearcast_appsrc_end_of_stream(GstAppSrc *source) {
    ClearCastAppSource *state = (ClearCastAppSource *)g_object_get_data(G_OBJECT(source), "clearcast-appsrc-budget");
    if (state) g_atomic_int_set(&state->cancelled, 1);
    return gst_app_src_end_of_stream(source);
}

// Takes ownership of buffer just like gst_app_src_push_buffer. Callers retain
// their source reference for the whole call. The source's admission lock also
// serializes concurrent producers, so querying a level and pushing is atomic
// with respect to other application pushes. GStreamer only drains the queue.
static GstFlowReturn clearcast_appsrc_push_buffer(GstElement *source, GstBuffer *buffer) {
    ClearCastAppSource *state = (ClearCastAppSource *)g_object_get_data(G_OBJECT(source), "clearcast-appsrc-budget");
    if (!state || !buffer) {
        if (buffer) gst_buffer_unref(buffer);
        return GST_FLOW_ERROR;
    }
    const guint64 size = gst_buffer_get_size(buffer);
    const gint64 deadline = g_get_monotonic_time() + 5 * G_TIME_SPAN_SECOND;
    GstFlowReturn result = GST_FLOW_ERROR;
    g_mutex_lock(&state->lock);
    if (state->failed) goto done;
    for (;;) {
        GstAppSrc *input = GST_APP_SRC(source);
        if (clearcast_media_input_cancelled() || g_atomic_int_get(&state->cancelled)) { result = GST_FLOW_FLUSHING; break; }
        const guint64 bytes = gst_app_src_get_current_level_bytes(input);
        const guint64 count = gst_app_src_get_current_level_buffers(input);
        const guint64 time = gst_app_src_get_current_level_time(input);
        if (size <= state->bytes && bytes <= state->bytes - size && count < state->buffers && time < state->time) {
            result = gst_app_src_push_buffer(input, buffer);
            buffer = NULL;
            break;
        }
        GstState current, pending;
        gst_element_get_state(source, &current, &pending, 0);
        if ((current <= GST_STATE_READY && pending == GST_STATE_VOID_PENDING) ||
            pending == GST_STATE_NULL || pending == GST_STATE_READY) {
            result = GST_FLOW_FLUSHING;
            break;
        }
        if (size > state->bytes || g_get_monotonic_time() >= deadline) {
            // End a persistently overloaded stream rather than drop an
            // arbitrary H.264/H.265 dependency and continue corrupt playback.
            // One error per source/reset goes through the existing retry path.
            state->failed = TRUE;
            GST_ELEMENT_ERROR(source, RESOURCE, NO_SPACE_LEFT,
                ("Media input remained above its bounded queue budget."), (NULL));
            break;
        }
        // block=TRUE could hold the renderer lock indefinitely. This wait is
        // finite and stop/reset cancellation remains observable while full.
        g_usleep(2000);
    }
done:
    g_mutex_unlock(&state->lock);
    if (buffer) gst_buffer_unref(buffer);
    return result;
}
#endif
