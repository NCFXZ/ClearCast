/*
 * Reuse GStreamer's D3D11 device contexts across receiver sessions.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#pragma once

#include <gst/gst.h>

#define CLEARCAST_D3D11_CONTEXT "gst.d3d11.device.handle"

typedef struct {
    gint64 luid;
    guint adapter;
    GstContext *context;
} ClearCastVideoContext;

G_LOCK_DEFINE_STATIC(clearcast_video_context);
static GList *clearcast_video_contexts;

static void clearcast_video_context_store(GstContext *context) {
    const GstStructure *data = gst_context_get_structure(context);
    gint64 luid;
    guint adapter;
    if (!gst_structure_get_int64(data, "adapter-luid", &luid) ||
        !gst_structure_get_uint(data, "adapter", &adapter)) return;

    GstContext *previous = NULL;
    G_LOCK(clearcast_video_context);
    ClearCastVideoContext *entry = NULL;
    for (GList *item = clearcast_video_contexts; item; item = item->next) {
        ClearCastVideoContext *candidate = item->data;
        if (candidate->luid == luid) entry = candidate;
    }
    if (!entry) {
        entry = g_new0(ClearCastVideoContext, 1);
        entry->luid = luid;
        entry->adapter = adapter;
        clearcast_video_contexts = g_list_prepend(clearcast_video_contexts, entry);
    }
    previous = entry->context;
    entry->context = gst_context_ref(context);
    G_UNLOCK(clearcast_video_context);
    if (previous) gst_context_unref(previous);
}

static GstContext *clearcast_video_context_ref(gboolean has_luid, gint64 luid, gint adapter) {
    GstContext *context = NULL;
    G_LOCK(clearcast_video_context);
    for (GList *item = clearcast_video_contexts; item; item = item->next) {
        ClearCastVideoContext *entry = item->data;
        if (entry->context && (has_luid ? entry->luid == luid :
                             entry->adapter == (guint)MAX(adapter, 0))) {
            context = gst_context_ref(entry->context);
            break;
        }
    }
    G_UNLOCK(clearcast_video_context);
    if (!context) return NULL;

    const GValue *value = gst_structure_get_value(gst_context_get_structure(context), "device");
    GObject *device = value && G_VALUE_HOLDS_OBJECT(value) ? g_value_get_object(value) : NULL;
    gint removed = 0;
    if (device) g_object_get(device, "device-removed-reason", &removed, NULL);
    if (device && !removed) return context;

    // Device removal requires a new device; ordinary reconnects retain the
    // healthy one. Never unref a device while holding the cache mutex.
    GstContext *previous = NULL;
    G_LOCK(clearcast_video_context);
    for (GList *item = clearcast_video_contexts; item; item = item->next) {
        ClearCastVideoContext *entry = item->data;
        if (entry->context == context) {
            previous = entry->context;
            entry->context = NULL;
            break;
        }
    }
    G_UNLOCK(clearcast_video_context);
    if (previous) gst_context_unref(previous);
    gst_context_unref(context);
    return NULL;
}

static void clearcast_video_context_apply(GstElement *element, gboolean require_property) {
    GObjectClass *klass = G_OBJECT_GET_CLASS(element);
    const gboolean has_luid = g_object_class_find_property(klass, "adapter-luid") != NULL;
    const gboolean has_adapter = g_object_class_find_property(klass, "adapter") != NULL;
    if (require_property && !has_luid && !has_adapter) return;
    gint64 luid = 0;
    gint adapter = -1;
    if (has_luid) g_object_get(element, "adapter-luid", &luid, NULL);
    else if (has_adapter) g_object_get(element, "adapter", &adapter, NULL);
    GstContext *context = clearcast_video_context_ref(has_luid, luid, adapter);
    if (context) {
        gst_element_set_context(element, context);
        gst_context_unref(context);
    }
}

static void clearcast_video_context_added(GstBin *pipeline, GstBin *parent,
                                         GstElement *element, gpointer unused) {
    (void)pipeline;
    (void)parent;
    (void)unused;
    // Decodebin may add a decoder for another GPU after the initial pipeline
    // exists. Supply that GPU's context before GstBin's default can mask NEED.
    clearcast_video_context_apply(element, TRUE);
}

static void clearcast_video_context_watch(GstElement *pipeline) {
    g_signal_connect(pipeline, "deep-element-added",
                     G_CALLBACK(clearcast_video_context_added), NULL);
}

static void clearcast_video_context_message(GstMessage *message) {
    if (GST_MESSAGE_TYPE(message) == GST_MESSAGE_HAVE_CONTEXT) {
        GstContext *context = NULL;
        gst_message_parse_have_context(message, &context);
        if (context) {
            if (gst_context_has_context_type(context, CLEARCAST_D3D11_CONTEXT)) {
                // A bounded, process-lifetime context per GPU avoids repeated
                // D3D11 device creation and the driver handles it retains.
                clearcast_video_context_store(context);
            }
            gst_context_unref(context);
        }
    } else if (GST_MESSAGE_TYPE(message) == GST_MESSAGE_NEED_CONTEXT) {
        const gchar *type;
        if (!gst_message_parse_context_type(message, &type) ||
            g_strcmp0(type, CLEARCAST_D3D11_CONTEXT)) return;
        clearcast_video_context_apply(GST_ELEMENT(GST_MESSAGE_SRC(message)), FALSE);
    }
}
