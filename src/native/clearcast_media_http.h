/* HTTP sources shared by direct playback and HLS's private downloaders.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#pragma once

#include <gst/gst.h>
#include <gst/gsturi.h>
#include <gio/gio.h>
#include <string>
#include "clearcast_media_connectable.h"

static std::string clearcast_http_ca_file;
static GTlsDatabase* clearcast_http_database;
static GstStateChangeReturn (*clearcast_http_parent_state)(GstElement*, GstStateChange);
static gboolean (*clearcast_http_parent_uri)(GstURIHandler*, const gchar*, GError**);

static bool clearcast_http_uri_allowed(const char* value) {
    if (!value || !*value || strlen(value) > 4096) return false;
    for (const unsigned char* p = reinterpret_cast<const unsigned char*>(value); *p; ++p) {
        if (*p <= 32 || *p == 127) return false;
    }
    GError* error = nullptr;
    GUri* uri = g_uri_parse(value, G_URI_FLAGS_NONE, &error);
    g_clear_error(&error);
    if (!uri) return false;
    const char* scheme = g_uri_get_scheme(uri);
    const char* host = g_uri_get_host(uri);
    bool allowed = scheme && (!g_ascii_strcasecmp(scheme, "http") || !g_ascii_strcasecmp(scheme, "https")) &&
        host && *host && !g_uri_get_userinfo(uri) && !g_uri_get_fragment(uri) && g_uri_get_port(uri) != 0;
    if (allowed) {
        GInetAddress* literal = g_inet_address_new_from_string(host);
        if (literal) {
            allowed = clearcast_http_address_allowed(literal);
            g_object_unref(literal);
        }
        // Other spellings (including short/hex IPv4 and DNS aliases) are
        // checked by the actual socket-address enumerator before connecting.
    }
    g_uri_unref(uri);
    return allowed;
}

static gboolean clearcast_http_set_uri(GstURIHandler* handler, const gchar* uri, GError** error) {
    if (!clearcast_http_uri_allowed(uri)) {
        g_set_error_literal(error, GST_URI_ERROR, GST_URI_ERROR_BAD_URI, "Only credential-free HTTP(S) media URLs are supported.");
        return FALSE;
    }
    return clearcast_http_parent_uri(handler, uri, error);
}

static const gchar* const* clearcast_http_protocols(GType) {
    static const gchar* protocols[] = {"http", "https", nullptr};
    return protocols;
}

static void clearcast_http_interface_init(gpointer value, gpointer) {
    auto* handler_interface = static_cast<GstURIHandlerInterface*>(value);
    clearcast_http_parent_uri = handler_interface->set_uri;
    handler_interface->set_uri = clearcast_http_set_uri;
    handler_interface->get_protocols = clearcast_http_protocols;
}

static GstStateChangeReturn clearcast_http_change_state(GstElement* source, GstStateChange transition) {
    if (transition == GST_STATE_CHANGE_NULL_TO_READY || transition == GST_STATE_CHANGE_READY_TO_PAUSED) {
        gchar* uri = gst_uri_handler_get_uri(GST_URI_HANDLER(source));
        bool allowed = clearcast_http_uri_allowed(uri);
        GObjectClass* klass = G_OBJECT_GET_CLASS(source);
        if (!allowed || !g_object_class_find_property(klass, "automatic-redirect") ||
            !g_object_class_find_property(klass, "ssl-strict") ||
            !g_object_class_find_property(klass, "remote-connectable")) {
            g_free(uri);
            GST_ELEMENT_ERROR(source, RESOURCE, NOT_AUTHORIZED, ("Media URL rejected by the receiver HTTP policy."), (nullptr));
            return GST_STATE_CHANGE_FAILURE;
        }
        GUri* parsed = g_uri_parse(uri, G_URI_FLAGS_NONE, nullptr);
        const char* host = g_uri_get_host(parsed);
        int port = g_uri_get_port(parsed);
        if (port < 0) port = !g_ascii_strcasecmp(g_uri_get_scheme(parsed), "https") ? 443 : 80;
        GSocketConnectable* previous = nullptr;
        g_object_get(source, "remote-connectable", &previous, nullptr);
        bool same_host = previous && G_TYPE_CHECK_INSTANCE_TYPE(previous, clearcast_http_connectable_type()) &&
            !g_ascii_strcasecmp(g_network_address_get_hostname(G_NETWORK_ADDRESS(previous)), host) &&
            g_network_address_get_port(G_NETWORK_ADDRESS(previous)) == port;
        if (!same_host) {
            GSocketConnectable* endpoint = clearcast_http_connectable_new(host, static_cast<guint16>(port));
            g_object_set(source, "remote-connectable", endpoint, nullptr);
            g_object_unref(endpoint);
        }
        g_clear_object(&previous);
        g_uri_unref(parsed);
        g_free(uri);
        g_object_set(source, "automatic-redirect", FALSE, "ssl-strict", TRUE, "timeout", 15u, nullptr);
        if (!clearcast_http_ca_file.empty()) {
            if (!clearcast_http_database || !g_object_class_find_property(klass, "tls-database")) return GST_STATE_CHANGE_FAILURE;
            g_object_set(source, "tls-database", clearcast_http_database, nullptr);
        }
    }
    return clearcast_http_parent_state(source, transition);
}

static void clearcast_http_class_init(gpointer value, gpointer) {
    auto* klass = static_cast<GstElementClass*>(value);
    auto* parent = static_cast<GstElementClass*>(g_type_class_peek_parent(value));
    clearcast_http_parent_state = parent->change_state;
    klass->change_state = clearcast_http_change_state;
    gst_element_class_set_static_metadata(klass, "ClearCast HTTP source", "Source/Network",
        "HTTP(S) media with verified TLS and redirects disabled", "ClearCast");
}

static gboolean clearcast_appsrc_set_uri(GstURIHandler*, const gchar*, GError** error) {
    g_set_error_literal(error, GST_URI_ERROR, GST_URI_ERROR_BAD_URI, "Application buffers cannot be opened as media URLs.");
    return FALSE;
}

static const gchar* const* clearcast_appsrc_protocols(GType) {
    static const gchar* protocols[] = {nullptr};
    return protocols;
}

static void clearcast_appsrc_interface_init(gpointer value, gpointer) {
    auto* handler_interface = static_cast<GstURIHandlerInterface*>(value);
    handler_interface->set_uri = clearcast_appsrc_set_uri;
    handler_interface->get_protocols = clearcast_appsrc_protocols;
}

static bool clearcast_http_policy_initialize(const char* ca_file) {
    static bool initialized = false;
    if (initialized) return true;
    if (ca_file && *ca_file) {
        if (!g_file_test(ca_file, G_FILE_TEST_IS_REGULAR)) return false;
        clearcast_http_ca_file = ca_file;
        GError* error = nullptr;
        clearcast_http_database = g_tls_file_database_new(ca_file, &error);
        g_clear_error(&error);
        if (!clearcast_http_database) return false;
    }
    GstElementFactory* factory = gst_element_factory_find("souphttpsrc");
    if (!factory) return false;
    GstPluginFeature* loaded = gst_plugin_feature_load(GST_PLUGIN_FEATURE(factory));
    gst_object_unref(factory);
    if (!loaded) return false;
    GType parent = gst_element_factory_get_element_type(GST_ELEMENT_FACTORY(loaded));
    GObjectClass* parent_class = G_OBJECT_CLASS(g_type_class_ref(parent));
    bool guarded = g_object_class_find_property(parent_class, "remote-connectable") != nullptr;
    g_type_class_unref(parent_class);
    if (!guarded) { gst_object_unref(loaded); return false; }
    GTypeQuery query;
    g_type_query(parent, &query);
    GType type = g_type_register_static_simple(parent, "ClearCastHttpSource", query.class_size,
        clearcast_http_class_init, query.instance_size, nullptr, static_cast<GTypeFlags>(0));
    GInterfaceInfo handler_interface = {clearcast_http_interface_init, nullptr, nullptr};
    g_type_add_interface_static(type, GST_TYPE_URI_HANDLER, &handler_interface);
    bool registered = gst_element_register(nullptr, "clearcasthttpsrc", GST_RANK_PRIMARY + 1000, type);
    gst_object_unref(loaded);
    if (!registered) return false;
    // Keep appsrc's explicit buffer-input API for mirroring/audio, while removing
    // its URI protocol so a nested HLS playlist cannot open appsrc:// and hang.
    GstElementFactory* app_factory = gst_element_factory_find("appsrc");
    GstPluginFeature* app_loaded = app_factory ? gst_plugin_feature_load(GST_PLUGIN_FEATURE(app_factory)) : nullptr;
    if (app_factory) gst_object_unref(app_factory);
    if (!app_loaded) return false;
    GType app_parent = gst_element_factory_get_element_type(GST_ELEMENT_FACTORY(app_loaded));
    g_type_query(app_parent, &query);
    GType app_type = g_type_register_static_simple(app_parent, "ClearCastAppSource", query.class_size,
        nullptr, query.instance_size, nullptr, static_cast<GTypeFlags>(0));
    GInterfaceInfo app_interface = {clearcast_appsrc_interface_init, nullptr, nullptr};
    g_type_add_interface_static(app_type, GST_TYPE_URI_HANDLER, &app_interface);
    gst_registry_remove_feature(gst_registry_get(), app_loaded);
    bool app_registered = gst_element_register(nullptr, "appsrc", GST_RANK_NONE, app_type);
    gst_object_unref(app_loaded);
    if (!app_registered) return false;
    // Load these plugins before removing their URI factories. Otherwise a later
    // explicit factory (e.g. coreelements' queue) could reload filesrc with them.
    GstRegistry* registry = gst_registry_get();
    GList* features = gst_registry_get_feature_list(registry, GST_TYPE_ELEMENT_FACTORY);
    for (GList* item = features; item; item = item->next) {
        auto* candidate = GST_ELEMENT_FACTORY(item->data);
        if (gst_element_factory_get_uri_type(candidate) == GST_URI_SRC) {
            GstPluginFeature* ready = gst_plugin_feature_load(GST_PLUGIN_FEATURE(candidate));
            if (ready) gst_object_unref(ready);
        }
    }
    gst_plugin_feature_list_free(features);
    // Nested HLS manifests must never select file/data/RTSP/FTP sources.
    features = gst_registry_get_feature_list(registry, GST_TYPE_ELEMENT_FACTORY);
    for (GList* item = features; item; item = item->next) {
        auto* candidate = GST_ELEMENT_FACTORY(item->data);
        const char* name = GST_OBJECT_NAME(candidate);
        if (gst_element_factory_get_uri_type(candidate) == GST_URI_SRC &&
            strcmp(name, "clearcasthttpsrc") && strcmp(name, "appsrc")) {
            gst_registry_remove_feature(registry, GST_PLUGIN_FEATURE(candidate));
        }
    }
    gst_plugin_feature_list_free(features);
    initialized = true;
    return true;
}
