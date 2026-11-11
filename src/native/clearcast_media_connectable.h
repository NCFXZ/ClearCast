/* Pin each HTTP connection to the same public/LAN address that was checked.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#pragma once

#include <gio/gio.h>
#include <cstring>

static bool clearcast_http_address_allowed(GInetAddress* address) {
    if (!address) return false;
    const guint8* bytes = g_inet_address_to_bytes(address);
    if (g_inet_address_get_family(address) == G_SOCKET_FAMILY_IPV6) {
        static const guint8 mapped[] = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff};
        if (!memcmp(bytes, mapped, sizeof(mapped))) {
            GInetAddress* ipv4 = g_inet_address_new_from_bytes(bytes + 12, G_SOCKET_FAMILY_IPV4);
            bool allowed = clearcast_http_address_allowed(ipv4);
            g_object_unref(ipv4);
            return allowed;
        }
    } else if (g_inet_address_get_family(address) == G_SOCKET_FAMILY_IPV4) {
        // 0/8 and 224/4 or above cannot name an ordinary unicast media host.
        return bytes[0] && bytes[0] != 127 && bytes[0] < 224;
    } else {
        return false;
    }
    return !g_inet_address_get_is_any(address) && !g_inet_address_get_is_loopback(address) &&
        !g_inet_address_get_is_multicast(address);
}

typedef struct {
    GSocketAddressEnumerator parent;
    GSocketAddressEnumerator* inner;
    guint examined;
    bool rejected;
} ClearCastHttpEnumerator;

typedef GSocketAddressEnumeratorClass ClearCastHttpEnumeratorClass;
G_DEFINE_TYPE(ClearCastHttpEnumerator, clearcast_http_enumerator, G_TYPE_SOCKET_ADDRESS_ENUMERATOR)

static bool clearcast_http_socket_allowed(GSocketAddress* address) {
    return G_IS_INET_SOCKET_ADDRESS(address) && !G_IS_PROXY_ADDRESS(address) &&
        clearcast_http_address_allowed(g_inet_socket_address_get_address(G_INET_SOCKET_ADDRESS(address)));
}

static void clearcast_http_enumerator_denied(GError** error) {
    g_set_error_literal(error, G_IO_ERROR, G_IO_ERROR_PERMISSION_DENIED, "The media host has no permitted unicast address.");
}

static GSocketAddress* clearcast_http_enumerator_next(GSocketAddressEnumerator* value, GCancellable* cancel, GError** error) {
    auto* self = reinterpret_cast<ClearCastHttpEnumerator*>(value);
    while (self->examined < 64) {
        if (g_cancellable_set_error_if_cancelled(cancel, error)) return nullptr;
        GSocketAddress* address = g_socket_address_enumerator_next(self->inner, cancel, error);
        if (!address) {
            if (self->rejected && (!error || !*error)) clearcast_http_enumerator_denied(error);
            return nullptr;
        }
        ++self->examined;
        if (clearcast_http_socket_allowed(address)) return address;
        self->rejected = true;
        g_object_unref(address);
    }
    clearcast_http_enumerator_denied(error);
    return nullptr;
}

static void clearcast_http_enumerator_ready(GObject* inner, GAsyncResult* result, gpointer pointer) {
    auto* task = G_TASK(pointer);
    auto* self = reinterpret_cast<ClearCastHttpEnumerator*>(g_task_get_source_object(task));
    GError* error = nullptr;
    GSocketAddress* address = g_socket_address_enumerator_next_finish(G_SOCKET_ADDRESS_ENUMERATOR(inner), result, &error);
    if (g_task_return_error_if_cancelled(task)) {
        g_clear_error(&error);
        g_clear_object(&address);
    } else if (error) {
        g_task_return_error(task, error);
    } else if (address && ++self->examined <= 64 && clearcast_http_socket_allowed(address)) {
        g_task_return_pointer(task, address, g_object_unref);
    } else {
        if (address) {
            self->rejected = true;
            g_object_unref(address);
            if (self->examined < 64) {
                g_socket_address_enumerator_next_async(self->inner, g_task_get_cancellable(task),
                    clearcast_http_enumerator_ready, task);
                return;
            }
        }
        if (self->rejected) {
            clearcast_http_enumerator_denied(&error);
            g_task_return_error(task, error);
        } else {
            g_task_return_pointer(task, nullptr, nullptr);
        }
    }
    g_object_unref(task);
}

static void clearcast_http_enumerator_next_async(GSocketAddressEnumerator* value, GCancellable* cancel,
    GAsyncReadyCallback callback, gpointer pointer) {
    GTask* task = g_task_new(value, cancel, callback, pointer);
    auto* self = reinterpret_cast<ClearCastHttpEnumerator*>(value);
    if (self->examined >= 64) {
        GError* error = nullptr;
        clearcast_http_enumerator_denied(&error);
        g_task_return_error(task, error);
        g_object_unref(task);
        return;
    }
    g_socket_address_enumerator_next_async(self->inner, cancel, clearcast_http_enumerator_ready, task);
}

static GSocketAddress* clearcast_http_enumerator_next_finish(GSocketAddressEnumerator* value, GAsyncResult* result, GError** error) {
    g_return_val_if_fail(g_task_is_valid(result, value), nullptr);
    return G_SOCKET_ADDRESS(g_task_propagate_pointer(G_TASK(result), error));
}

static void clearcast_http_enumerator_finalize(GObject* value) {
    auto* self = reinterpret_cast<ClearCastHttpEnumerator*>(value);
    g_clear_object(&self->inner);
    G_OBJECT_CLASS(clearcast_http_enumerator_parent_class)->finalize(value);
}

static void clearcast_http_enumerator_class_init(ClearCastHttpEnumeratorClass* klass) {
    klass->next = clearcast_http_enumerator_next;
    klass->next_async = clearcast_http_enumerator_next_async;
    klass->next_finish = clearcast_http_enumerator_next_finish;
    G_OBJECT_CLASS(klass)->finalize = clearcast_http_enumerator_finalize;
}

static void clearcast_http_enumerator_init(ClearCastHttpEnumerator*) {}

static GSocketConnectableIface* clearcast_http_network_parent;

static GSocketAddressEnumerator* clearcast_http_network_enumerate(GSocketConnectable* endpoint) {
    auto* enumerator = static_cast<ClearCastHttpEnumerator*>(g_object_new(clearcast_http_enumerator_get_type(), nullptr));
    enumerator->inner = clearcast_http_network_parent->enumerate(endpoint);
    return G_SOCKET_ADDRESS_ENUMERATOR(enumerator);
}

static void clearcast_http_network_interface_init(gpointer value, gpointer) {
    auto* iface = static_cast<GSocketConnectableIface*>(value);
    clearcast_http_network_parent = static_cast<GSocketConnectableIface*>(g_type_interface_peek_parent(value));
    iface->enumerate = clearcast_http_network_enumerate;
    // A proxy must not choose an unchecked destination or re-resolve the name.
    iface->proxy_enumerate = clearcast_http_network_enumerate;
}

static GType clearcast_http_connectable_type() {
    static GType type = [] {
        GTypeQuery query;
        g_type_query(G_TYPE_NETWORK_ADDRESS, &query);
        // Retain GNetworkAddress's original hostname/port and type identity:
        // libsoup also uses this object for TLS certificate validation and SNI.
        GType child = g_type_register_static_simple(G_TYPE_NETWORK_ADDRESS, "ClearCastHttpNetworkAddress",
            query.class_size, nullptr, query.instance_size, nullptr, static_cast<GTypeFlags>(0));
        GInterfaceInfo info = {clearcast_http_network_interface_init, nullptr, nullptr};
        g_type_add_interface_static(child, G_TYPE_SOCKET_CONNECTABLE, &info);
        return child;
    }();
    return type;
}

static GSocketConnectable* clearcast_http_connectable_new(const char* host, guint16 port) {
    return G_SOCKET_CONNECTABLE(g_object_new(clearcast_http_connectable_type(), "hostname", host, "port", static_cast<guint>(port), nullptr));
}
