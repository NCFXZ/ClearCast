/* Bounded AirPlay audio presentation and sender-scoped DACP control.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#pragma once
#include <ws2tcpip.h>
#include <cstdint>
#include <climits>
#include <string>
#include <vector>
#include <map>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <array>
#include "clearcast_media_diagnostics.h"
#include "clearcast_network_scope.h"

namespace {
static bool clearcast_direct_video_snapshot(bool&, int64_t&, int64_t&);
static bool clearcast_mirror_media_active();
static bool clearcast_direct_video_controls(uint64_t&, int&, int64_t&, int64_t&, bool&);
static clearcast_diagnostics::Info clearcast_video_diagnostics(bool);
}
extern "C" void clearcast_direct_video_stop();
extern "C" bool clearcast_direct_video_is_active();

namespace clearcast_media {
constexpr size_t max_artwork = 2 * 1024 * 1024;
constexpr size_t max_metadata = 64 * 1024;
struct Lock {
    explicit Lock(SRWLOCK& value) : value(value) { AcquireSRWLockExclusive(&value); }
    ~Lock() { ReleaseSRWLockExclusive(&value); }
    SRWLOCK& value;
};
struct Peer {
    unsigned char bytes[16] = {};
    int length = 0;
    unsigned int zone = 0;
    std::string key() const {
        std::string result(reinterpret_cast<const char*>(bytes), length);
        result.append(reinterpret_cast<const char*>(&zone), sizeof(zone));
        return result;
    }
    bool assign(const unsigned char* value, int size, unsigned int scope) {
        if (!value || (size != 4 && size != 16)) return false;
        if ((size == 4 && (value[0] == 0 || value[0] >= 224)) ||
            (size == 16 && value[0] == 0xff)) return false;
        bool nonzero = false;
        for (int i = 0; i < size; ++i) nonzero |= value[i] != 0;
        if (!nonzero) return false;
        memcpy(bytes, value, size); length = size; zone = scope; return true;
    }
    sockaddr_storage address(unsigned short port) const {
        sockaddr_storage result = {};
        if (length == 4) {
            auto* address = reinterpret_cast<sockaddr_in*>(&result);
            address->sin_family = AF_INET; address->sin_port = htons(port);
            memcpy(&address->sin_addr, bytes, 4);
        } else {
            auto* address = reinterpret_cast<sockaddr_in6*>(&result);
            address->sin6_family = AF_INET6; address->sin6_port = htons(port);
            address->sin6_scope_id = zone; memcpy(&address->sin6_addr, bytes, 16);
        }
        return result;
    }
};
struct Verification { unsigned int connections = 0; uint64_t closed = 0; bool audio = false, video = false; std::string name; };
struct Connection { std::string peer, dacp, remote; };
struct State {
    uint64_t revision = 1, artwork_revision = 1, owner = 0, content_revision = 0;
    bool configured = false, active = false, playing = false;
    uint64_t last_packet = 0, position_tick = 0, sender_state_tick = 0;
    bool sender_playing = false;
    int64_t position = 0, duration = 0;
    std::string title, artist, album, dacp, remote;
    std::vector<unsigned char> artwork;
    Peer peer;
    unsigned short port = 0;
    int controls = 0, denied_controls = 0;
};
static SRWLOCK state_lock = SRWLOCK_INIT;
static State state;
static uint64_t content_counter = 0;
static std::map<std::string, Verification> verified;
static std::map<uint64_t, Connection> connections;
static std::atomic<bool> stopping(true);
static std::atomic_flag command_pending = ATOMIC_FLAG_INIT;
struct CommandAdmission {
    const bool admitted;
    CommandAdmission() : admitted(!command_pending.test_and_set()) {}
    ~CommandAdmission() { if (admitted) command_pending.clear(); }
};
static HANDLE monitor = nullptr;
static SRWLOCK audio_pipeline_lock = SRWLOCK_INIT;
static GstElement* audio_pipeline = nullptr;
static std::atomic<uint64_t> audio_pipeline_generation(1);
struct AudioDiagnostics {
    clearcast_diagnostics::Info info;
    uint64_t generation = 0;
};
inline void set_audio_pipeline(GstElement* pipeline) {
    if (pipeline) gst_object_ref(pipeline);
    GstElement* previous;
    { Lock lock(audio_pipeline_lock); previous = audio_pipeline; audio_pipeline = pipeline; ++audio_pipeline_generation; }
    if (previous) gst_object_unref(previous);
}
inline AudioDiagnostics audio_diagnostics() {
    GstElement* pipeline;
    AudioDiagnostics result;
    { Lock lock(audio_pipeline_lock); pipeline = audio_pipeline ? GST_ELEMENT(gst_object_ref(audio_pipeline)) : nullptr;
        result.generation = audio_pipeline_generation; }
    result.info = clearcast_diagnostics::collect(pipeline, false, true);
    if (pipeline) gst_object_unref(pipeline);
    return result;
}

// The streaming callback never acquires state_lock. The epoch is attached to
// encoded buffers before decode; old queues cannot publish under a new owner.
static SRWLOCK activity_lock = SRWLOCK_INIT;
static std::atomic<uint64_t> activity_generation(1);
static std::atomic<uint64_t> activity_source(0);
static std::atomic<bool> activity_enabled(false);
struct Activity {
    uint64_t owner = 0, tick = 0, epoch = 0;
    double energy = 0, density = 0;
};
static Activity activity;
struct BeatEvent {
    uint64_t sequence = 0, epoch = 0, tick = 0;
    double strength = 0;
};
static SRWLOCK beat_lock = SRWLOCK_INIT;
static std::array<BeatEvent,16> beat_history;
static size_t beat_head = 0, beat_count = 0;
static uint64_t beat_sequence = 0;
inline std::string beat_json(uint64_t epoch, uint64_t now, bool fresh = true) {
    if (!epoch) return ",\"beatEpoch\":0,\"beats\":[]";
    if (!fresh) return ",\"beatEpoch\":" + std::to_string(epoch) + ",\"beats\":[]";
    std::array<BeatEvent,16> retained;
    size_t count = 0;
    AcquireSRWLockShared(&beat_lock);
    for (size_t i = 0; i < beat_count; ++i) {
        const BeatEvent& event = beat_history[(beat_head + i) % beat_history.size()];
        if (event.epoch == epoch && now >= event.tick && now - event.tick <= 900) retained[count++] = event;
    }
    ReleaseSRWLockShared(&beat_lock);
    // Formatting and allocation happen after the fixed-size queue copy.
    std::string json = ",\"beatEpoch\":" + std::to_string(epoch) + ",\"beats\":[";
    for (size_t i = 0; i < count; ++i) {
        char strength[32];
        g_ascii_formatd(strength,sizeof(strength),"%.6f",retained[i].strength);
        if (i) json += ',';
        json += "{\"sequence\":" + std::to_string(retained[i].sequence) + ",\"strength\":" + strength +
            ",\"ageMs\":" + std::to_string(now - retained[i].tick) + "}";
    }
    return json + "]";
}
inline void reset_activity_locked() {
    activity_enabled = false;
    ++activity_generation;
    Lock lock(activity_lock);
    activity = Activity(); activity.owner = state.owner;
    activity_source = state.owner;
    activity_enabled = state.configured && state.active && state.playing;
}
inline void enable_activity_locked() {
    activity_enabled = state.configured && state.active && state.playing;
}
inline std::string activity_json(uint64_t now, bool allowed) {
    Activity sample;
    { Lock lock(activity_lock); sample = activity; }
    uint64_t age = sample.tick && now >= sample.tick ? now - sample.tick : INT_MAX;
    const uint64_t epoch = activity_generation;
    const bool beat_allowed = allowed && activity_enabled && state.owner && activity_source == state.owner;
    bool valid = beat_allowed && sample.owner == state.owner && sample.epoch == epoch && sample.tick && age <= 900;
    std::string beats = beat_json(beat_allowed ? epoch : 0, now, valid);
    if (epoch != activity_generation || !activity_enabled) {
        valid = false;
        beats = beat_json(allowed && activity_enabled && state.owner && activity_source == state.owner ?
            activity_generation.load() : 0, now, false);
    }
    char energy[32], density[32];
    g_ascii_formatd(energy, sizeof(energy), "%.6f", valid ? sample.energy : 0);
    g_ascii_formatd(density, sizeof(density), "%.6f", valid ? sample.density : 0);
    return std::string("{\"valid\":") + (valid ? "true" : "false") +
        ",\"energy\":" + energy + ",\"density\":" + density +
        ",\"ageMs\":" + std::to_string(std::min(age, uint64_t(INT_MAX))) + beats + "}";
}

inline uint32_t be32(const unsigned char* value) {
    return (uint32_t(value[0]) << 24) | (uint32_t(value[1]) << 16) | (uint32_t(value[2]) << 8) | value[3];
}
inline unsigned int be16(const unsigned char* value) { return (unsigned(value[0]) << 8) | value[1]; }
inline int64_t position_at(uint64_t now) {
    int64_t value = state.position + (state.playing ? int64_t(now - state.position_tick) : 0);
    return state.duration > 0 ? std::min(value, state.duration) : value;
}
inline void pause_locked() {
    if (state.playing) { state.position = position_at(GetTickCount64()); state.playing = false; ++state.revision; }
    state.last_packet = 0;
    reset_activity_locked();
}
inline void clear_audio_locked() {
    auto entry = verified.find(state.peer.key());
    if (entry != verified.end()) entry->second.audio = false;
    const uint64_t revision = state.revision + 1, artwork = state.artwork_revision + 1;
    state = State(); state.revision = revision; state.artwork_revision = artwork;
    state.content_revision = ++content_counter;
    set_audio_pipeline(nullptr);
    reset_activity_locked();
}
inline bool peer_verified_locked(const std::string& key) {
    auto item = verified.find(key);
    return item != verified.end() && (item->second.connections || item->second.audio || item->second.video ||
        GetTickCount64() - item->second.closed <= 60000);
}

// Reject a complete malformed record; never publish a partially parsed title.
inline bool metadata_text(const unsigned char* data, size_t size, std::string& result) {
    if (size > 512 || !g_utf8_validate(reinterpret_cast<const char*>(data), size, nullptr)) return false;
    result.clear();
    for (size_t i = 0; i < size; ++i) {
        unsigned char ch = data[i];
        if (ch == 0) return false;
        if (ch >= 32 && ch != 127) result.push_back(static_cast<char>(ch));
    }
    return true;
}
struct Tags { std::string title, artist, album; bool have_title = false, have_artist = false, have_album = false; int count = 0; };
inline bool parse_tags(const unsigned char* bytes, size_t length, Tags& result, unsigned int depth = 0) {
    if (depth > 4) return false;
    size_t offset = 0;
    while (offset < length) {
        if (length - offset < 8 || ++result.count > 256) return false;
        const unsigned char* tag = bytes + offset;
        const size_t size = be32(tag + 4); offset += 8;
        if (size > length - offset) return false;
        const auto* data = bytes + offset;
        if (!memcmp(tag, "mlit", 4) || !memcmp(tag, "mlcl", 4) || !memcmp(tag, "cmst", 4)) {
            if (!parse_tags(data, size, result, depth + 1)) return false;
        } else if (!memcmp(tag, "minm", 4)) {
            if (!metadata_text(data, size, result.title)) return false;
            result.have_title = true;
        } else if (!memcmp(tag, "asar", 4)) {
            if (!metadata_text(data, size, result.artist)) return false;
            result.have_artist = true;
        } else if (!memcmp(tag, "asal", 4)) {
            if (!metadata_text(data, size, result.album)) return false;
            result.have_album = true;
        }
        offset += size;
    }
    return true;
}
inline bool artwork_dimensions(const unsigned char* bytes, size_t size, unsigned int& width, unsigned int& height) {
    width = height = 0;
    static const unsigned char png[] = {137,80,78,71,13,10,26,10};
    if (size >= 33 && !memcmp(bytes, png, sizeof(png)) && be32(bytes + 8) == 13 && !memcmp(bytes + 12, "IHDR", 4)) {
        width = be32(bytes + 16); height = be32(bytes + 20);
    } else if (size >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8) {
        size_t offset = 2;
        while (offset + 4 <= size) {
            if (bytes[offset++] != 0xff) return false;
            while (offset < size && bytes[offset] == 0xff) ++offset;
            if (offset >= size) return false;
            unsigned char marker = bytes[offset++];
            if (marker == 0xd9 || marker == 0xda || marker == 0x00) return false;
            if (marker == 0x01 || (marker >= 0xd0 && marker <= 0xd7)) continue;
            if (size - offset < 2) return false;
            size_t segment = be16(bytes + offset);
            if (segment < 2 || segment > size - offset) return false;
            if ((marker >= 0xc0 && marker <= 0xc3) || (marker >= 0xc5 && marker <= 0xc7) ||
                (marker >= 0xc9 && marker <= 0xcb) || (marker >= 0xcd && marker <= 0xcf)) {
                if (segment < 8) return false;
                height = be16(bytes + offset + 3); width = be16(bytes + offset + 5); break;
            }
            offset += segment;
        }
    }
    return width && height && width <= 4096 && height <= 4096 && uint64_t(width) * height <= 16000000;
}
inline bool decimal(const char*& text, const char* end, uint32_t& result) {
    if (text == end || *text < '0' || *text > '9') return false;
    uint64_t value = 0;
    while (text != end && *text >= '0' && *text <= '9') {
        value = value * 10 + unsigned(*text++ - '0');
        if (value > UINT32_MAX) return false;
    }
    result = static_cast<uint32_t>(value); return true;
}
inline bool credentials(const char* dacp, const char* remote) {
    if (!dacp || !remote || !*dacp || !*remote) return false;
    size_t id = strnlen(dacp, 17), token = strnlen(remote, 11);
    if (id > 16 || token > 10) return false;
    for (size_t i = 0; i < id; ++i) if (!g_ascii_isxdigit(dacp[i])) return false;
    uint32_t value = 0; const char* cursor = remote;
    return decimal(cursor, remote + token, value) && cursor == remote + token && value;
}

inline bool dns_name(const unsigned char* packet, size_t size, size_t& offset, std::string& name) {
    name.clear(); size_t cursor = offset, resume = 0; unsigned int hops = 0;
    for (;;) {
        if (cursor >= size || ++hops > 64) return false;
        unsigned int length = packet[cursor++];
        if (!length) { offset = resume ? resume : cursor; return true; }
        if ((length & 0xc0) == 0xc0) {
            if (cursor >= size) return false;
            size_t target = ((length & 0x3f) << 8) | packet[cursor++];
            if (target >= cursor - 2 || target >= size) return false;
            if (!resume) resume = cursor;
            cursor = target;
            continue;
        }
        if (length > 63 || length > size - cursor || name.size() + length + 1 > 255) return false;
        if (!name.empty()) name.push_back('.');
        for (unsigned int i = 0; i < length; ++i) {
            unsigned char ch = packet[cursor++];
            if (ch < 33 || ch > 126 || ch == '.') return false;
            name.push_back(static_cast<char>(g_ascii_tolower(ch)));
        }
    }
}
inline unsigned short dns_port(const unsigned char* packet, size_t size, const std::string& service) {
    if (size < 12 || !(packet[2] & 0x80) || (packet[3] & 0x0f)) return 0;
    size_t offset = 12; std::string name;
    const unsigned int questions = be16(packet + 4);
    const unsigned int records = be16(packet + 6) + be16(packet + 8) + be16(packet + 10);
    if (questions > 16 || records > 128) return 0;
    for (unsigned int i = 0; i < questions; ++i) {
        if (!dns_name(packet, size, offset, name) || size - offset < 4) return 0;
        offset += 4;
    }
    for (unsigned int i = 0; i < records; ++i) {
        if (!dns_name(packet, size, offset, name) || size - offset < 10) return 0;
        unsigned int type = be16(packet + offset), klass = be16(packet + offset + 2) & 0x7fff;
        size_t length = be16(packet + offset + 8); offset += 10;
        if (length > size - offset) return 0;
        if (type == 33 && klass == 1 && name == service && length >= 7) {
            size_t target = offset + 6; std::string host;
            if (!dns_name(packet, size, target, host) || target > offset + length || host.empty()) return 0;
            unsigned int port = be16(packet + offset + 4);
            if (port >= 1024) return static_cast<unsigned short>(port);
        }
        offset += length;
    }
    return 0;
}
struct Socket { SOCKET value = INVALID_SOCKET; ~Socket() { if (value != INVALID_SOCKET) closesocket(value); } };
inline bool socket_ready(SOCKET socket, bool write, uint64_t deadline) {
    const uint64_t now = GetTickCount64();
    if (now >= deadline || stopping) return false;
    fd_set values; FD_ZERO(&values); FD_SET(socket, &values);
    const uint64_t remaining = deadline - now;
    timeval timeout = {long(remaining / 1000), long(remaining % 1000) * 1000};
    return select(0, write ? nullptr : &values, write ? &values : nullptr, nullptr, &timeout) > 0;
}
inline unsigned short discover(const Peer& peer, const std::string& dacp) {
    std::string service = "itunes_ctrl_" + dacp + "._dacp._tcp.local";
    std::transform(service.begin(), service.end(), service.begin(), [](char ch) { return char(g_ascii_tolower(ch)); });
    std::vector<unsigned char> query(12, 0); query[5] = 1;
    size_t offset = 0;
    while (offset < service.size()) {
        size_t end = service.find('.', offset); if (end == std::string::npos) end = service.size();
        query.push_back(static_cast<unsigned char>(end - offset));
        query.insert(query.end(), service.begin() + offset, service.begin() + end); offset = end + 1;
    }
    query.insert(query.end(), {0,0,33,0x80,1});
    Socket socket; socket.value = ::socket(peer.length == 4 ? AF_INET : AF_INET6, SOCK_DGRAM, IPPROTO_UDP);
    if (socket.value == INVALID_SOCKET) return 0;
    // DACP discovery follows the receiver's adapter, including global IPv6
    // peers whose address carries no zone. Never use a default-route fallback.
    unsigned int selected_scope = 0;
    if (peer.length == 4) {
        in_addr selected = {}; selected.s_addr = clearcast_network_scope_ipv4();
        if (!selected.s_addr || setsockopt(socket.value, IPPROTO_IP, IP_MULTICAST_IF,
            reinterpret_cast<const char*>(&selected), sizeof(selected)) == SOCKET_ERROR) return 0;
    } else {
        unsigned char selected[16];
        if (!clearcast_network_scope_ipv6(selected, &selected_scope) || !selected_scope ||
            (peer.zone && peer.zone != selected_scope) ||
            setsockopt(socket.value, IPPROTO_IPV6, IPV6_MULTICAST_IF,
                reinterpret_cast<const char*>(&selected_scope), sizeof(selected_scope)) == SOCKET_ERROR) return 0;
    }
    if (clearcast_network_scope_prepare_datagram(static_cast<int>(socket.value), peer.length == 16) == -1) return 0;
    sockaddr_storage local = {}; local.ss_family = peer.length == 4 ? AF_INET : AF_INET6;
    const int address_size = peer.length == 4 ? sizeof(sockaddr_in) : sizeof(sockaddr_in6);
    if (bind(socket.value, reinterpret_cast<sockaddr*>(&local), address_size) == SOCKET_ERROR) return 0;
    auto destination = peer.address(5353);
    sendto(socket.value, reinterpret_cast<const char*>(query.data()), int(query.size()), 0,
        reinterpret_cast<sockaddr*>(&destination), address_size);
    if (peer.length == 4) {
        sockaddr_in multicast = {}; multicast.sin_family = AF_INET; multicast.sin_port = htons(5353);
        multicast.sin_addr.s_addr = htonl(0xe00000fb);
        sendto(socket.value, reinterpret_cast<const char*>(query.data()), int(query.size()), 0,
            reinterpret_cast<sockaddr*>(&multicast), sizeof(multicast));
    } else {
        sockaddr_in6 multicast = {}; multicast.sin6_family = AF_INET6; multicast.sin6_port = htons(5353);
        multicast.sin6_scope_id = selected_scope; inet_pton(AF_INET6, "ff02::fb", &multicast.sin6_addr);
        sendto(socket.value, reinterpret_cast<const char*>(query.data()), int(query.size()), 0,
            reinterpret_cast<sockaddr*>(&multicast), sizeof(multicast));
    }
    const uint64_t deadline = GetTickCount64() + 1500;
    while (socket_ready(socket.value, false, deadline)) {
        unsigned char packet[4096]; sockaddr_storage source = {}; int source_size = sizeof(source);
        int count = clearcast_network_scope_recvfrom(static_cast<int>(socket.value), reinterpret_cast<char*>(packet), sizeof(packet),
            reinterpret_cast<sockaddr*>(&source), &source_size, peer.length == 16);
        if (count <= 0 || source.ss_family != local.ss_family) continue;
        if (peer.length == 4) {
            auto* address = reinterpret_cast<sockaddr_in*>(&source);
            if (address->sin_port != htons(5353) || memcmp(&address->sin_addr, peer.bytes, 4)) continue;
        } else {
            auto* address = reinterpret_cast<sockaddr_in6*>(&source);
            if (address->sin6_port != htons(5353) || memcmp(&address->sin6_addr, peer.bytes, 16) ||
                (peer.zone && peer.zone != address->sin6_scope_id)) continue;
        }
        unsigned short port = dns_port(packet, count, service); if (port) return port;
    }
    return 0;
}
inline bool player_state_tags(const unsigned char* data, size_t length, int& count, int& state_value, unsigned int depth) {
    if (depth > 4) return false;
    size_t offset = 0;
    while (offset < length) {
        if (length - offset < 8 || ++count > 256) return false;
        const auto* tag = data + offset; size_t size = be32(tag + 4); offset += 8;
        if (size > length - offset) return false;
        if (!memcmp(tag,"caps",4)) {
            if (size != 1 && size != 4) return false;
            int value = size == 1 ? data[offset] : int(be32(data + offset));
            if (value < 0 || value > 6) return false;
            state_value = value;
        } else if (!memcmp(tag,"cmgt",4) || !memcmp(tag,"cmst",4) || !memcmp(tag,"mlit",4) || !memcmp(tag,"mlcl",4)) {
            if(!player_state_tags(data+offset,size,count,state_value,depth+1))return false;
        }
        offset += size;
    }
    return true;
}
inline int player_state(const unsigned char* data,size_t length,int& count) {
    int value=-1;
    return player_state_tags(data,length,count,value,0)?value:-1;
}
inline void sender_state_locked(int value) {
    if (value < 0 || value > 6) return;
    state.sender_state_tick = GetTickCount64();
    state.sender_playing = value >= 4;
    if (state.active && state.playing != state.sender_playing) {
        state.position = position_at(state.sender_state_tick); state.position_tick = state.sender_state_tick;
        state.playing = state.sender_playing; ++state.revision;
        if (state.playing) enable_activity_locked(); else reset_activity_locked();
    }
}
inline bool playback_time_tags(const unsigned char* data, size_t length, int64_t& remaining, int64_t& duration, unsigned depth = 0) {
    if (depth > 4) return false;
    for (size_t offset = 0; offset < length;) {
        if (length - offset < 8) return false;
        const unsigned char* tag = data + offset;
        size_t size = be32(tag + 4); offset += 8;
        if (size > length - offset) return false;
        if (!memcmp(tag, "cmgt", 4) || !memcmp(tag, "cmst", 4)) {
            if (!playback_time_tags(data + offset, size, remaining, duration, depth + 1)) return false;
        } else if (!memcmp(tag, "cant", 4) || !memcmp(tag, "cast", 4)) {
            if (size != 4) return false;
            int64_t& value = !memcmp(tag, "cant", 4) ? remaining : duration;
            if (value != -1) return false;
            value = be32(data + offset);
        }
        offset += size;
    }
    return true;
}
inline int request(const Peer& peer, unsigned short port, const std::string& token, const char* command, uint64_t owner = 0, int* playback_state = nullptr, bool* dmap_supported = nullptr, int64_t* position = nullptr, int64_t* duration = nullptr, uint64_t content = 0) {
    if (playback_state) *playback_state = -1;
    if (dmap_supported) *dmap_supported = false;
    Socket socket; socket.value = ::socket(peer.length == 4 ? AF_INET : AF_INET6, SOCK_STREAM, IPPROTO_TCP);
    if (socket.value == INVALID_SOCKET) return WSAGetLastError();
    if (clearcast_network_scope_prepare_unicast(static_cast<int>(socket.value), peer.length == 16) == -1) return WSAGetLastError();
    u_long nonblocking = 1;
    if (ioctlsocket(socket.value, FIONBIO, &nonblocking) == SOCKET_ERROR) return WSAGetLastError();
    auto address = peer.address(port); const uint64_t deadline = GetTickCount64() + 1200;
    int result = connect(socket.value, reinterpret_cast<sockaddr*>(&address),
        peer.length == 4 ? sizeof(sockaddr_in) : sizeof(sockaddr_in6));
    if (result == SOCKET_ERROR) {
        if (WSAGetLastError() != WSAEWOULDBLOCK || !socket_ready(socket.value, true, deadline)) return WSAETIMEDOUT;
        int error = 0, length = sizeof(error);
        if (getsockopt(socket.value, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&error), &length) || error) return error ? error : WSAECONNREFUSED;
    }
    if (owner) {
        Lock lock(state_lock);
        if (state.owner != owner || !state.active || stopping || (content && state.content_revision != content)) return ERROR_CANCELLED;
    }
    char host[INET6_ADDRSTRLEN] = {};
    inet_ntop(peer.length == 4 ? AF_INET : AF_INET6, peer.bytes, host, sizeof(host));
    const std::string host_header = peer.length == 16 ? "[" + std::string(host) + "]" : host;
    const std::string text = "GET /ctrl-int/1/" + std::string(command) + " HTTP/1.1\r\nHost: " + host_header + ":" +
        std::to_string(port) + "\r\nActive-Remote: " + token + "\r\nConnection: close\r\n\r\n";
    size_t sent = 0;
    while (sent < text.size()) {
        if (!socket_ready(socket.value, true, deadline)) return WSAETIMEDOUT;
        int count;
        if (owner) {
            // The socket is nonblocking. Validate and send atomically with
            // respect to replacement/stop, without holding this lock in select.
            Lock lock(state_lock);
            if (state.owner != owner || !state.active || stopping || (content && state.content_revision != content)) return ERROR_CANCELLED;
            count = send(socket.value, text.data() + sent, int(text.size() - sent), 0);
        } else count = send(socket.value, text.data() + sent, int(text.size() - sent), 0);
        if (count <= 0) return WSAECONNRESET;
        sent += count;
    }
    std::string headers;
    while (headers.find("\r\n\r\n") == std::string::npos) {
        if (!socket_ready(socket.value, false, deadline)) return WSAETIMEDOUT;
        char buffer[1024]; int count = recv(socket.value, buffer, sizeof(buffer), 0);
        if (count <= 0 || headers.size() + size_t(count) > 4096) return ERROR_INVALID_DATA;
        headers.append(buffer, count);
    }
    if (headers.size() < 12 || (headers.compare(0, 9, "HTTP/1.1 ") && headers.compare(0, 9, "HTTP/1.0 ")) ||
        headers[9] < '0' || headers[9] > '9' || headers[10] < '0' || headers[10] > '9' || headers[11] < '0' || headers[11] > '9') return ERROR_INVALID_DATA;
    const int status = (headers[9] - '0') * 100 + (headers[10] - '0') * 10 + headers[11] - '0';
    if (status < 200 || status >= 300) return status == 401 || status == 403 ? ERROR_ACCESS_DENIED : ERROR_NOT_SUPPORTED;
    if (!playback_state && !dmap_supported && !position && !duration) return 0;
    const size_t header_end = headers.find("\r\n\r\n");
    int content_length = -1;
    size_t line = headers.find("\r\n") + 2;
    while (line < header_end) {
        size_t end = headers.find("\r\n",line);
        if (end == std::string::npos || end > header_end) return ERROR_INVALID_DATA;
        if (!g_ascii_strncasecmp(headers.data()+line,"Transfer-Encoding:",18)) return ERROR_NOT_SUPPORTED;
        if (!g_ascii_strncasecmp(headers.data()+line,"Content-Length:",15)) {
            const char* cursor=headers.data()+line+15;const char* finish=headers.data()+end;
            while(cursor!=finish&&(*cursor==' '||*cursor=='\t'))++cursor;
            uint32_t value=0;
            if(!decimal(cursor,finish,value)||value>65536)return ERROR_INVALID_DATA;
            while(cursor!=finish&&(*cursor==' '||*cursor=='\t'))++cursor;
            if(cursor!=finish||content_length>=0)return ERROR_INVALID_DATA;
            content_length=int(value);
        }
        line=end+2;
    }
    if(content_length<0)return 0; // A sender without a bounded status body uses observed RTP/FLUSH state.
    std::string body=headers.substr(header_end+4);
    while(body.size()<size_t(content_length)) {
        if(!socket_ready(socket.value,false,deadline))return WSAETIMEDOUT;
        char bytes[1024];int count=recv(socket.value,bytes,int(std::min<size_t>(sizeof(bytes),size_t(content_length)-body.size())),0);
        if(count<=0)return ERROR_INVALID_DATA;
        body.append(bytes,count);
    }
    if(body.size()!=size_t(content_length))return ERROR_INVALID_DATA;
    int tags=0,value=-1;
    const auto* bytes=reinterpret_cast<const unsigned char*>(body.data());
    bool valid=body.size()>=8&&!memcmp(bytes,"cmgt",4)&&be32(bytes+4)==body.size()-8&&
        player_state_tags(bytes,body.size(),tags,value,0);
    if(dmap_supported)*dmap_supported=valid;
    if(playback_state&&valid)*playback_state=value;
    if (position && duration && valid) {
        int64_t remaining = -1, total = -1;
        if (playback_time_tags(bytes, body.size(), remaining, total) && total > 0 &&
            total <= 7LL * 86400000 && remaining >= 0 && remaining <= total) {
            *position = total - remaining; *duration = total;
        }
    }
    return 0;
}
inline DWORD WINAPI monitor_worker(void*) try {
    uint64_t next_probe = 0;
    while (!stopping) {
        Peer peer; std::string dacp, token; uint64_t owner = 0, content = 0; unsigned short port=0;
        {
            Lock lock(state_lock);
            if (state.active && state.configured && !state.dacp.empty() &&
                GetTickCount64() >= next_probe && peer_verified_locked(state.peer.key())) {
                peer = state.peer; dacp = state.dacp; token = state.remote; owner = state.owner; content = state.content_revision; port=state.port;
            }
        }
        if (owner) {
            if(!port)port=discover(peer,dacp);
            int playback_state=-1;bool supported=false;
            int result=port?request(peer,port,token,"getproperty?properties=dacp.playerstate",owner,&playback_state,&supported):ERROR_NOT_FOUND;
            if(result==ERROR_NOT_SUPPORTED||(!result&&!supported))result=request(peer,port,token,"getproperty?properties=dmcp.volume",owner,nullptr,&supported);
            int64_t seek_position = -1, seek_duration = -1;
            if (!result && supported)
                request(peer, port, token, "getproperty?properties=dacp.playingtime", owner, nullptr, nullptr, &seek_position, &seek_duration, content);
            Lock lock(state_lock);
            if (!stopping && state.owner == owner && state.content_revision == content && state.dacp == dacp && state.remote == token && !result && supported) {
                state.port = port;
                if (seek_position >= 0) {
                    state.position = seek_position; state.duration = seek_duration; state.position_tick = GetTickCount64();
                }
                int controls = (7 | (seek_position >= 0 ? 8 : 0)) & ~state.denied_controls;
                if(state.controls!=controls){state.controls=controls;++state.revision;}
                sender_state_locked(playback_state);
            }
            next_probe = GetTickCount64() + (result?5000:2000);
        }
        Sleep(100);
    }
    return 0;
} catch (...) {
    return ERROR_OUTOFMEMORY;
}
inline std::string escaped(const std::string& text) {
    std::string result;
    for (unsigned char ch : text) {
        if (ch == '"' || ch == '\\') result.push_back('\\');
        if (ch >= 32) result.push_back(static_cast<char>(ch));
    }
    return result;
}
} // namespace clearcast_media

extern "C" void clearcast_media_verified(uint64_t id, const unsigned char* peer, int length, unsigned int zone) try {
    using namespace clearcast_media; Peer address; if (!id || !address.assign(peer, length, zone)) return;
    Lock lock(state_lock); if (connections.count(id)) return;
    for (auto it = verified.begin(); it != verified.end();) {
        if (!peer_verified_locked(it->first)) it = verified.erase(it); else ++it;
    }
    const std::string key = address.key();
    if (connections.size() >= 64 || (!verified.count(key) && verified.size() >= 16)) return;
    auto entry = verified.emplace(key, Verification()).first;
    connections.emplace(id, Connection{key, "", ""}); ++entry->second.connections;
} catch (...) {}
extern "C" void clearcast_media_client_name(uint64_t id, const char* name) try {
    using namespace clearcast_media;
    if (!name) return;
    size_t size = strnlen(name, 513);
    if (!size || size > 512 || !g_utf8_validate(name, size, nullptr)) return;
    for (const char* cursor = name; *cursor; cursor = g_utf8_next_char(cursor))
        if (g_unichar_iscntrl(g_utf8_get_char(cursor))) return;
    Lock lock(state_lock);
    auto connection = connections.find(id);
    if (connection == connections.end()) return;
    auto peer = verified.find(connection->second.peer);
    if (peer != verified.end() && peer->second.name != name) { peer->second.name = name; ++state.revision; }
} catch (...) {}
extern "C" void clearcast_media_remote(uint64_t id, const char* dacp, const char* remote) try {
    using namespace clearcast_media; if (!credentials(dacp, remote)) return;
    Lock lock(state_lock); auto entry = connections.find(id);
    if (entry == connections.end()) return;
    entry->second.dacp = dacp; entry->second.remote = remote;
    if (state.owner == id && state.configured && (state.dacp != dacp || state.remote != remote)) {
        state.dacp = dacp; state.remote = remote; state.port = 0; state.controls = state.denied_controls = 0; ++state.revision;
    }
} catch (...) {}
extern "C" void clearcast_media_retain_peer(const unsigned char* peer, int length, unsigned int zone, bool active) try {
    using namespace clearcast_media; Peer address; if (!address.assign(peer, length, zone)) return;
    Lock lock(state_lock); auto entry = verified.find(address.key());
    if (entry != verified.end()) { entry->second.video = active; if (!active) entry->second.closed = GetTickCount64(); }
} catch (...) {}
extern "C" void clearcast_media_closed(uint64_t id) try {
    using namespace clearcast_media; Lock lock(state_lock); auto entry = connections.find(id);
    if (entry != connections.end()) {
        auto peer = verified.find(entry->second.peer);
        if (peer != verified.end()) { if (peer->second.connections) --peer->second.connections; peer->second.closed = GetTickCount64(); }
        connections.erase(entry);
    }
    if (state.owner == id) clear_audio_locked();
} catch (...) {}
extern "C" void clearcast_media_audio_setup(uint64_t id, const unsigned char* peer, int length, unsigned int zone,
    int using_screen, const char* dacp, const char* remote) try {
    using namespace clearcast_media; Peer address; if (!id || !address.assign(peer, length, zone)) return;
    // SETUP prepares a transport, including auxiliary audio recreated on lock.
    // A direct-video owner is released by its own stop/end, not this transport.
#ifdef CLEARCAST_IN_PROCESS
    write_diagnostic("audio setup usingScreen=%d directVideo=%d", using_screen,
        int(clearcast_direct_video_is_active()));
#endif
    Lock lock(state_lock);
    auto authenticated = connections.find(id);
    if (authenticated == connections.end() || authenticated->second.peer != address.key()) return;
    if (using_screen) { clear_audio_locked(); return; }
    if (state.owner != id || !state.configured) clear_audio_locked();
    state.owner = id; state.peer = address; state.configured = true; verified[address.key()].audio = true;
    // clear_audio_locked assigned owner0 before this fresh session was known.
    {
        Lock activity_guard(activity_lock);
        activity.owner = id;
        activity_source = id;
    }
    auto cached = connections.find(id);
    if (!credentials(dacp, remote) && cached != connections.end()) {
        dacp = cached->second.dacp.c_str(); remote = cached->second.remote.c_str();
    }
    if (credentials(dacp, remote) && (state.dacp != dacp || state.remote != remote)) {
        state.dacp = dacp; state.remote = remote; state.controls = state.denied_controls = 0; state.port = 0; ++state.revision;
    }
} catch (...) {}
static void clearcast_media_audio_packet(uint64_t source_id) {
    using namespace clearcast_media; Lock lock(state_lock); if (!state.configured || state.owner != source_id) return;
    const uint64_t now = GetTickCount64();
    const bool playing=state.sender_state_tick&&now-state.sender_state_tick<5000?state.sender_playing:true;
    if (!state.active || state.playing!=playing) {
        state.active = true;state.playing = playing; state.position_tick = now; ++state.revision;
    }
    state.last_packet = now;
    enable_activity_locked();
}
extern "C" uint64_t clearcast_media_audio_activity_epoch() {
    return clearcast_media::activity_enabled ? clearcast_media::activity_generation.load() : 0;
}
extern "C" void clearcast_media_audio_pipeline(GstElement* pipeline, uint64_t epoch) {
    using namespace clearcast_media;
    Lock lock(state_lock);
    if (pipeline && (!epoch || !activity_enabled || epoch != activity_generation)) return;
    { Lock pipeline_guard(audio_pipeline_lock); if (audio_pipeline == pipeline) return; }
    set_audio_pipeline(pipeline);
}
extern "C" uint64_t clearcast_media_audio_activity_epoch_for_source(uint64_t source_id) {
    using namespace clearcast_media;
    if (!source_id || !activity_enabled || activity_source != source_id) return 0;
    const uint64_t epoch = activity_generation;
    return activity_enabled && activity_source == source_id && epoch == activity_generation ? epoch : 0;
}
extern "C" void clearcast_media_audio_activity_boundary() {
    using namespace clearcast_media;
    ++activity_generation;
    Lock lock(activity_lock);
    activity.tick = 0; activity.energy = activity.density = 0;
}
extern "C" void clearcast_media_audio_activity_publish(uint64_t epoch, double energy, double density) {
    using namespace clearcast_media;
    if (!epoch || !activity_enabled || epoch != activity_generation || !std::isfinite(energy) || !std::isfinite(density)) return;
    AcquireSRWLockExclusive(&activity_lock);
    if (activity_enabled && epoch == activity_generation && activity.owner) {
        activity.energy = std::max(0.0, std::min(1.0, energy));
        activity.density = std::max(0.0, std::min(1.0, density));
        activity.tick = GetTickCount64(); activity.epoch = epoch;
    }
    ReleaseSRWLockExclusive(&activity_lock);
}
extern "C" void clearcast_media_audio_activity_beat(uint64_t epoch, double strength, uint64_t sample_age_ms) {
    using namespace clearcast_media;
    if (!epoch || !activity_enabled || epoch != activity_generation || !std::isfinite(strength) || strength <= 0 || sample_age_ms > 900) return;
    // Dedicated short lock: no PCM processing, allocation, JSON, I/O or state
    // transitions occur here. Events do not contend with aggregate TryLock.
    AcquireSRWLockExclusive(&beat_lock);
    if (activity_enabled && epoch == activity_generation && beat_sequence != UINT64_MAX) {
        const uint64_t now = GetTickCount64();
        BeatEvent event;
        event.sequence = ++beat_sequence; event.epoch = epoch;
        event.tick = now >= sample_age_ms ? now - sample_age_ms : 0;
        event.strength = std::min(1.0,strength);
        const size_t index = (beat_head + beat_count) % beat_history.size();
        beat_history[index] = event;
        if (beat_count < beat_history.size()) ++beat_count;
        else beat_head = (beat_head + 1) % beat_history.size();
    }
    ReleaseSRWLockExclusive(&beat_lock);
}
extern "C" void clearcast_media_audio_activity_invalidate(uint64_t epoch) {
    using namespace clearcast_media;
    if (!epoch || !activity_generation.compare_exchange_strong(epoch, epoch + 1)) return;
    if (!TryAcquireSRWLockExclusive(&activity_lock)) return;
    if (epoch + 1 == activity_generation) {
        activity.tick = 0; activity.energy = activity.density = 0;
    }
    ReleaseSRWLockExclusive(&activity_lock);
}
extern "C" void clearcast_media_audio_stop(uint64_t id, int ended) try {
    using namespace clearcast_media; Lock lock(state_lock); if (state.owner != id) return;
    if (ended) clear_audio_locked(); else pause_locked();
} catch (...) {}
extern "C" void clearcast_media_audio_parameter(uint64_t id, const char* type, const void* buffer, int length) try {
    using namespace clearcast_media;
    if (!type || length < 0 || (length && !buffer)) return;
    const auto* bytes = static_cast<const unsigned char*>(buffer);
    if (!strcmp(type, "application/x-dmap-tagged")) {
        if (!length || size_t(length) > max_metadata) return;
        Tags tags; if (!parse_tags(bytes, length, tags)) return;
        Lock lock(state_lock); if (!state.configured || state.owner != id) return;
        if ((tags.have_title && state.title != tags.title) || (tags.have_artist && state.artist != tags.artist) ||
            (tags.have_album && state.album != tags.album)) {
            state.content_revision = ++content_counter;
            state.denied_controls &= ~8;
            state.controls &= ~8;
        }
        if (tags.have_title && state.title != tags.title) {
            if (!state.title.empty()) { state.artwork.clear(); ++state.artwork_revision; }
            state.title = tags.title;
            state.position = state.duration = 0; state.position_tick = GetTickCount64();
            reset_activity_locked();
        }
        if (tags.have_artist) state.artist = tags.artist;
        if (tags.have_album) state.album = tags.album;
        ++state.revision;
    } else if (!strcmp(type, "image/jpeg") || !strcmp(type, "image/png")) {
        unsigned int width, height;
        if (size_t(length) > max_artwork || (length && !artwork_dimensions(bytes, length, width, height))) return;
        std::vector<unsigned char> artwork;
        if (length) artwork.assign(bytes, bytes + length);
        Lock lock(state_lock); if (!state.configured || state.owner != id) return;
        state.artwork.swap(artwork); ++state.artwork_revision; ++state.revision;
    } else if (!strcmp(type, "text/parameters") && length <= 128 && length >= 10 && !memcmp(bytes, "progress: ", 10)) {
        const char* cursor = reinterpret_cast<const char*>(bytes) + 10;
        const char* end = reinterpret_cast<const char*>(bytes) + length; uint32_t start, current, finish;
        if (!decimal(cursor, end, start) || cursor == end || *cursor++ != '/' || !decimal(cursor, end, current) ||
            cursor == end || *cursor++ != '/' || !decimal(cursor, end, finish)) return;
        while (cursor != end && (*cursor == '\r' || *cursor == '\n')) ++cursor;
        uint32_t duration = finish - start, position = current - start;
        if (cursor != end || duration > 44100u * 60 * 60 * 12 || position > duration) return;
        Lock lock(state_lock); if (!state.configured || state.owner != id) return;
        const int64_t next_position = int64_t(position) * 1000 / 44100;
        if (state.duration && std::llabs(next_position - position_at(GetTickCount64())) > 2000) reset_activity_locked();
        state.duration = int64_t(duration) * 1000 / 44100; state.position = int64_t(position) * 1000 / 44100;
        state.position_tick = GetTickCount64(); ++state.revision;
    }
} catch (...) {}
static inline int clearcast_media_audio_start() {
    using namespace clearcast_media; Lock lock(state_lock);
    clear_audio_locked(); verified.clear(); connections.clear(); stopping = false;
    monitor = CreateThread(nullptr, 0, monitor_worker, nullptr, 0, nullptr);
    return monitor ? 0 : int(GetLastError());
}
static inline void clearcast_media_audio_shutdown() {
    using namespace clearcast_media; stopping = true;
    if (monitor) { WaitForSingleObject(monitor, INFINITE); CloseHandle(monitor); monitor = nullptr; }
    Lock lock(state_lock); clear_audio_locked(); verified.clear(); connections.clear();
}
extern "C" __declspec(dllexport) int clearcast_receiver_read_media(char* buffer, int capacity) try {
    using namespace clearcast_media; bool video_playing = false; int64_t video_position = 0, video_duration = 0;
    const bool direct_video = clearcast_direct_video_snapshot(video_playing, video_position, video_duration);
    uint64_t video_session = 0; int video_controls = 0; int64_t seek_start = 0, seek_end = 0;
    bool video_live = false;
    if (direct_video) clearcast_direct_video_controls(video_session, video_controls, seek_start, seek_end, video_live);
    const bool mirror = clearcast_mirror_media_active();
    const bool video = direct_video || mirror;
    auto diagnostics = video ? clearcast_video_diagnostics(direct_video) : clearcast_diagnostics::Info();
    // No GStreamer graph traversal occurs under the media state lock. A session
    // clear or renderer replacement revokes the sample before publication.
    auto audio = !direct_video ? audio_diagnostics() : AudioDiagnostics();
    if(mirror&&!direct_video){video_playing=true;video_position=video_duration=0;}
    Lock lock(state_lock);
    if (state.playing && state.last_packet && GetTickCount64() - state.last_packet > 2500 &&
        (!state.sender_state_tick||GetTickCount64()-state.sender_state_tick>5000)) pause_locked();
    const char* kind = video ? "video" : state.active ? "audio" : "none";
    if (audio.generation == audio_pipeline_generation) {
        if (!video && state.active) diagnostics = audio.info;
        if (mirror) {
            diagnostics.audio_codec = audio.info.audio_codec; diagnostics.sample_rate = audio.info.sample_rate;
            diagnostics.channels = audio.info.channels; diagnostics.audio_bitrate = audio.info.audio_bitrate;
        }
    }
    const bool playing = video ? video_playing : state.playing;
    const bool connected = !stopping && (!connections.empty() || state.configured || video);
    std::string device_name;
    if (connected) for (auto connection = connections.rbegin(); connection != connections.rend(); ++connection) {
        auto peer = verified.find(connection->second.peer);
        if (peer != verified.end() && !peer->second.name.empty()) { device_name = peer->second.name; break; }
    }
    const std::string json = "{\"revision\":" + std::to_string(state.revision) + ",\"kind\":\"" + kind +
        "\",\"connected\":" + (connected ? "true" : "false") + ",\"deviceName\":\"" + escaped(device_name) +
        "\",\"isLive\":" + (video_live ? "true" : "false") + ",\"session\":"+std::to_string(!video&&state.active?state.owner:0)+",\"playing\":" + (playing ? "true" : "false") + ",\"title\":\"" + escaped(state.title) +
        "\",\"artist\":\"" + escaped(state.artist) + "\",\"album\":\"" + escaped(state.album) +
        "\",\"positionMs\":" + std::to_string(video ? video_position : position_at(GetTickCount64())) +
        ",\"durationMs\":" + std::to_string(video ? video_duration : state.duration) +
        ",\"artworkRevision\":" + std::to_string(state.artwork_revision) + ",\"artworkBytes\":" +
        std::to_string(video ? 0 : state.artwork.size()) + ",\"controls\":" + std::to_string(direct_video ? video_controls : video ? 0 : state.controls) +
        ",\"source\":\"" + (direct_video ? "direct" : mirror ? "mirror" : state.active ? "audio" : "none") +
        "\",\"controlSession\":" + std::to_string(direct_video ? video_session : state.active ? state.owner : 0) +
        ",\"contentRevision\":" + std::to_string(direct_video ? video_session : state.content_revision) +
        ",\"seekStartMs\":" + std::to_string(direct_video ? seek_start : 0) +
        ",\"seekEndMs\":" + std::to_string(direct_video ? seek_end : state.duration) +
        ",\"audioActivity\":" + activity_json(GetTickCount64(), !video && state.active && state.playing) +
        ",\"diagnostics\":" + clearcast_diagnostics::json(diagnostics) + "}";
    const int required = int(json.size()) + 1;
    if (required > 8192) return 0;
    if (buffer && capacity >= required) memcpy(buffer, json.c_str(), required);
    return required;
} catch (...) { return 0; }
extern "C" __declspec(dllexport) int clearcast_receiver_read_artwork(uint64_t revision, void* buffer, int capacity) {
    using namespace clearcast_media; Lock lock(state_lock);
    if (revision != state.artwork_revision) return -1;
    if (capacity < 0 || (!buffer && capacity)) return -2;
    const int length = int(state.artwork.size());
    if (buffer && capacity >= length && length) memcpy(buffer, state.artwork.data(), length);
    return length;
}
static int clearcast_media_command_audio(int command, uint64_t session = 0, uint64_t expected_content = 0) {
    using namespace clearcast_media;
    if (command < 1 || command > 3) return ERROR_INVALID_PARAMETER;
    CommandAdmission admission;
    if (!admission.admitted) return ERROR_BUSY;
    Peer peer; std::string token; unsigned short port; uint64_t owner, content;
    const int capability = command == 1 ? 1 : command == 2 ? 2 : 4;
    {
        Lock lock(state_lock);
        if (session && (state.owner != session || state.content_revision != expected_content)) return ERROR_CANCELLED;
        if (!state.active || !(state.controls & capability) || stopping || !peer_verified_locked(state.peer.key())) return ERROR_NOT_SUPPORTED;
        peer = state.peer; token = state.remote; port = state.port; owner = state.owner; content = state.content_revision;
    }
    const char* commands[] = {"playpause", "previtem", "nextitem"};
    int result = request(peer, port, token, commands[command - 1], owner, nullptr, nullptr, nullptr, nullptr, content);
    Lock lock(state_lock);
    if (stopping || state.owner != owner || state.content_revision != content) return ERROR_CANCELLED;
    if (result) {
        if(result==ERROR_ACCESS_DENIED||result==ERROR_NOT_SUPPORTED)state.denied_controls|=capability;
        state.controls &= ~capability; ++state.revision;
    }
    return result;
}
extern "C" __declspec(dllexport) int clearcast_receiver_media_command(int command) try {
    return clearcast_media_command_audio(command);
} catch (...) { return ERROR_OUTOFMEMORY; }

static int clearcast_media_seek_audio(uint64_t session, uint64_t content, int64_t target) {
    using namespace clearcast_media;
    CommandAdmission admission;
    if (!admission.admitted) return ERROR_BUSY;
    Peer peer; std::string token; unsigned short port;
    {
        Lock lock(state_lock);
        if (!session || state.owner != session || state.content_revision != content || !state.active || stopping)
            return ERROR_CANCELLED;
        if (!(state.controls & 8) || target < 0 || target > state.duration || !peer_verified_locked(state.peer.key()))
            return ERROR_NOT_SUPPORTED;
        peer = state.peer; token = state.remote; port = state.port;
    }
    const std::string command = "setproperty?dacp.playingtime=" + std::to_string(target);
    int result = request(peer, port, token, command.c_str(), session, nullptr, nullptr, nullptr, nullptr, content);
    int64_t position = -1, duration = -1;
    for (int attempt = 0; !result && attempt < 3; ++attempt) {
        result = request(peer, port, token, "getproperty?properties=dacp.playingtime", session,
            nullptr, nullptr, &position, &duration, content);
        if (!result && position >= 0 && std::llabs(position - target) <= 2000) break;
        if (!result && attempt < 2) Sleep(150);
    }
    if (!result && (position < 0 || std::llabs(position - target) > 2000)) result = ERROR_NOT_SUPPORTED;
    Lock lock(state_lock);
    if (state.owner != session || state.content_revision != content) return ERROR_CANCELLED;
    if (result) {
        state.controls &= ~8;
        if (result == ERROR_NOT_SUPPORTED || result == ERROR_ACCESS_DENIED) state.denied_controls |= 8;
    } else {
        state.position = position; state.duration = duration; state.position_tick = GetTickCount64();
        reset_activity_locked();
    }
    ++state.revision;
    return result;
}
