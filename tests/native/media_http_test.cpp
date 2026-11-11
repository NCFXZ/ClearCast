/* Exercise the production HTTP source policy, including private HLS fetches.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include <objbase.h>
#include <gst/gst.h>
#include <gst/video/video.h>
#include <glib/gstdio.h>
#include <atomic>
#include <cstdio>
#include "clearcast_media_http.h"

static std::atomic<unsigned int> frames(0);
static void handoff(GstElement*, GstBuffer*, GstPad*, gpointer) { ++frames; }

// Test executable only: deterministic DNS answers exercise the production
// connectable without modifying Windows DNS/hosts or bypassing its policy.
static const char* fixture_address;
static std::atomic<unsigned int> fixture_dns_lookups(0);
typedef struct { GResolver parent; } FixtureResolver;
typedef GResolverClass FixtureResolverClass;
G_DEFINE_TYPE(FixtureResolver, fixture_resolver, G_TYPE_RESOLVER)

static GList* fixture_lookup_flags(GResolver*, const char* hostname, GResolverNameLookupFlags flags, GCancellable* cancel, GError** error) {
    if (g_cancellable_set_error_if_cancelled(cancel, error)) return nullptr;
    if (flags == G_RESOLVER_NAME_LOOKUP_FLAGS_IPV6_ONLY || !g_str_has_suffix(hostname, ".clearcast.test")) {
        g_set_error_literal(error, G_RESOLVER_ERROR, G_RESOLVER_ERROR_NOT_FOUND, "No fixture DNS record");
        return nullptr;
    }
    unsigned int lookup = fixture_dns_lookups.fetch_add(1);
    bool loopback = !strcmp(hostname, "loopback.clearcast.test") || (!strcmp(hostname, "rebind.clearcast.test") && lookup);
    GList* addresses = g_list_append(nullptr, g_inet_address_new_from_string(loopback ? "127.0.0.1" : fixture_address));
    if (!strcmp(hostname, "mixed.clearcast.test"))
        addresses = g_list_prepend(addresses, g_inet_address_new_from_string("127.0.0.1"));
    return addresses;
}

static GList* fixture_lookup(GResolver* resolver, const char* hostname, GCancellable* cancel, GError** error) {
    return fixture_lookup_flags(resolver, hostname, G_RESOLVER_NAME_LOOKUP_FLAGS_DEFAULT, cancel, error);
}

static void fixture_lookup_flags_async(GResolver* resolver, const char* hostname, GResolverNameLookupFlags flags,
    GCancellable* cancel, GAsyncReadyCallback callback, gpointer pointer) {
    GTask* task = g_task_new(resolver, cancel, callback, pointer);
    GError* error = nullptr;
    GList* addresses = fixture_lookup_flags(resolver, hostname, flags, cancel, &error);
    if (error) g_task_return_error(task, error);
    else g_task_return_pointer(task, addresses, reinterpret_cast<GDestroyNotify>(g_resolver_free_addresses));
    g_object_unref(task);
}

static void fixture_lookup_async(GResolver* resolver, const char* hostname, GCancellable* cancel,
    GAsyncReadyCallback callback, gpointer pointer) {
    fixture_lookup_flags_async(resolver, hostname, G_RESOLVER_NAME_LOOKUP_FLAGS_DEFAULT, cancel, callback, pointer);
}

static GList* fixture_lookup_finish(GResolver* resolver, GAsyncResult* result, GError** error) {
    g_return_val_if_fail(g_task_is_valid(result, resolver), nullptr);
    return static_cast<GList*>(g_task_propagate_pointer(G_TASK(result), error));
}

static void fixture_resolver_class_init(FixtureResolverClass* klass) {
    klass->lookup_by_name = fixture_lookup;
    klass->lookup_by_name_async = fixture_lookup_async;
    klass->lookup_by_name_finish = fixture_lookup_finish;
    klass->lookup_by_name_with_flags = fixture_lookup_flags;
    klass->lookup_by_name_with_flags_async = fixture_lookup_flags_async;
    klass->lookup_by_name_with_flags_finish = fixture_lookup_finish;
}

static void fixture_resolver_init(FixtureResolver*) {}

struct AddressProbe {
    GMainLoop* loop;
    bool allowed;
    bool cancelled;
};

static void address_ready(GObject* enumerator, GAsyncResult* result, gpointer pointer) {
    auto* probe = static_cast<AddressProbe*>(pointer);
    GError* error = nullptr;
    GSocketAddress* address = g_socket_address_enumerator_next_finish(G_SOCKET_ADDRESS_ENUMERATOR(enumerator), result, &error);
    probe->allowed = address && clearcast_http_socket_allowed(address);
    probe->cancelled = error && g_error_matches(error, G_IO_ERROR, G_IO_ERROR_CANCELLED);
    g_clear_object(&address);
    g_clear_error(&error);
    g_main_loop_quit(probe->loop);
}

static bool address_policy() {
    const char* denied[] = {"0.0.0.0", "0.12.34.56", "127.0.0.1", "127.99.5.7", "224.0.0.1", "239.255.255.250",
        "255.255.255.255", "::", "::1", "ff02::fb", "::ffff:127.0.0.1", "::ffff:0.0.0.0", "::ffff:224.0.0.1", nullptr};
    const char* permitted[] = {"10.0.0.1", "172.16.0.1", "192.168.1.100", "8.8.8.8", "169.254.1.2", "2001:db8::1",
        "fe80::1234", "fd12::1", "::ffff:192.168.1.2", nullptr};
    for (const char** value = denied; *value; ++value) {
        GInetAddress* address = g_inet_address_new_from_string(*value);
        bool allowed = clearcast_http_address_allowed(address);
        g_clear_object(&address);
        if (allowed) return false;
    }
    for (const char** value = permitted; *value; ++value) {
        GInetAddress* address = g_inet_address_new_from_string(*value);
        bool allowed = clearcast_http_address_allowed(address);
        g_clear_object(&address);
        if (!allowed) return false;
    }
    const char* hosts[] = {"127.0.0.1", "127.1", "0x7f000001", "2130706433", "localhost", "::1", "::ffff:127.0.0.1",
        "192.168.1.100", "2001:db8::1", nullptr};
    for (unsigned int i = 0; hosts[i]; ++i) {
        GSocketConnectable* endpoint = clearcast_http_connectable_new(hosts[i], 1234);
        bool identity = G_IS_NETWORK_ADDRESS(endpoint) &&
            !strcmp(g_network_address_get_hostname(G_NETWORK_ADDRESS(endpoint)), hosts[i]) &&
            g_network_address_get_port(G_NETWORK_ADDRESS(endpoint)) == 1234;
        GSocketAddressEnumerator* enumerator = g_socket_connectable_enumerate(endpoint);
        GError* error = nullptr;
        GSocketAddress* address = g_socket_address_enumerator_next(enumerator, nullptr, &error);
        bool allowed = address && clearcast_http_socket_allowed(address);
        bool expected = i >= 7;
        g_clear_error(&error);
        g_clear_object(&address);
        g_object_unref(enumerator);
        enumerator = g_socket_connectable_proxy_enumerate(endpoint);
        AddressProbe probe{g_main_loop_new(nullptr, FALSE), false, false};
        g_socket_address_enumerator_next_async(enumerator, nullptr, address_ready, &probe);
        g_main_loop_run(probe.loop);
        bool asynchronous = probe.allowed == expected;
        g_main_loop_unref(probe.loop);
        g_object_unref(enumerator);
        g_object_unref(endpoint);
        if (!identity || allowed != expected || !asynchronous) return false;
    }
    GSocketConnectable* endpoint = clearcast_http_connectable_new("192.168.1.100", 80);
    GSocketAddressEnumerator* enumerator = g_socket_connectable_enumerate(endpoint);
    GCancellable* cancel = g_cancellable_new();
    g_cancellable_cancel(cancel);
    AddressProbe probe{g_main_loop_new(nullptr, FALSE), false, false};
    g_socket_address_enumerator_next_async(enumerator, cancel, address_ready, &probe);
    g_main_loop_run(probe.loop);
    g_main_loop_unref(probe.loop);
    g_object_unref(cancel);
    g_object_unref(enumerator);
    g_object_unref(endpoint);
    return probe.cancelled && !probe.allowed;
}

struct AppBufferProbe {
    std::atomic<unsigned int> need_data{0}, received{0}, bytes{0}, sequence_mask{0}, errors{0};
    std::atomic<bool> supplied{false};
};

static void supply_app_buffers(GstElement* source, guint, gpointer pointer) {
    auto& probe = *static_cast<AppBufferProbe*>(pointer);
    ++probe.need_data;
    if (probe.supplied.exchange(true)) return;
    for (unsigned int sequence = 0; sequence < 3; ++sequence) {
        unsigned char bytes[64];
        for (unsigned int i = 0; i < sizeof(bytes); ++i) bytes[i] = static_cast<unsigned char>(i * 13 + sequence * 37);
        GstBuffer* buffer = gst_buffer_new_allocate(nullptr, sizeof(bytes), nullptr);
        if (!buffer) { ++probe.errors; break; }
        if (gst_buffer_fill(buffer, 0, bytes, sizeof(bytes)) != sizeof(bytes)) ++probe.errors;
        GST_BUFFER_OFFSET(buffer) = sequence;
        GST_BUFFER_PTS(buffer) = sequence * GST_MSECOND;
        GST_BUFFER_DURATION(buffer) = GST_MSECOND;
        GstFlowReturn flow = GST_FLOW_ERROR;
        g_signal_emit_by_name(source, "push-buffer", buffer, &flow);
        // The action signal retains its own reference, unlike the direct appsrc API.
        gst_buffer_unref(buffer);
        if (flow != GST_FLOW_OK) { ++probe.errors; break; }
    }
    GstFlowReturn eos = GST_FLOW_ERROR;
    g_signal_emit_by_name(source, "end-of-stream", &eos);
    if (eos != GST_FLOW_OK) ++probe.errors;
}

static void receive_app_buffer(GstElement*, GstBuffer* buffer, GstPad*, gpointer pointer) {
    auto& probe = *static_cast<AppBufferProbe*>(pointer);
    unsigned char bytes[64];
    const guint64 sequence = GST_BUFFER_OFFSET(buffer);
    bool valid = sequence < 3 && gst_buffer_get_size(buffer) == sizeof(bytes) &&
        gst_buffer_extract(buffer, 0, bytes, sizeof(bytes)) == sizeof(bytes);
    for (unsigned int i = 0; valid && i < sizeof(bytes); ++i)
        valid = bytes[i] == static_cast<unsigned char>(i * 13 + sequence * 37);
    if (valid) {
        unsigned int bit = 1u << static_cast<unsigned int>(sequence);
        if (probe.sequence_mask.fetch_or(bit) & bit) ++probe.errors;
        probe.bytes += sizeof(bytes);
    } else {
        ++probe.errors;
    }
    ++probe.received;
}

static bool appsrc_buffer_transport(AppBufferProbe& probe) {
    GstElement* pipeline = gst_pipeline_new(nullptr);
    GstElement* source = gst_element_factory_make("appsrc", nullptr);
    GstElement* sink = gst_element_factory_make("fakesink", nullptr);
    if (!pipeline || !source || !sink) {
        if (source) gst_object_unref(source);
        if (sink) gst_object_unref(sink);
        if (pipeline) gst_object_unref(pipeline);
        return false;
    }
    bool replaced = !strcmp(G_OBJECT_TYPE_NAME(source), "ClearCastAppSource");
    gst_bin_add_many(GST_BIN(pipeline), source, sink, nullptr);
    GstCaps* caps = gst_caps_from_string("audio/x-raw,format=S16LE,layout=interleaved,channels=2,rate=44100");
    g_object_set(source, "caps", caps, "format", GST_FORMAT_TIME, "emit-signals", TRUE, "block", FALSE, nullptr);
    gst_caps_unref(caps);
    g_object_set(sink, "sync", FALSE, "signal-handoffs", TRUE, nullptr);
    g_signal_connect(source, "need-data", G_CALLBACK(supply_app_buffers), &probe);
    g_signal_connect(sink, "handoff", G_CALLBACK(receive_app_buffer), &probe);
    GstBus* bus = gst_element_get_bus(pipeline);
    bool eos = false;
    if (gst_element_link(source, sink) && gst_element_set_state(pipeline, GST_STATE_PLAYING) != GST_STATE_CHANGE_FAILURE) {
        GstMessage* message = gst_bus_timed_pop_filtered(bus, 5 * GST_SECOND,
            static_cast<GstMessageType>(GST_MESSAGE_EOS | GST_MESSAGE_ERROR));
        eos = message && GST_MESSAGE_TYPE(message) == GST_MESSAGE_EOS;
        if (message) gst_message_unref(message);
    }
    gst_element_set_state(pipeline, GST_STATE_NULL);
    gst_object_unref(bus);
    gst_object_unref(pipeline);
    return replaced && eos && probe.need_data && probe.received == 3 && probe.bytes == 192 &&
        probe.sequence_mask == 7 && !probe.errors;
}

struct FixtureRuntime {
    gchar* registry_directory = nullptr;
    ~FixtureRuntime() {
        if (!registry_directory) return;
        gchar* registry = g_build_filename(registry_directory, "registry.bin", nullptr);
        g_remove(registry); g_free(registry);
        g_rmdir(registry_directory); g_free(registry_directory);
    }
    bool configure(const char* runtime) {
        gchar* plugins = g_build_filename(runtime, "lib", "gstreamer-1.0", nullptr);
        gchar* modules = g_build_filename(runtime, "lib", "gio", "modules", nullptr);
        gchar* scanner = g_build_filename(runtime, "gst-plugin-scanner.exe", nullptr);
        gchar* parent = g_path_get_dirname(runtime);
        registry_directory = g_build_filename(parent, "http-policy-registry-XXXXXX", nullptr);
        g_free(parent);
        bool valid = g_file_test(plugins, G_FILE_TEST_IS_DIR) && g_file_test(modules, G_FILE_TEST_IS_DIR) &&
            g_file_test(scanner, G_FILE_TEST_IS_REGULAR) && g_mkdtemp(registry_directory);
        if (valid) {
            gchar* registry = g_build_filename(registry_directory, "registry.bin", nullptr);
            g_setenv("GST_PLUGIN_SYSTEM_PATH_1_0", plugins, TRUE);
            g_setenv("GST_PLUGIN_PATH_1_0", "", TRUE);
            g_setenv("GST_PLUGIN_SCANNER_1_0", scanner, TRUE);
            g_setenv("GST_REGISTRY", registry, TRUE);
            g_setenv("GST_REGISTRY_1_0", registry, TRUE);
            g_setenv("GIO_EXTRA_MODULES", modules, TRUE);
            gst_registry_fork_set_enabled(FALSE);
            g_free(registry);
        } else {
            g_clear_pointer(&registry_directory, g_free);
        }
        g_free(plugins); g_free(modules); g_free(scanner);
        return valid;
    }
};

int main(int argc, char** argv) {
    FixtureRuntime runtime;
    std::string runtime_ca;
    const char* ca_file = argc > 3 ? argv[3] : nullptr;
    if (argc > 2 && !strcmp(argv[1], "--policy") && !strcmp(argv[2], "--runtime")) {
        if (argc != 4 || !runtime.configure(argv[3])) {
            fprintf(stderr, "A complete, writable controlled runtime is required for --policy --runtime.\n");
            return 2;
        }
        runtime_ca = std::string(argv[3]) + "/certs/ca-bundle.crt";
        ca_file = runtime_ca.c_str();
    }
    if (argc > 4) {
        fixture_address = argv[4];
        GInetAddress* address = g_inet_address_new_from_string(fixture_address);
        bool allowed = clearcast_http_address_allowed(address);
        g_clear_object(&address);
        if (!allowed) return 13;
        GResolver* resolver = G_RESOLVER(g_object_new(fixture_resolver_get_type(), nullptr));
        g_resolver_set_default(resolver);
        g_object_unref(resolver);
    }
    gst_init(nullptr, nullptr);
    if (argc > 1 && !strcmp(argv[1], "--address-policy")) {
        bool passed = address_policy();
        printf("{\"addressPolicy\":%s,\"passed\":%s}\n", passed ? "true" : "false", passed ? "true" : "false");
        return passed ? 0 : 12;
    }
    if (!clearcast_http_policy_initialize(ca_file)) {
        fprintf(stderr, "Could not initialize the controlled HTTP source policy.\n");
        return 2;
    }
    if (argc < 2 || !strcmp(argv[1], "--policy")) {
        const char* invalid[] = {"file:///C:/secret", "ftp://127.0.0.1/a", "data:video/mp4,x", "http://user:password@127.0.0.1/a",
            "https://user%40host@localhost/a", "http://localhost/a#fragment", "http://localhost/a\r\nx", "http:///a", "appsrc://buffer",
            "http://127.0.0.1/a", "http://127.99.5.7/a", "http://[::1]/a", "http://[::ffff:127.0.0.1]/a", "http://0.0.0.0/a",
            "http://[::]/a", "http://224.0.0.1/a", "http://255.255.255.255/a", "http://192.168.1.2:0/a", nullptr};
        for (const char** uri = invalid; *uri; ++uri) {
            if (clearcast_http_uri_allowed(*uri)) return 3;
            GError* error = nullptr;
            GstElement* source = gst_element_make_from_uri(GST_URI_SRC, *uri, nullptr, &error);
            g_clear_error(&error);
            if (source) { gst_object_unref(source); return 4; }
        }
        if (!address_policy() || !clearcast_http_uri_allowed("http://192.168.1.100:1234/video.mp4") ||
            !clearcast_http_uri_allowed("https://example.com/master.m3u8?token=1")) return 5;
        GstElement* buffers = gst_element_factory_make("appsrc", nullptr);
        if (!buffers || !g_object_class_find_property(G_OBJECT_GET_CLASS(buffers), "caps")) return 9;
        gst_object_unref(buffers);
        AppBufferProbe probe;
        if (!appsrc_buffer_transport(probe)) return 10;
        // These FFmpeg input paths are deliberately absent from gst-libav's
        // registered demuxers; MP4/HLS use their native GStreamer equivalents.
        const char* excluded_demuxers[] = {"avdemux_mov", "avdemux_mov_mp4_m4a_3gp_3g2_mj2", "avdemux_hls",
            "avdemux_applehttp", "avdemux_dash", "avdemux_rtsp", "avdemux_sdp"};
        for (const char* name : excluded_demuxers) {
            GstElementFactory* factory = gst_element_factory_find(name);
            if (factory) { gst_object_unref(factory); return 11; }
        }
        printf("{\"policyCases\":%u,\"addressPolicy\":true,\"excludedAvDemuxers\":7,\"appsrcBuffers\":%u,\"appsrcBytes\":%u,\"appsrcNeedData\":%u,\"passed\":true}\n",
            static_cast<unsigned int>(G_N_ELEMENTS(invalid) - 1 + 2), probe.received.load(), probe.bytes.load(), probe.need_data.load());
        return 0;
    }
    bool expect_rejection = argc > 2 && !strcmp(argv[2], "--reject");
    GstElement* player = gst_element_factory_make("playbin", nullptr);
    GstElement* video = gst_element_factory_make("fakesink", nullptr);
    GstElement* audio = gst_element_factory_make("fakesink", nullptr);
    if (!player || !video || !audio) return 6;
    gst_object_ref_sink(player);
    gst_object_ref_sink(video);
    gst_object_ref_sink(audio);
    g_object_set(video, "sync", TRUE, "signal-handoffs", TRUE, nullptr);
    g_object_set(audio, "sync", TRUE, nullptr);
    g_signal_connect(video, "handoff", G_CALLBACK(handoff), nullptr);
    g_object_set(player, "uri", argv[1], "video-sink", video, "audio-sink", audio, nullptr);
    GstBus* bus = gst_element_get_bus(player);
    bool error = gst_element_set_state(player, GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE;
    bool eos = false;
    gint64 deadline = g_get_monotonic_time() + 20 * G_TIME_SPAN_SECOND;
    while (!error && !eos && g_get_monotonic_time() < deadline) {
        GstMessage* message = gst_bus_timed_pop_filtered(bus, 100 * GST_MSECOND,
            static_cast<GstMessageType>(GST_MESSAGE_ERROR | GST_MESSAGE_EOS));
        if (!message) continue;
        error = GST_MESSAGE_TYPE(message) == GST_MESSAGE_ERROR;
        eos = GST_MESSAGE_TYPE(message) == GST_MESSAGE_EOS;
        if (error) {
            GError* failure = nullptr;
            gchar* debug = nullptr;
            gst_message_parse_error(message, &failure, &debug);
            fprintf(stderr, "media error domain=%s code=%d message=%s details=%s\n", g_quark_to_string(failure->domain),
                failure->code, failure->message, debug ? debug : "");
            g_clear_error(&failure);
            g_free(debug);
        }
        gst_message_unref(message);
    }
    GstPad* pad = gst_element_get_static_pad(video, "sink");
    GstCaps* caps = pad ? gst_pad_get_current_caps(pad) : nullptr;
    GstVideoInfo info;
    bool geometry = caps && gst_video_info_from_caps(&info, caps);
    printf("{\"frames\":%u,\"width\":%u,\"height\":%u,\"error\":%s,\"eos\":%s,\"dnsLookups\":%u}\n",
        frames.load(), geometry ? info.width : 0, geometry ? info.height : 0, error ? "true" : "false", eos ? "true" : "false", fixture_dns_lookups.load());
    if (caps) gst_caps_unref(caps);
    if (pad) gst_object_unref(pad);
    gst_element_set_state(player, GST_STATE_NULL);
    gst_object_unref(bus);
    gst_object_unref(player);
    gst_object_unref(video);
    gst_object_unref(audio);
    return expect_rejection ? (error && !frames ? 0 : 7) : (!error && eos && frames && geometry ? 0 : 8);
}
