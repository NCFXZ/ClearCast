/* Only the control messages consumed by the embedded receiver need queuing.
 * SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef CLEARCAST_BUS_BACKPRESSURE_H
#define CLEARCAST_BUS_BACKPRESSURE_H
#include <gst/gst.h>
#include <gst/base/gstbasesink.h>

static void clearcast_bus_prepare(GstBus *bus) {
    g_object_set_data_full(G_OBJECT(bus), "clearcast-control-messages", g_new0(gint, 1), g_free);
}

static void clearcast_bus_reset(GstBus *bus) {
    gint *pending = (gint *)g_object_get_data(G_OBJECT(bus), "clearcast-control-messages");
    if (pending) g_atomic_int_set(pending, 0);
}

static gint clearcast_bus_message_bit(GstMessage *message) {
    switch (GST_MESSAGE_TYPE(message)) {
    case GST_MESSAGE_ERROR: return 1;
    case GST_MESSAGE_EOS: return 2;
    case GST_MESSAGE_ASYNC_DONE: return 4;
    case GST_MESSAGE_STATE_CHANGED: return 8;
    default: return 0;
    }
}

// Mode 1 retains direct-video seek completion. Mode 2 retains the mirror
// renderer's sink-state notification, which binds its decoder/stats sink.
static GstBusSyncReply clearcast_bus_sync(GstBus *bus, GstMessage *message, gpointer mode) {
    gint *pending = (gint *)g_object_get_data(G_OBJECT(bus), "clearcast-control-messages");
    const gint bit = clearcast_bus_message_bit(message);
    const gint flags = GPOINTER_TO_INT(mode);
    const gboolean consumed = bit && (bit != 4 || (flags & 1)) &&
        (bit != 8 || ((flags & 2) && GST_IS_BASE_SINK(GST_MESSAGE_SRC(message))));
    if (pending && consumed && !(g_atomic_int_or(pending, bit) & bit)) return GST_BUS_PASS;
    gst_message_unref(message);
    return GST_BUS_DROP;
}

static void clearcast_bus_popped(GstBus *bus, GstMessage *message) {
    gint *pending = (gint *)g_object_get_data(G_OBJECT(bus), "clearcast-control-messages");
    if (pending) g_atomic_int_and(pending, ~clearcast_bus_message_bit(message));
}
#endif
