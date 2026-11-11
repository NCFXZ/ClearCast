/*
 * ClearCast's in-process UxPlay entry points. Included by the pinned UxPlay
 * translation unit to share its RAOP callbacks and renderer configuration.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#pragma once

#include <gst/base/gstbasesink.h>
#include <gst/gst.h>
#include <gst/video/video.h>
#include <glib/gstdio.h>
#include <cstdarg>
#include <new>
#include <mutex>
#include <cmath>
#include <gst/video/videooverlay.h>
#include "lib/global.h"
#include "lib/mdnsd/mdnsd.h"
#include "clearcast_media_http.h"
#include "clearcast_video_frame.h"
#include "clearcast_event_queue.h"
#include "clearcast_network_scope.h"
namespace { void write_diagnostic(const char* format, ...) noexcept; }
#include "clearcast_media_audio.h"

extern "C" void clearcast_decoder_result(int decoder, int error);

namespace {

struct ReceiverOptions {
    std::string state_directory;
    unsigned short width;
    unsigned short height;
    unsigned short fps;
    int decoder;
    bool hevc;
    HWND parent;
    uint32_t mdns_address;
};

SRWLOCK lifecycle_lock = SRWLOCK_INIT;
SRWLOCK event_lock = SRWLOCK_INIT;
SRWLOCK stats_lock = SRWLOCK_INIT;
SRWLOCK diagnostic_lock = SRWLOCK_INIT;
std::string diagnostic_path;
std::string diagnostic_previous_path;
uint64_t diagnostic_connection_id = 0;
HANDLE worker_thread = nullptr;
GstBaseSink* stats_sink = nullptr;
uint64_t video_frame_generation = 0;
int requested_rotation = 0;
int applied_rotation = -1;
ClearCastEventQueue events;
std::atomic<bool> mirror_active(false);
std::atomic<bool> decoder_commands_ready(false);
std::atomic<int> receiver_failure(0);
std::atomic<unsigned int> pairing_generation(0);

struct ExclusiveLock {
    explicit ExclusiveLock(SRWLOCK& lock) : lock(lock) { AcquireSRWLockExclusive(&lock); }
    ~ExclusiveLock() { ReleaseSRWLockExclusive(&lock); }
    SRWLOCK& lock;
};

// Only explicit connection metadata reaches this writer. Never pass UxPlay's
// general logger, request headers, bodies, credentials or pairing material here.
void write_diagnostic(const char* format, ...) noexcept {
    char message[512];
    va_list args;
    va_start(args, format);
    vsnprintf(message, sizeof(message), format, args);
    va_end(args);
    SYSTEMTIME now;
    GetSystemTime(&now);
    char entry[576];
    int count = snprintf(entry, sizeof(entry),
        "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ %s\n",
        static_cast<unsigned>(now.wYear), static_cast<unsigned>(now.wMonth),
        static_cast<unsigned>(now.wDay), static_cast<unsigned>(now.wHour),
        static_cast<unsigned>(now.wMinute), static_cast<unsigned>(now.wSecond),
        static_cast<unsigned>(now.wMilliseconds), message);
    if (count <= 0 || static_cast<size_t>(count) >= sizeof(entry)) return;

    ExclusiveLock lock(diagnostic_lock);
    if (diagnostic_path.empty()) return;
    FILE* file = g_fopen(diagnostic_path.c_str(), "a+b");
    if (!file) return;
    if (fseek(file, 0, SEEK_END) != 0) {
        fclose(file);
        return;
    }
    long size = ftell(file);
    constexpr long limit = 256 * 1024;
    if (size < 0 || size + count > limit) {
        fclose(file);
        if (size < 0) return;
        g_remove(diagnostic_previous_path.c_str());
        if (g_rename(diagnostic_path.c_str(), diagnostic_previous_path.c_str()) != 0) return;
        file = g_fopen(diagnostic_path.c_str(), "wb");
        if (!file) return;
    }
    fwrite(entry, 1, count, file);
    fclose(file);
}

void diagnostic_token(const char* input, char* output, size_t size, bool path = false) noexcept {
    if (!input) input = "";
    if (path) {
        const char* authority = nullptr;
        for (const char* scheme : {"rtsp://", "http://", "https://"}) {
            if (!g_ascii_strncasecmp(input, scheme, strlen(scheme))) {
                authority = input + strlen(scheme);
                break;
            }
        }
        if (authority) {
            const char* slash = strchr(authority, '/');
            input = slash ? slash : "/";
        }
    }
    size_t length = 0;
    while (*input && length + 1 < size && (!path || (*input != '?' && *input != '#'))) {
        unsigned char ch = static_cast<unsigned char>(*input++);
        output[length++] = ch > 32 && ch < 127 ? static_cast<char>(ch) : '_';
    }
    output[length] = '\0';
}

void diagnostic_ready() noexcept {
    uint32_t address = mdnsd_get_active_ipv4();
    const auto* bytes = reinterpret_cast<const unsigned char*>(&address);
    write_diagnostic("ready address=%u.%u.%u.%u rtsp=%u tcp=%u,%u,%u udp=%u,%u,%u",
        static_cast<unsigned>(bytes[0]), static_cast<unsigned>(bytes[1]),
        static_cast<unsigned>(bytes[2]), static_cast<unsigned>(bytes[3]),
        static_cast<unsigned>(raop_get_port(raop)),
        static_cast<unsigned>(tcp[0]), static_cast<unsigned>(tcp[1]), static_cast<unsigned>(tcp[2]),
        static_cast<unsigned>(udp[0]), static_cast<unsigned>(udp[1]), static_cast<unsigned>(udp[2]));
}

void set_stats_sink(GstElement* sink) {
    ExclusiveLock lock(stats_lock);
    GstBaseSink* previous = stats_sink;
    stats_sink = sink && mirror_active && GST_IS_BASE_SINK(sink)
        ? GST_BASE_SINK(gst_object_ref(sink)) : nullptr;
    if (!stats_sink) {
        requested_rotation = 0;
    }
    if (stats_sink != previous) {
        applied_rotation = -1;
        ++video_frame_generation;
    }
    if (previous) {
        gst_object_unref(previous);
    }
}

void enqueue_event(ReceiverEvent event) {
    ExclusiveLock lock(event_lock);
    events.push(event);
}

#include "clearcast_media_video.h"

static bool clearcast_mirror_media_active() {
    return mirror_active && !direct_video_active;
}

static clearcast_diagnostics::Info clearcast_video_diagnostics(bool direct) {
    GstElement* pipeline = direct ? clearcast_direct_video_ref(true) : nullptr;
    if (!direct) {
        GstObject* object = nullptr;
        { ExclusiveLock lock(stats_lock); if (stats_sink && mirror_active) object = GST_OBJECT(gst_object_ref(stats_sink)); }
        // The mirror renderer owns a standalone pipeline. Keep references while
        // walking parents so a simultaneous disconnect can safely dispose it.
        for (int depth = 0; object && depth < 16; ++depth) {
            if (GST_IS_PIPELINE(object)) { pipeline = GST_ELEMENT(object); object = nullptr; break; }
            GstObject* parent = gst_object_get_parent(object);
            gst_object_unref(object); object = parent;
        }
        if (object) gst_object_unref(object);
    }
    auto result = clearcast_diagnostics::collect(pipeline, true, direct);
    if (pipeline) gst_object_unref(pipeline);
    return result;
}

void clear_events() {
    ExclusiveLock lock(event_lock);
    events.clear();
}

bool parse_dimension(const char*& value, unsigned short& dimension) {
    unsigned int result = 0;
    const char* start = value;
    while (*value >= '0' && *value <= '9' && result <= 6553) {
        result = result * 10 + (*value++ - '0');
    }
    if (start == value || !result || result > 65535) {
        return false;
    }
    dimension = static_cast<unsigned short>(result);
    return true;
}

struct ScopedEnvironment {
    ScopedEnvironment(const char* name, const char* value)
        : name(name), original(g_strdup(g_getenv(name))) {
        g_setenv(name, value, TRUE);
    }
    ~ScopedEnvironment() {
        if (original) {
            g_setenv(name, original, TRUE);
        } else {
            g_unsetenv(name);
        }
        g_free(original);
    }
    const char* name;
    gchar* original;
};

int initialize_gstreamer(const std::string& state_directory) {
    static bool portable_plugins_loaded = false;
    if (!portable_plugins_loaded) {
        HMODULE module = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                               reinterpret_cast<LPCWSTR>(&initialize_gstreamer), &module)) {
            return GetLastError();
        }
        wchar_t module_path[32768];
        DWORD length = GetModuleFileNameW(module, module_path, ARRAYSIZE(module_path));
        if (!length || length == ARRAYSIZE(module_path)) {
            return ERROR_BAD_PATHNAME;
        }
        std::wstring runtime_directory(module_path, length);
        runtime_directory.resize(runtime_directory.find_last_of(L"\\/"));
        std::wstring plugin_directory = runtime_directory + L"\\lib\\gstreamer-1.0";
        gchar* plugin_path = g_utf16_to_utf8(reinterpret_cast<const gunichar2*>(plugin_directory.c_str()),
                                            -1, nullptr, nullptr, nullptr);
        if (!plugin_path) {
            return ERROR_BAD_PATHNAME;
        }
        std::unique_ptr<gchar, decltype(&g_free)> portable_path(plugin_path, &g_free);
        if (!gst_is_initialized()) {
            std::wstring scanner_file = runtime_directory + L"\\gst-plugin-scanner.exe";
            if (GetFileAttributesW(scanner_file.c_str()) == INVALID_FILE_ATTRIBUTES) {
                return GetLastError();
            }
            std::unique_ptr<gchar, decltype(&g_free)> scanner_path(
                g_utf16_to_utf8(reinterpret_cast<const gunichar2*>(scanner_file.c_str()),
                    -1, nullptr, nullptr, nullptr), &g_free);
            if (!scanner_path) {
                return ERROR_BAD_PATHNAME;
            }
            // Limit the initial scan and registry to this app's packaged runtime.
            // Probe graphics-driver capabilities in the bundled helper process,
            // so cold D3D11 discovery does not stall WinUI's composition buffers.
            const std::string registry_path = state_directory + "/gstreamer-registry.bin";
            ScopedEnvironment registry_option("GST_REGISTRY", registry_path.c_str());
            ScopedEnvironment system_plugins("GST_PLUGIN_SYSTEM_PATH_1_0", plugin_path);
            ScopedEnvironment extra_plugins("GST_PLUGIN_PATH_1_0", "");
            ScopedEnvironment scanner_option("GST_PLUGIN_SCANNER_1_0", scanner_path.get());
            gst_registry_fork_set_enabled(TRUE);
            GError* error = nullptr;
            const bool initialized = gst_init_check(nullptr, nullptr, &error);
            g_clear_error(&error);
            if (!initialized) {
                return ERROR_DLL_INIT_FAILED;
            }
        } else {
            gst_registry_scan_path(gst_registry_get(), plugin_path);
        }
        GstRegistry* registry = gst_registry_get();
        if (!gstreamer_init()) {
            return ERROR_MOD_NOT_FOUND;
        }
        for (const char* name : {"d3d11h264dec", "d3d11h265dec"}) {
            GstPluginFeature* feature = gst_registry_find_feature(registry, name, GST_TYPE_ELEMENT_FACTORY);
            if (feature) {
                gst_plugin_feature_set_rank(feature, 512);
                gst_object_unref(feature);
            }
        }
        portable_plugins_loaded = true;
    }
    return 0;
}

std::string make_receiver_display_name() {
    wchar_t computer_name[64];
    DWORD length = ARRAYSIZE(computer_name);
    if (!GetComputerNameExW(ComputerNamePhysicalDnsHostname, computer_name, &length) || !length) {
        length = ARRAYSIZE(computer_name);
        if (!GetComputerNameExW(ComputerNamePhysicalNetBIOS, computer_name, &length) || !length) {
            return "ClearCast";
        }
    }

    auto converted = g_utf16_to_utf8(reinterpret_cast<const gunichar2*>(computer_name), length,
                                     nullptr, nullptr, nullptr);
    std::unique_ptr<gchar, decltype(&g_free)> hostname(converted, &g_free);
    if (!hostname) {
        return "ClearCast";
    }

    // RAOP prepends MAC@ inside the same 63-byte DNS label.
    constexpr char prefix[] = "ClearCast ";
    constexpr size_t max_hostname_bytes = 63 - (2 * MAX_HWADDR_LEN + 1) - (sizeof(prefix) - 1);
    const size_t count = std::min(strcspn(hostname.get(), "."), max_hostname_bytes);
    const char* end;
    // If the byte limit splits a UTF-8 code point, keep only the complete prefix.
    g_utf8_validate(hostname.get(), count, &end);
    if (end == hostname.get()) {
        return "ClearCast";
    }
    return std::string(prefix) + std::string(hostname.get(), end - hostname.get());
}

const char* receiver_display_name() noexcept {
    try {
        static const std::string name = make_receiver_display_name();
        return name.c_str();
    } catch (const std::bad_alloc&) {
        return "ClearCast";
    }
}

int load_pairing_keys() {
    registered_keys.clear();
    registered_client_metadata.clear();
    const std::unique_ptr<FILE, decltype(&fclose)> file(g_fopen(pairing_register.c_str(), "rb"), &fclose);
    if (!file) {
        return errno == ENOENT ? 0 : ERROR_READ_FAULT;
    }
    if (fseek(file.get(), 0, SEEK_END) != 0) return ERROR_READ_FAULT;
    const long file_bytes = ftell(file.get());
    if (file_bytes < 0 || fseek(file.get(), 0, SEEK_SET) != 0) return ERROR_READ_FAULT;
    if (static_cast<size_t>(file_bytes) > clearcast_pairing_max_bytes) return ERROR_FILE_TOO_LARGE;

    // Bound metadata memory and consume oversized lines without treating their
    // continuation as another device. Cache metadata to avoid duplicate SETUP writes.
    constexpr size_t key_length = 44;
    char prefix[1024];
    size_t length = 0;
    bool truncated = false;
    bool capacity_exceeded = false;
    const auto add_key = [&](bool ended_with_newline) {
        if (ended_with_newline && length && prefix[length - 1] == '\r') {
            --length;
        }
        if (length < key_length || (length > key_length && prefix[key_length] != ',') ||
            prefix[key_length - 1] != '=') {
            return;
        }
        if (std::all_of(prefix, prefix + key_length - 1, [](unsigned char c) {
            return g_ascii_isalnum(c) || c == '+' || c == '/';
        })) {
            const std::string key(prefix, key_length);
            if (registered_client_metadata.find(key) == registered_client_metadata.end() &&
                registered_client_metadata.size() >= clearcast_pairing_max_clients) {
                capacity_exceeded = true;
                return;
            }
            if (registered_client_metadata.emplace(key, "").second) {
                registered_keys.emplace_back(key);
            }
            if (!truncated && length > key_length) {
                const std::string row(prefix, length);
                const size_t second_comma = row.find(',', key_length + 1);
                if (second_comma != std::string::npos) {
                    const std::string metadata = row.substr(second_comma + 1);
                    const size_t name_comma = metadata.find(',');
                    if (name_comma != std::string::npos &&
                        (name_comma + 1 < metadata.size() || registered_client_metadata[key].empty())) {
                        registered_client_metadata[key] = metadata;
                    }
                }
            }
        }
    };

    size_t consumed = 0;
    for (int ch; !capacity_exceeded && (ch = fgetc(file.get())) != EOF;) {
        if (++consumed > clearcast_pairing_max_bytes) { capacity_exceeded = true; break; }
        if (ch == '\n') {
            add_key(true);
            length = 0;
            truncated = false;
        } else if (length < sizeof(prefix)) {
            prefix[length++] = static_cast<char>(ch);
        } else {
            truncated = true;
        }
    }
    if (!capacity_exceeded) add_key(false);
    if (ferror(file.get()) || capacity_exceeded) {
        registered_keys.clear();
        registered_client_metadata.clear();
        return capacity_exceeded ? ERROR_FILE_TOO_LARGE : ERROR_READ_FAULT;
    }
    return 0;
}

void configure_receiver(const ReceiverOptions& options) {
    server_name = receiver_display_name();
    videosink = "d3d11videosink";
    // Mirroring does not send input back to the device. Avoid locking D3D11 on mouse moves.
    videosink_options = " force-aspect-ratio=true enable-navigation-events=false";
    video_decoder = "decodebin name=clearcast_decoder";
    clearcast_video_initialize_decoder(options.decoder);
    h265_support = options.hevc;
    display[0] = options.width;
    display[1] = options.height;
    display[3] = options.fps;
    tcp[0] = udp[0] = 47000;
    tcp[1] = udp[1] = 47001;
    tcp[2] = udp[2] = 47002;
    nofreeze = true;
    setup_legacy_pairing = true;
    hls_support = true;
    pin_pw = 1;
    pin = 1; // Enable PIN pairing. Each control connection generates its own challenge.
    keyfile = options.state_directory + "/receiver.pem";
    pairing_register = options.state_directory + "/clients.txt";
    registration_list = true;
    audio_type = previous_audio_type = compression_type = 0;
    current_volume = initial_volume;
    remote_clock_offset = 0;
    open_connections = 0;
    reset_loop = false;
    relaunch_video = false;
    reset_httpd = false;
    full_video_reset = true;
    close_window = true;
    missed_feedback = 0;
}

bool initialize_video() {
    if (!video_renderer_init(render_logger, server_name.c_str(), videoflip, video_parser.c_str(),
                             rtp_pipeline.c_str(), video_decoder.c_str(), video_converter.c_str(),
                             videosink.c_str(), videosink_options.c_str(), fullscreen, video_sync,
                             h265_support, render_coverart, playbin_version, nullptr)) {
        return false;
    }
    video_renderer_start();
    return true;
}

int run_receiver(const ReceiverOptions& options, HWND& host, HANDLE& host_thread) {
    {
        ExclusiveLock lock(diagnostic_lock);
        diagnostic_path = options.state_directory + "/receiver.log";
        diagnostic_previous_path = options.state_directory + "/receiver.previous.log";
    }
    write_diagnostic("receiver starting");
    int error = clearcast_network_scope_select(options.mdns_address);
    if (error) return error;
    ntp_global_init();
    configure_receiver(options);
    error = load_pairing_keys();
    if (error) {
        write_diagnostic("could not read saved clients; pairing identity unchanged");
        return error;
    }
    // Apple remembers the receiver's public key and skips PIN setup on later visits.
    // With no saved clients, retaining that identity leaves those clients trying
    // pair-verify forever. A fresh identity makes the next visit request a PIN.
    if (registered_keys.empty() && g_file_test(keyfile.c_str(), G_FILE_TEST_EXISTS)) {
        const std::string previous_identity = options.state_directory + "/receiver.unpaired.pem";
        if ((g_file_test(previous_identity.c_str(), G_FILE_TEST_EXISTS) &&
             g_remove(previous_identity.c_str()) != 0) ||
            g_rename(keyfile.c_str(), previous_identity.c_str()) != 0) {
            return ERROR_ACCESS_DENIED;
        }
        write_diagnostic("no saved clients; renewing receiver pairing identity");
    }
    error = initialize_gstreamer(options.state_directory);
    if (error || clearcast_stopping) {
        return error;
    }
    error = clearcast_media_audio_start();
    if (error) return error;
    if (options.decoder == CLEARCAST_DECODER_HARDWARE) {
        if (!clearcast_video_has_hardware_decoder(false)) {
            return ERROR_NOT_SUPPORTED;
        }
        h265_support = h265_support && clearcast_video_has_hardware_decoder(true);
    }

    host = create_winui_video_host(options.parent, &host_thread);
    if (!host) {
        error = GetLastError();
        return error ? error : ERROR_INVALID_WINDOW_HANDLE;
    }
    video_renderer_set_window_handle(reinterpret_cast<uintptr_t>(host));
    if (!clearcast_direct_video_set_window(reinterpret_cast<uintptr_t>(host), options.decoder)) {
        return ERROR_NOT_SUPPORTED;
    }

    render_logger = logger_init();
    if (!render_logger) {
        return ERROR_OUTOFMEMORY;
    }
    logger_set_callback(render_logger, log_callback, nullptr);
    logger_set_level(render_logger, LOGGER_INFO);
    // Pure ALAC music must honor the sender's NTP presentation schedule.
    // Playing decoded packets immediately makes audio lead the sender's lyrics.
    audio_sync = true;
    if (!audio_renderer_init(render_logger, audiosink.c_str(), &audio_sync, &video_sync, audio_rtp_pipeline.c_str())
        || !initialize_video()) {
        return ERROR_NOT_SUPPORTED;
    }
    if (mac_address.empty()) {
        mac_address = find_mac();
        if (mac_address.empty()) {
            mac_address = random_mac();
        }
    }
    std::vector<char> hw_address;
    parse_hw_addr(mac_address, hw_address);

    if (clearcast_stopping) {
        return 0;
    }
    mdnsd_set_ipv4_override(clearcast_network_scope_ipv4());
    if (start_dnssd(hw_address, server_name)) {
        return ERROR_SERVICE_NOT_ACTIVE;
    }
    if (start_raop_server(display, tcp, udp, false)) {
        return WSAEADDRINUSE;
    }
    // The embedded entry bypasses UxPlay's CLI initialization of HLS language preferences.
    raop_set_lang(raop, lang_requested.c_str(), lang_subtitles.c_str(),
        lang_system.empty() ? "en" : lang_system.c_str());
    if (register_dnssd()) {
        return options.mdns_address ? WSAEADDRNOTAVAIL : ERROR_SERVICE_NOT_ACTIVE;
    }

    decoder_commands_ready = true;
    diagnostic_ready();
    enqueue_event({Ready, static_cast<int>(mdnsd_get_active_ipv4()), 0, 0, 0});
    while (!clearcast_stopping && !receiver_failure) {
        main_loop();
        if (clearcast_stopping || receiver_failure) {
            break;
        }
        if (!relaunch_video) {
            return ERROR_GEN_FAILURE;
        }

        // Join the RTSP and RTP threads before replacing their renderer.
        raop_stop_httpd(raop);
        reset_loop = false;
        audio_renderer_stop();
        video_renderer_destroy();
        if (!initialize_video()) {
            return ERROR_NOT_SUPPORTED;
        }
        compression_type = 0;
        unsigned short port = raop_get_port(raop);
        if (raop_start_httpd(raop, &port) < 0) {
            return WSAEADDRINUSE;
        }
        raop_set_port(raop, port);
        diagnostic_ready();
        enqueue_event({Ready, static_cast<int>(mdnsd_get_active_ipv4()), 0, 0, 0});
    }
    return receiver_failure;
}

DWORD WINAPI receiver_worker(void* parameter) {
    const std::unique_ptr<ReceiverOptions> options(static_cast<ReceiverOptions*>(parameter));
    HWND host = nullptr;
    HANDLE host_thread = nullptr;
    int error = ERROR_GEN_FAILURE;
    try {
        error = run_receiver(*options, host, host_thread);
    } catch (const std::bad_alloc&) {
        error = ERROR_OUTOFMEMORY;
    } catch (...) {
        error = ERROR_GEN_FAILURE;
    }
    decoder_commands_ready = false;
    stop_raop_server();
    stop_dnssd();
    clearcast_network_scope_clear();
    clearcast_direct_video_clear();
    clearcast_media_audio_shutdown();
    mdnsd_set_ipv4_override(0);
    set_stats_sink(nullptr);
    audio_renderer_destroy();
    video_renderer_destroy();
    video_renderer_set_window_handle(0);
    if (render_logger) {
        logger_destroy(render_logger);
        render_logger = nullptr;
    }
    if (host_thread) {
        PostMessageW(host, WM_CLOSE, 0, 0);
        WaitForSingleObject(host_thread, INFINITE);
        CloseHandle(host_thread);
    }
    mirror_active = false;
    write_diagnostic("receiver stopped error=%d", error);
    if (error && !clearcast_stopping) {
        enqueue_event({Fatal, 0, 0, 0, error});
    }
    return 0;
}

} // namespace

extern "C" uint64_t clearcast_diagnostic_connected(const char* remote) {
    uint64_t id;
    {
        ExclusiveLock lock(diagnostic_lock);
        id = ++diagnostic_connection_id;
    }
    char address[64];
    diagnostic_token(remote, address, sizeof(address));
    write_diagnostic("connection=%llu opened remote=%s", static_cast<unsigned long long>(id), address);
    return id;
}

extern "C" void clearcast_diagnostic_closed(uint64_t id) {
    write_diagnostic("connection=%llu closed", static_cast<unsigned long long>(id));
}

extern "C" void clearcast_diagnostic_request(uint64_t id, const char* method, const char* path) {
    char safe_method[24], safe_path[160];
    diagnostic_token(method, safe_method, sizeof(safe_method));
    diagnostic_token(path, safe_path, sizeof(safe_path), true);
    write_diagnostic("connection=%llu request method=%s path=%s",
        static_cast<unsigned long long>(id), safe_method, safe_path);
}

extern "C" void clearcast_diagnostic_response(uint64_t id, int status, int generation, int pairing_feedback) {
    write_diagnostic("connection=%llu response status=%d", static_cast<unsigned long long>(id), status);
    if (status >= 400 && !pairing_feedback && !mirror_active) {
        enqueue_event({ConnectionRejected, 0, generation, 0, status});
    }
}

static void clearcast_poll_video_rotation() {
    static constexpr GstVideoOrientationMethod directions[] = {
        GST_VIDEO_ORIENTATION_IDENTITY, GST_VIDEO_ORIENTATION_90R,
        GST_VIDEO_ORIENTATION_180, GST_VIDEO_ORIENTATION_90L
    };
    GstBaseSink* sink;
    GstVideoOrientationMethod direction;
    {
        ExclusiveLock lock(stats_lock);
        if (!stats_sink || requested_rotation == applied_rotation) {
            return;
        }
        sink = GST_BASE_SINK(gst_object_ref(stats_sink));
        applied_rotation = requested_rotation;
        direction = directions[applied_rotation];
    }

    int current_direction;
    g_object_get(sink, "rotate-method", &current_direction, nullptr);
    if (current_direction != direction) {
        // Reapplying the aspect-ratio setting updates the viewport and redraws the cached frame.
        g_object_set(sink, "redraw-on-update", FALSE, "rotate-method", direction,
                     "force-aspect-ratio", TRUE, "redraw-on-update", TRUE, nullptr);
    }
    gst_object_unref(sink);
}

static void clearcast_mirror_running(bool running) {
    if (running) {
        clearcast_direct_video_clear();
        {
            clearcast_media::Lock lock(clearcast_media::state_lock);
            clearcast_media::clear_audio_locked();
        }
        mirror_active = true;
        enqueue_event({Connecting, 0, 0, 0, 0});
    } else if (!direct_video_active && mirror_active.exchange(false)) {
        set_stats_sink(nullptr);
        enqueue_event({MirrorEnded, 0, 0, 0, 0});
        relaunch_video = true;
        reset_loop = true;
    }
}

static void clearcast_mirror_geometry(int width, int height, bool hevc) {
    if (mirror_active && width > 0 && height > 0) {
        enqueue_event({Video, width, height, hevc ? 265 : 264, 0});
    }
}

extern "C" void clearcast_set_video_sink(GstElement* sink) {
    set_stats_sink(sink);
}

extern "C" int clearcast_begin_pairing(const char* pin) {
    // Keep generations positive in the unchanged, signed ReceiverEvent ABI.
    int generation = static_cast<int>(pairing_generation.fetch_add(1) % 0x7fffffffu) + 1;
    if (!mirror_active) {
        write_diagnostic("pairing generation=%d stage=challenge outcome=required", generation);
        enqueue_event({PairingRequired, std::atoi(pin), generation, 0, 0});
    }
    return generation;
}

static void clearcast_display_pairing_pin(const char* pin) {
    clearcast_begin_pairing(pin);
}

extern "C" void clearcast_pairing_result(int status, int generation, int reason) {
    if (!mirror_active) {
        // Only fixed stage names and counters are logged, never PINs or proofs.
        const char* stages[] = {"verification", "setup-data", "setup-proof", "challenge-expired", "cooldown", "verification"};
        const char* stage = reason >= 0 && reason < 6 ? stages[reason] : "unknown";
        write_diagnostic("pairing generation=%d stage=%s outcome=%s", generation, stage,
            status == 2 ? "completed" : status == 0 ? "authentication-required" : "rejected");
        ReceiverEventType type = status == 2 ? PairingCompleted : status == 0 ? PairingAuthenticationRequired : PairingRejected;
        enqueue_event({type, 0, generation, 0, reason});
    }
}

extern "C" void clearcast_decoder_result(int decoder, int error) {
    enqueue_event({error ? DecoderChangeFailed : DecoderChanged, decoder, 0, 0, error});
}

extern "C" void clearcast_pipeline_error() {
    if (mirror_active) {
        receiver_failure = ERROR_NOT_SUPPORTED;
    }
}

extern "C" gboolean clearcast_media_input_cancelled() {
    return clearcast_stopping || reset_loop || receiver_failure;
}

extern "C" __declspec(dllexport) int clearcast_receiver_network_current() {
    return clearcast_network_scope_is_current();
}

extern "C" __declspec(dllexport) const char* clearcast_receiver_get_name() {
    return receiver_display_name();
}

extern "C" __declspec(dllexport) int clearcast_receiver_start_v2(
    const char* state_dir_utf8, const char* size, int fps, int decoder, int hevc,
    const char* pin_text, intptr_t hwnd, uint32_t mdns_address) {
    if (!state_dir_utf8 || !g_path_is_absolute(state_dir_utf8) || !size || !pin_text
        || strlen(pin_text) != 4 || fps < 1 || fps > 120
        || decoder < CLEARCAST_DECODER_AUTOMATIC || decoder > CLEARCAST_DECODER_HARDWARE
        || hevc < 0 || hevc > 1
        || !IsWindow(reinterpret_cast<HWND>(hwnd))) {
        return ERROR_INVALID_PARAMETER;
    }
    unsigned short width = 0, height = 0, pairing_pin = 0;
    if (!parse_dimension(size, width) || *size++ != 'x' || !parse_dimension(size, height)
        || *size || !parse_dimension(pin_text, pairing_pin) || *pin_text) {
        return ERROR_INVALID_PARAMETER;
    }

    ExclusiveLock lock(lifecycle_lock);
    if (worker_thread) {
        if (WaitForSingleObject(worker_thread, 0) != WAIT_OBJECT_0) {
            return ERROR_BUSY;
        }
        CloseHandle(worker_thread);
        worker_thread = nullptr;
    }
    if (g_mkdir_with_parents(state_dir_utf8, 0700) != 0) {
        return ERROR_ACCESS_DENIED;
    }
    try {
        std::unique_ptr<ReceiverOptions> options(new ReceiverOptions{
            state_dir_utf8, width, height, static_cast<unsigned short>(fps),
            decoder, hevc == 1, reinterpret_cast<HWND>(hwnd), mdns_address});
        clear_events();
        clearcast_stopping = false;
        receiver_failure = 0;
        worker_thread = CreateThread(nullptr, 0, receiver_worker, options.get(), 0, nullptr);
        if (!worker_thread) {
            return GetLastError();
        }
        options.release();
        return 0;
    } catch (const std::bad_alloc&) {
        return ERROR_OUTOFMEMORY;
    }
}

extern "C" __declspec(dllexport) int clearcast_receiver_start(
    const char* state_dir_utf8, const char* size, int fps, int decoder, int hevc,
    const char* pin_text, intptr_t hwnd) {
    return clearcast_receiver_start_v2(state_dir_utf8, size, fps, decoder, hevc, pin_text, hwnd, 0);
}

extern "C" __declspec(dllexport) int clearcast_receiver_set_decoder(int decoder) {
    if (decoder < CLEARCAST_DECODER_AUTOMATIC || decoder > CLEARCAST_DECODER_HARDWARE) {
        return ERROR_INVALID_PARAMETER;
    }
    ExclusiveLock lock(lifecycle_lock);
    if (!worker_thread || !decoder_commands_ready || clearcast_stopping ||
        WaitForSingleObject(worker_thread, 0) != WAIT_TIMEOUT) {
        return ERROR_SERVICE_NOT_ACTIVE;
    }
    if (direct_video_active) return clearcast_direct_video_set_decoder(decoder) ? 0 : ERROR_BUSY;
    return clearcast_video_request_decoder(decoder) ? 0 : ERROR_BUSY;
}

extern "C" __declspec(dllexport) int clearcast_receiver_set_video_rotation(int quarter_turns_clockwise) {
    if (quarter_turns_clockwise < 0 || quarter_turns_clockwise > 3) {
        return ERROR_INVALID_PARAMETER;
    }
    ExclusiveLock lock(stats_lock);
    if (!mirror_active || !stats_sink || clearcast_stopping) {
        return ERROR_SERVICE_NOT_ACTIVE;
    }
    requested_rotation = quarter_turns_clockwise;
    return 0;
}

extern "C" __declspec(dllexport) int clearcast_receiver_stop() {
    ExclusiveLock lock(lifecycle_lock);
    if (worker_thread) {
        clearcast_stopping = true;
        for (;;) {
            DWORD result = MsgWaitForMultipleObjectsEx(1, &worker_thread, INFINITE,
                                                       QS_SENDMESSAGE, MWMO_INPUTAVAILABLE);
            if (result == WAIT_OBJECT_0) {
                break;
            }
            if (result != WAIT_OBJECT_0 + 1) {
                return GetLastError();
            }
            // Service synchronous HWND messages if the caller owns the UI thread.
            MSG message;
            PeekMessageW(&message, nullptr, 0, 0, PM_NOREMOVE);
        }
        CloseHandle(worker_thread);
        worker_thread = nullptr;
    }
    clear_events();
    return 0;
}

extern "C" __declspec(dllexport) int clearcast_receiver_read_event(
    int* type, int* width, int* height, int* codec, int* error_code) {
    if (!type || !width || !height || !codec || !error_code) {
        return 0;
    }
    ExclusiveLock lock(event_lock);
    ReceiverEvent event;
    if (!events.pop(event)) {
        return 0;
    }
    *type = event.type;
    *width = event.width;
    *height = event.height;
    *codec = event.codec;
    *error_code = event.error;
    return 1;
}

extern "C" __declspec(dllexport) int clearcast_receiver_read_stats(
    uint64_t* rendered, uint64_t* dropped, int* width, int* height) {
    if (!rendered || !dropped || !width || !height) {
        return 0;
    }
    *rendered = *dropped = 0;
    *width = *height = 0;
    ExclusiveLock lock(stats_lock);
    if (!mirror_active || !stats_sink) {
        return 0;
    }
    GstStructure* stats = gst_base_sink_get_stats(stats_sink);
    if (!stats) {
        return 0;
    }
    guint64 rendered_frames = 0;
    guint64 dropped_frames = 0;
    bool available = gst_structure_get_uint64(stats, "rendered", &rendered_frames)
        && gst_structure_get_uint64(stats, "dropped", &dropped_frames);
    gst_structure_free(stats);
    if (!available) {
        return 0;
    }
    *rendered = rendered_frames;
    *dropped = dropped_frames;
    GstPad* pad = gst_element_get_static_pad(GST_ELEMENT(stats_sink), "sink");
    if (pad) {
        GstCaps* caps = gst_pad_get_current_caps(pad);
        if (caps) {
            if (gst_caps_get_size(caps) > 0) {
                const GstStructure* format = gst_caps_get_structure(caps, 0);
                gst_structure_get_int(format, "width", width);
                gst_structure_get_int(format, "height", height);
            }
            gst_caps_unref(caps);
        }
        gst_object_unref(pad);
    }
    return 1;
}

extern "C" __declspec(dllexport) int clearcast_receiver_read_video_frame(
    unsigned char* pixels, int capacity, uint64_t* generation) {
    if (!pixels || capacity < clearcast_video_frame::bytes || !generation) return 0;
    *generation = 0;
    GstBaseSink* sink;
    uint64_t epoch;
    {
        ExclusiveLock lock(stats_lock);
        if (!mirror_active || !stats_sink || clearcast_stopping) return 0;
        sink = GST_BASE_SINK(gst_object_ref(stats_sink));
        epoch = video_frame_generation;
    }
    GstSample* sample = gst_base_sink_get_last_sample(sink);
    std::array<unsigned char, clearcast_video_frame::bytes> frame;
    int copied = clearcast_video_frame::copy(sample, frame.data(), int(frame.size()));
    if (sample) gst_sample_unref(sample);
    {
        ExclusiveLock lock(stats_lock);
        if (copied && stats_sink == sink && epoch == video_frame_generation && !clearcast_stopping) {
            std::memcpy(pixels, frame.data(), frame.size());
            *generation = epoch;
        } else copied = 0;
    }
    gst_object_unref(sink);
    return copied;
}
