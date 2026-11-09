/* SPDX-License-Identifier: GPL-3.0-or-later */
#include "clearcast_video_context.h"
#include <stdio.h>
#include <stdlib.h>

static unsigned checks;
#define CHECK(test) do { ++checks; if (!(test)) { \
    fprintf(stderr, "check %u failed: %s (%d)\n", checks, #test, __LINE__); exit(1); \
} } while (0)

typedef struct { GObject parent; gint removed; } TestDevice;
typedef struct { GObjectClass parent; } TestDeviceClass;
G_DEFINE_TYPE(TestDevice, test_device, G_TYPE_OBJECT)
static void device_get(GObject *object, guint id, GValue *value, GParamSpec *spec) {
    (void)spec;
    if (id == 1) g_value_set_int(value, ((TestDevice *)object)->removed);
}
static void test_device_class_init(TestDeviceClass *klass) {
    GObjectClass *object = G_OBJECT_CLASS(klass);
    object->get_property = device_get;
    g_object_class_install_property(object, 1, g_param_spec_int("device-removed-reason",
        "reason", "reason", G_MININT, G_MAXINT, 0, G_PARAM_READABLE));
}
static void test_device_init(TestDevice *device) { device->removed = 0; }
static void finalized(gpointer count, GObject *object) { (void)object; ++*(unsigned *)count; }

typedef struct { GstElement parent; gint64 luid; } TestElement;
typedef struct { GstElementClass parent; } TestElementClass;
G_DEFINE_TYPE(TestElement, test_element, GST_TYPE_ELEMENT)
static void element_get(GObject *object, guint id, GValue *value, GParamSpec *spec) {
    (void)spec;
    if (id == 1) g_value_set_int64(value, ((TestElement *)object)->luid);
}
static void test_element_class_init(TestElementClass *klass) {
    GObjectClass *object = G_OBJECT_CLASS(klass);
    object->get_property = element_get;
    g_object_class_install_property(object, 1, g_param_spec_int64("adapter-luid",
        "luid", "luid", G_MININT64, G_MAXINT64, 0, G_PARAM_READABLE));
}
static void test_element_init(TestElement *element) { element->luid = 222; }

static GstContext *context_new(guint adapter, gint64 luid, unsigned *destroyed, TestDevice **device) {
    *device = g_object_new(test_device_get_type(), NULL);
    g_object_weak_ref(G_OBJECT(*device), finalized, destroyed);
    GstContext *context = gst_context_new(CLEARCAST_D3D11_CONTEXT, TRUE);
    gst_structure_set(gst_context_writable_structure(context), "adapter", G_TYPE_UINT, adapter,
        "adapter-luid", G_TYPE_INT64, luid, "device", G_TYPE_OBJECT, *device, NULL);
    g_object_unref(*device);
    return context;
}

static GstBusSyncReply context_message(GstBus *bus, GstMessage *message, gpointer unused) {
    (void)bus; (void)unused;
    clearcast_video_context_message(message);
    return GST_BUS_PASS;
}

int main(int argc, char **argv) {
    gst_init(&argc, &argv);
    unsigned destroyed = 0;
    TestDevice *first, *second, *replacement;
    GstContext *a = context_new(0, 111, &destroyed, &first);
    GstContext *b = context_new(1, 222, &destroyed, &second);
    clearcast_video_context_store(a);
    clearcast_video_context_store(a);
    clearcast_video_context_store(b);
    gst_context_unref(a);
    gst_context_unref(b);
    CHECK(destroyed == 0);
    CHECK(g_list_length(clearcast_video_contexts) == 2);
    CHECK(clearcast_video_context_ref(TRUE, 999, 0) == NULL);
    CHECK(clearcast_video_context_ref(FALSE, 0, 3) == NULL);
    GstContext *value = clearcast_video_context_ref(FALSE, 0, -1);
    CHECK(value == a); gst_context_unref(value);
    value = clearcast_video_context_ref(TRUE, 222, 0);
    CHECK(value == b); gst_context_unref(value);
    value = clearcast_video_context_ref(FALSE, 0, 1);
    CHECK(value == b); gst_context_unref(value);

    // Real bus messages use transfer-full context parsing. The cache must own
    // exactly one reference and provide it synchronously to a new element.
    GstElement *pipeline = gst_pipeline_new(NULL);
    GstElement *element = gst_bin_new(NULL);
    CHECK(gst_bin_add(GST_BIN(pipeline), element));
    clearcast_video_context_watch(pipeline);
    GstBus *bus = gst_element_get_bus(pipeline);
    gst_bus_set_sync_handler(bus, context_message, NULL, NULL);
    CHECK(gst_bus_post(bus, gst_message_new_have_context(GST_OBJECT(element), gst_context_ref(b))));
    CHECK(gst_bus_post(bus, gst_message_new_have_context(GST_OBJECT(element),
        gst_context_new("unrelated.context", TRUE))));
    CHECK(g_list_length(clearcast_video_contexts) == 2);
    CHECK(gst_element_post_message(element,
        gst_message_new_need_context(GST_OBJECT(element), CLEARCAST_D3D11_CONTEXT)));
    value = gst_element_get_context(element, CLEARCAST_D3D11_CONTEXT);
    CHECK(value == a); gst_context_unref(value);
    GstElement *other_gpu = g_object_new(test_element_get_type(), NULL);
    CHECK(gst_bin_add(GST_BIN(element), other_gpu));
    value = gst_element_get_context(other_gpu, CLEARCAST_D3D11_CONTEXT);
    CHECK(value == b); gst_context_unref(value);
    gst_object_unref(bus);
    gst_object_unref(pipeline);

    first->removed = -1;
    CHECK(clearcast_video_context_ref(FALSE, 0, 0) == NULL);
    CHECK(destroyed == 1);
    CHECK(clearcast_video_context_ref(TRUE, 111, 0) == NULL);
    a = context_new(0, 111, &destroyed, &replacement);
    clearcast_video_context_store(a); gst_context_unref(a);
    CHECK(g_list_length(clearcast_video_contexts) == 2);
    value = clearcast_video_context_ref(TRUE, 111, 0);
    CHECK(value == a); gst_context_unref(value);
    CHECK(destroyed == 1);
    b = context_new(1, 222, &destroyed, &second);
    clearcast_video_context_store(b); gst_context_unref(b);
    CHECK(destroyed == 2);
    for (GList *item = clearcast_video_contexts; item; item = item->next) {
        ClearCastVideoContext *entry = item->data;
        if (entry->context) gst_context_unref(entry->context);
        g_free(entry);
    }
    g_list_free(clearcast_video_contexts);
    clearcast_video_contexts = NULL;
    CHECK(destroyed == 4);
    printf("PASS: %u video context ownership, GPU selection and removal checks\n", checks);
    return 0;
}
