// SPDX-License-Identifier: GPL-3.0-or-later
#include <winsock2.h>
#include <windows.h>
#include <glib.h>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <vector>
#include <string>
#include <limits>
#include <clocale>
#include <thread>
#include "clearcast_media_audio.h"

namespace {
static bool test_mirror_active=false;
static bool test_direct_active=false;
static bool clearcast_mirror_media_active(){return test_mirror_active;}
static bool clearcast_direct_video_snapshot(bool& playing, int64_t& position, int64_t& duration) {
    playing = false; position = duration = 0; return test_direct_active;
}
}
extern "C" void clearcast_direct_video_stop() {}
static int checks = 0, failures = 0;
static void check(bool value, const char* message) {
    ++checks;
    if (!value) { ++failures; fprintf(stderr, "FAIL: %s\n", message); }
}
static std::vector<unsigned char> tag(const char* name, const std::vector<unsigned char>& data) {
    std::vector<unsigned char> result(name, name + 4);
    uint32_t size = static_cast<uint32_t>(data.size());
    result.insert(result.end(), {static_cast<unsigned char>(size >> 24), static_cast<unsigned char>(size >> 16),
        static_cast<unsigned char>(size >> 8), static_cast<unsigned char>(size)});
    result.insert(result.end(), data.begin(), data.end()); return result;
}
static std::vector<unsigned char> text_tag(const char* name, const std::string& text) {
    return tag(name, std::vector<unsigned char>(text.begin(), text.end()));
}
static void append(std::vector<unsigned char>& value, const std::vector<unsigned char>& next) {
    value.insert(value.end(), next.begin(), next.end());
}
static std::string snapshot() {
    char json[8192] = {}; int length = clearcast_receiver_read_media(json, sizeof(json));
    check(length > 0 && length <= 8192 && int(strlen(json)) + 1 == length, "snapshot has exact bounded NUL length");
    return json;
}
static void connection_cases() {
    using namespace clearcast_media;
    const unsigned char peer[] = {127,0,0,1};
    auto connected = [] { return snapshot().find("\"connected\":true") != std::string::npos; };
    stopping = false;
    check(!connected(), "ready receiver has no authenticated connection or media session");
    clearcast_media_audio_setup(1001,peer,4,0,0,nullptr,nullptr);
    check(!connected() && !state.configured, "unverified SETUP cannot report connected");
    clearcast_media_verified(1001,peer,4,0);
    check(connected() && !state.configured && !state.active,
        "live pair-verified control reports connected before any media SETUP");
    clearcast_media_verified(1002,peer,4,0);
    clearcast_media_closed(1001);
    check(connected(), "closing one authenticated sibling retains the other live connection");
    clearcast_media_closed(1002);
    check(!connected() && clearcast_media_peer_verified(peer,4,0),
        "60-second peer authorization grace does not report an online connection");
    clearcast_media_audio_setup(1003,peer,4,0,0,nullptr,nullptr);
    check(connected() && state.configured && !state.active,
        "established audio SETUP reports connected without inventing playback");
    clearcast_media_audio_packet(1003);
    clearcast_media_audio_stop(1003,0);
    check(connected() && state.active && !state.playing, "paused audio retains its connection status");
    clearcast_media_closed(1003);
    check(!connected(), "media owner closure clears its established audio session");
    clearcast_media_verified(1004,peer,4,0);
    clearcast_media_audio_setup(1004,peer,4,0,0,nullptr,nullptr);
    clearcast_media_audio_stop(1004,1);
    check(connected() && !state.configured && !state.active,
        "TEARDOWN retains connected while authenticated control remains live");
    clearcast_media_closed(1004);
    check(!connected(), "last authenticated control closure without media reports disconnected");
    test_mirror_active=true;
    check(connected(), "active mirror reports an established media connection");
    test_mirror_active=false;test_direct_active=true;
    check(connected(), "active direct video reports an established media connection");
    test_direct_active=false;
    clearcast_media_verified(1005,peer,4,0);
    clearcast_media_audio_setup(1005,peer,4,0,0,nullptr,nullptr);
    stopping=true;
    check(!connected(), "shutdown reports disconnected even before transient maps are cleared");
    clearcast_media_closed(1005);verified.clear();stopping=false;
}
struct TestServer {
    SOCKET listener = INVALID_SOCKET;
    HANDLE thread = nullptr;
    unsigned short port = 0;
    std::string response, received;
    int delay = 0;
};
static DWORD WINAPI serve(void* pointer) {
    auto& server = *static_cast<TestServer*>(pointer);
    fd_set pending; FD_ZERO(&pending); FD_SET(server.listener, &pending);
    timeval timeout = {3,0};
    if (select(0,&pending,nullptr,nullptr,&timeout) <= 0) return 1;
    SOCKET client = accept(server.listener,nullptr,nullptr);
    if (client == INVALID_SOCKET) return 2;
    DWORD read_timeout = 1000;
    setsockopt(client,SOL_SOCKET,SO_RCVTIMEO,reinterpret_cast<char*>(&read_timeout),sizeof(read_timeout));
    while (server.received.find("\r\n\r\n") == std::string::npos && server.received.size() < 4096) {
        char buffer[1024]; int count = recv(client,buffer,sizeof(buffer),0);
        if (count <= 0) break;
        server.received.append(buffer,count);
    }
    if (server.delay) Sleep(server.delay);
    if (!server.response.empty()) send(client,server.response.data(),int(server.response.size()),0);
    closesocket(client);
    return 0;
}
static void control_case(const std::string& response,int expected,int command=1,int delay=0,bool ipv6=false,int expected_state=-1) {
    using namespace clearcast_media;
    TestServer server; server.response=response; server.delay=delay;
    server.listener=socket(ipv6?AF_INET6:AF_INET,SOCK_STREAM,IPPROTO_TCP);
    check(server.listener!=INVALID_SOCKET,"test control listener created");
    Peer peer; unsigned char bytes[16]={};
    if (ipv6) bytes[15]=1; else {bytes[0]=127;bytes[3]=1;}
    peer.assign(bytes,ipv6?16:4,0);
    auto address=peer.address(0); int length=ipv6?sizeof(sockaddr_in6):sizeof(sockaddr_in);
    int result=bind(server.listener,reinterpret_cast<sockaddr*>(&address),length);
    check(result==0 && listen(server.listener,1)==0,"test control listener bound on ephemeral loopback port");
    if(result!=0){closesocket(server.listener);return;}
    getsockname(server.listener,reinterpret_cast<sockaddr*>(&address),&length);
    server.port=ntohs(ipv6?reinterpret_cast<sockaddr_in6*>(&address)->sin6_port:reinterpret_cast<sockaddr_in*>(&address)->sin_port);
    clearcast_media_verified(100,bytes,ipv6?16:4,0);
    clearcast_media_audio_setup(100,bytes,ipv6?16:4,0,0,"AABBCCDDEEFF0011","12345");
    clearcast_media_audio_packet(state.owner);
    state.controls=7;state.port=server.port;stopping=false;
    server.thread=CreateThread(nullptr,0,serve,&server,0,nullptr);
    uint64_t before=GetTickCount64();
    int observed_state=-1;
    result=command?clearcast_receiver_media_command(command):request(peer,server.port,"12345","getproperty?properties=dacp.playerstate",100,&observed_state);
    uint64_t elapsed=GetTickCount64()-before;
    check(result==expected,"real DACP HTTP response mapped to command result");
    check(elapsed<1900,"DACP connection/read operation has finite deadline");
    if(command)check(state.playing,"command result does not fake a local playback toggle");
    else {
        check(observed_state==expected_state,"bounded sender status body parsed without guessing");
        if(observed_state>=0) {
            sender_state_locked(observed_state);clearcast_media_audio_packet(state.owner);
            check(state.playing==(observed_state>=4),"authoritative sender pause survives fresh silence RTP");
        }
    }
    check(WaitForSingleObject(server.thread,5000)==WAIT_OBJECT_0,"test sender thread exited");
    CloseHandle(server.thread);closesocket(server.listener);
    const char* commands[]={"playpause","previtem","nextitem"};
    const std::string path=command?commands[command-1]:"getproperty?properties=dacp.playerstate";
    check(server.received.find("GET /ctrl-int/1/"+path+" HTTP/1.1\r\n")==0,"only fixed DACP command path sent");
    check(server.received.find("\r\nActive-Remote: 12345\r\n")!=std::string::npos,"sender token carried only in fixed HTTP header");
    if(ipv6)check(server.received.find("\r\nHost: [::1]:")!=std::string::npos,"IPv6 HTTP Host is bracketed");
    if(expected&&command)check(!(state.controls&(command==1?1:command==2?2:4)),"denied or failed command removes its capability");
    stopping=true;clearcast_media_closed(100);
}
static void activity_cases() {
    using namespace clearcast_media;
    const unsigned char peer[] = {127,0,0,1};
    clearcast_media_verified(900,peer,4,0);
    clearcast_media_audio_setup(900,peer,4,0,0,nullptr,nullptr);
    clearcast_media_audio_packet(state.owner);
    uint64_t epoch = clearcast_media_audio_activity_epoch(), revision = state.revision;
    check(epoch && snapshot().find("\"audioActivity\":{\"valid\":false,\"energy\":0.000000,\"density\":0.000000") != std::string::npos,
        "RTP alone cannot invent decoded PCM activity");
    clearcast_media_audio_activity_publish(epoch,.3,.4);
    check(snapshot().find("\"audioActivity\":{\"valid\":true,\"energy\":0.300000,\"density\":0.400000") != std::string::npos,
        "current-session decoded aggregates enter the existing bounded JSON snapshot");
    check(state.revision == revision, "dynamic PCM activity does not bump metadata revision");
    const std::string original_locale = std::setlocale(LC_NUMERIC,nullptr);
    const char *german = std::setlocale(LC_NUMERIC,"de-DE");
    check(german && !strcmp(std::localeconv()->decimal_point,","), "fixture actually selects a comma-decimal CRT locale");
    check(snapshot().find("\"energy\":0.300000,\"density\":0.400000") != std::string::npos,
        "audio activity JSON uses invariant decimal points in a comma-decimal locale");
    std::setlocale(LC_NUMERIC,original_locale.c_str());
    clearcast_media_audio_activity_publish(epoch + 1,.9,.9);
    check(activity.energy == .3 && activity.density == .4, "unmatched epochs cannot publish under a current owner");
    clearcast_media_audio_activity_publish(epoch,std::numeric_limits<double>::quiet_NaN(),.5);
    check(activity.energy == .3, "nonfinite aggregates cannot poison snapshot JSON");
    activity.tick = GetTickCount64() - 901;
    check(snapshot().find("\"audioActivity\":{\"valid\":false,\"energy\":0.000000,\"density\":0.000000") != std::string::npos,
        "decoded sample age over900ms fails closed even with active RTP");
    clearcast_media_audio_activity_publish(epoch,.3,.4);
    clearcast_media_audio_stop(900,0);
    check(!clearcast_media_audio_activity_epoch() && activity.tick == 0, "pause/FLUSH clears activity immediately");
    clearcast_media_audio_activity_publish(epoch,.9,.9);
    check(activity.tick == 0, "queued pre-pause decoded buffers cannot revive activity");
    clearcast_media_audio_packet(state.owner);
    uint64_t resumed = clearcast_media_audio_activity_epoch();
    clearcast_media_audio_activity_publish(epoch,.9,.9);
    check(resumed != epoch && activity.tick == 0, "resume retains an epoch boundary against old queued PCM");
    clearcast_media_audio_activity_publish(resumed,.2,.6);
    clearcast_media_audio_activity_boundary();
    clearcast_media_audio_activity_publish(resumed,.9,.9);
    check(activity.tick == 0 && clearcast_media_audio_activity_epoch() != resumed,
        "renderer start/stop/flush boundaries discard old history without changing playback state");
    auto title = text_tag("minm","new activity track");
    resumed = clearcast_media_audio_activity_epoch();
    clearcast_media_audio_activity_publish(resumed,.2,.6);
    clearcast_media_audio_parameter(900,"application/x-dmap-tagged",title.data(),int(title.size()));
    check(activity.tick == 0 && clearcast_media_audio_activity_epoch() != resumed, "track change resets decoded history");
    const char progress1[]="progress: 0/0/4410000\r\n", progress2[]="progress: 0/441000/4410000\r\n";
    clearcast_media_audio_parameter(900,"text/parameters",progress1,sizeof(progress1)-1);
    resumed = clearcast_media_audio_activity_epoch();
    clearcast_media_audio_activity_publish(resumed,.2,.6);
    clearcast_media_audio_parameter(900,"text/parameters",progress2,sizeof(progress2)-1);
    check(activity.tick == 0 && clearcast_media_audio_activity_epoch() != resumed, "seek/progress discontinuity resets decoded history");
    clearcast_media_verified(901,peer,4,0);
    epoch = clearcast_media_audio_activity_epoch();
    clearcast_media_audio_setup(901,peer,4,0,0,nullptr,nullptr);
    clearcast_media_audio_packet(900);
    check(!state.active && !clearcast_media_audio_activity_epoch_for_source(900), "late prior-source RTP cannot activate or tag a new audio owner");
    clearcast_media_audio_packet(901);
    check(clearcast_media_audio_activity_epoch_for_source(901) != 0, "current source receives its own active analysis epoch");
    clearcast_media_audio_activity_publish(epoch,.9,.9);
    check(state.owner == 901 && activity.tick == 0, "late PCM from a prior audio session cannot overwrite a new owner");
    clearcast_media_audio_activity_publish(clearcast_media_audio_activity_epoch(),.2,.6);
    test_mirror_active = true;
    check(snapshot().find("\"audioActivity\":{\"valid\":false,\"energy\":0.000000,\"density\":0.000000") != std::string::npos,
        "video snapshots expose no audio animation activity");
    test_mirror_active = false;
    clearcast_media_audio_stop(901,1);
    check(!clearcast_media_audio_activity_epoch() && activity.tick == 0 && activity.owner == 0,
        "TEARDOWN clears activity ownership and samples");
}
static std::vector<uint64_t> beat_sequences(const std::string& json) {
    std::vector<uint64_t> sequences;
    size_t cursor=json.find("\"beats\":[");
    if(cursor==std::string::npos)return sequences;
    while((cursor=json.find("\"sequence\":",cursor))!=std::string::npos) {
        cursor+=11;
        sequences.push_back(std::strtoull(json.c_str()+cursor,nullptr,10));
    }
    return sequences;
}
static void beat_cases() {
    using namespace clearcast_media;
    const unsigned char peer[]={127,0,0,1};
    clearcast_media_verified(903,peer,4,0);clearcast_media_audio_setup(903,peer,4,0,0,nullptr,nullptr);
    clearcast_media_audio_packet(903);
    uint64_t epoch=clearcast_media_audio_activity_epoch_for_source(903);
    auto pending=snapshot();
    check(pending.find("\"valid\":false")!=std::string::npos&&
        pending.find("\"beatEpoch\":"+std::to_string(epoch)+",\"beats\":[]")!=std::string::npos,
        "playing audio without fresh PCM exposes current epoch but no beat events");
    clearcast_media_audio_activity_publish(epoch,.3,.4);
    clearcast_media_audio_activity_beat(epoch,.25,140);
    clearcast_media_audio_activity_beat(epoch,.85,20);
    auto first=snapshot();auto first_ids=beat_sequences(first);
    check(first_ids.size()==2&&first_ids[0]<first_ids[1],"multiple onsets survive one read in sequence order");
    check(first.find("\"strength\":0.250000")!=std::string::npos&&first.find("\"strength\":0.850000")!=std::string::npos,
        "event strengths retain their individual amplitudes");
    check(beat_sequences(snapshot())==first_ids,"reading JSON does not consume or duplicate beat IDs");
    clearcast_media_audio_activity_publish(epoch,.1,.2);
    check(beat_sequences(snapshot())==first_ids,"later aggregate publication does not overwrite the beat history");
    const std::string original_locale=std::setlocale(LC_NUMERIC,nullptr);
    std::setlocale(LC_NUMERIC,"de-DE");
    check(snapshot().find("\"strength\":0.250000")!=std::string::npos,"beat strength JSON is locale invariant");
    std::setlocale(LC_NUMERIC,original_locale.c_str());
    for(int i=0;i<20;++i)clearcast_media_audio_activity_beat(epoch,.5,0);
    auto retained=beat_sequences(snapshot());
    check(retained.size()==16&&retained.front()>first_ids.back()&&
        std::adjacent_find(retained.begin(),retained.end(),std::greater_equal<uint64_t>())==retained.end(),
        "bounded history retains the latest16 strictly increasing events");
    const std::string title=state.title,artist=state.artist,album=state.album;
    state.title=state.artist=state.album=std::string(512,'\\');
    char full[8192];int required=clearcast_receiver_read_media(full,sizeof(full));
    check(required>0&&required<=8192&&int(strlen(full))+1==required&&
        strstr(full,"\"connected\":"),
        "maximum escaped text plus16 beats and connection status fits the8192-byte ABI");
    std::vector<char> short_buffer(required-1,'X');
    check(clearcast_receiver_read_media(short_buffer.data(),int(short_buffer.size()))==required&&
        std::all_of(short_buffer.begin(),short_buffer.end(),[](char value){return value=='X';}),
        "snapshot one byte short receives no partial JSON or NUL");
    std::vector<char> exact_buffer(required);
    check(clearcast_receiver_read_media(exact_buffer.data(),required)==required&&
        exact_buffer.back()==0&&int(strlen(exact_buffer.data()))+1==required,
        "exact required capacity includes the terminating NUL");
    state.title=title;state.artist=artist;state.album=album;
    clearcast_media_audio_activity_beat(epoch+1,.9,0);
    clearcast_media_audio_activity_beat(epoch,std::numeric_limits<double>::quiet_NaN(),0);
    clearcast_media_audio_activity_beat(epoch,.9,901);
    check(beat_sequences(snapshot())==retained,"old epochs,nonfinite strength andover-age events cannot enter the queue");
    clearcast_media_audio_activity_invalidate(epoch);
    uint64_t flushed=clearcast_media_audio_activity_epoch_for_source(903);
    auto after_flush=snapshot();
    check(flushed!=epoch&&after_flush.find("\"valid\":false")!=std::string::npos&&
        after_flush.find("\"beatEpoch\":"+std::to_string(flushed)+",\"beats\":[]")!=std::string::npos,
        "flush invalidation exposes its new epoch immediately even without fresh PCM");
    clearcast_media_audio_activity_beat(epoch,.9,0);
    clearcast_media_audio_activity_publish(flushed,.2,.3);
    check(beat_sequences(snapshot()).empty(),"an in-flight old-epoch beat cannot return after flush");
    clearcast_media_audio_activity_beat(flushed,.8,0);
    check(beat_sequences(snapshot()).size()==1,"new epoch accepts fresh beats after flush");
    activity.tick=GetTickCount64()-901;
    auto stale=snapshot();
    check(stale.find("\"valid\":false")!=std::string::npos&&beat_sequences(stale).empty()&&
        stale.find("\"beatEpoch\":"+std::to_string(flushed))!=std::string::npos,
        "900ms expiry suppresses events while retaining the playing session epoch");
    clearcast_media_audio_stop(903,0);
    check(snapshot().find("\"beatEpoch\":0,\"beats\":[]")!=std::string::npos,"pause clears beat epoch and events");
    clearcast_media_audio_packet(903);uint64_t resumed=clearcast_media_audio_activity_epoch_for_source(903);
    clearcast_media_audio_activity_publish(resumed,.2,.3);clearcast_media_audio_activity_beat(resumed,.8,0);
    test_mirror_active=true;
    check(snapshot().find("\"beatEpoch\":0,\"beats\":[]")!=std::string::npos,"video never exports music beat events");
    test_mirror_active=false;clearcast_media_audio_stop(903,1);
    check(snapshot().find("\"beatEpoch\":0,\"beats\":[]")!=std::string::npos,"TEARDOWN clears beat epoch and events");
    clearcast_media_verified(904,peer,4,0);clearcast_media_audio_setup(904,peer,4,0,0,nullptr,nullptr);
    clearcast_media_audio_packet(904);epoch=clearcast_media_audio_activity_epoch_for_source(904);
    std::atomic<bool> started(false),finished(false);
    AcquireSRWLockExclusive(&activity_lock);
    std::thread producer([&]{started=true;clearcast_media_audio_activity_publish(epoch,.2,.3);
        clearcast_media_audio_activity_beat(epoch,.8,0);finished=true;});
    while(!started)Sleep(0);
    Sleep(20);
    check(!finished,"first aggregate is not discarded when its short independent lock is busy");
    ReleaseSRWLockExclusive(&activity_lock);producer.join();
    auto sole=snapshot();
    check(sole.find("\"valid\":true")!=std::string::npos&&beat_sequences(sole).size()==1,
        "a sole fresh PCM window andbeat remain visible after aggregate lock contention");
    clearcast_media_audio_stop(904,1);
}
int main(int argc,char** argv) {
    using namespace clearcast_media;
    connection_cases();
    const unsigned char peer[] = {127,0,0,1}, other[] = {127,0,0,2};
    check(!clearcast_media_peer_verified(peer, 4, 0), "new peer unverified");
    clearcast_media_verified(1, peer, 4, 0);
    check(clearcast_media_peer_verified(peer, 4, 0), "verified peer admitted");
    clearcast_media_remote(1, "0123456789ABCDEF", "1234567890");
    clearcast_media_audio_setup(1, peer, 4, 0, 0, nullptr, nullptr);
    check(state.dacp == "0123456789ABCDEF" && state.remote == "1234567890", "credentials survive two-phase setup");
    check(snapshot().find("\"kind\":\"none\"") != std::string::npos, "SETUP does not fake active audio");
    clearcast_media_audio_packet(state.owner);
    check(snapshot().find("\"kind\":\"audio\"") != std::string::npos && state.playing, "fresh RTP activates audio");
    test_mirror_active=true;
    check(snapshot().find("\"kind\":\"video\"")!=std::string::npos,"active screen mirror takes precedence over audio even without usingScreen flag");
    test_mirror_active=false;

    std::vector<unsigned char> tags = text_tag("minm", "Title \"quoted\" \\ music");
    append(tags, text_tag("asar", "Artist")); append(tags, text_tag("asal", "Album"));
    tags = tag("mlit", tags);
    clearcast_media_audio_parameter(1, "application/x-dmap-tagged", tags.data(), int(tags.size()));
    auto json = snapshot();
    check(json.find("Title \\\"quoted\\\" \\\\ music") != std::string::npos && state.artist == "Artist" && state.album == "Album", "DMAP text safely escaped and published");
    uint64_t revision = state.revision;
    clearcast_media_audio_parameter(2, "application/x-dmap-tagged", tags.data(), int(tags.size()));
    check(state.revision == revision, "unrelated connection cannot mutate metadata");
    auto bad = tags; bad[7] = 0xff;
    clearcast_media_audio_parameter(1, "application/x-dmap-tagged", bad.data(), int(bad.size()));
    check(state.revision == revision, "oversized DMAP item rejects entire snapshot");
    bad = text_tag("minm", std::string(513, 'x'));
    clearcast_media_audio_parameter(1, "application/x-dmap-tagged", bad.data(), int(bad.size()));
    check(state.revision == revision, "oversized title bounded");
    bad = tag("minm", {0xc0,0x80});
    clearcast_media_audio_parameter(1, "application/x-dmap-tagged", bad.data(), int(bad.size()));
    check(state.revision == revision, "invalid UTF8 rejected");
    bad = text_tag("minm", std::string("A\0B", 3));
    clearcast_media_audio_parameter(1, "application/x-dmap-tagged", bad.data(), int(bad.size()));
    check(state.revision == revision, "embedded NUL rejected");
    bad = tags; for (int i = 0; i < 5; ++i) bad = tag("mlit", bad);
    clearcast_media_audio_parameter(1, "application/x-dmap-tagged", bad.data(), int(bad.size()));
    check(state.revision == revision, "DMAP nesting bounded");
    bad.clear(); for (int i = 0; i < 257; ++i) append(bad, tag("xxxx", {}));
    clearcast_media_audio_parameter(1, "application/x-dmap-tagged", bad.data(), int(bad.size()));
    check(state.revision == revision, "DMAP item count bounded");
    const std::string unicode_title="\xE5\xA4\x9C\xE6\x9B\xB2 \xF0\x9F\x8E\xB5";
    auto unicode=text_tag("minm",unicode_title);
    clearcast_media_audio_parameter(1,"application/x-dmap-tagged",unicode.data(),int(unicode.size()));
    check(state.title==unicode_title && snapshot().find(unicode_title)!=std::string::npos,"valid UTF8 multilingual title preserved");
    for (size_t length = 0; length < tags.size(); ++length) {
        Tags parsed; check(!parse_tags(tags.data(), length, parsed) || length == 0, "every truncated outer DMAP record fails safely");
    }
    uint32_t random = 0x391b1234;
    for (int i = 0; i < 1000; ++i) {
        bad = tags; random = random * 1664525 + 1013904223;
        bad[random % bad.size()] ^= static_cast<unsigned char>((random >> 16) | 1);
        Tags parsed; (void)parse_tags(bad.data(), bad.size(), parsed); ++checks;
    }
    char small[8]; memset(small, 0x5a, sizeof(small));
    int needed = clearcast_receiver_read_media(small, sizeof(small));
    check(needed > int(sizeof(small)) && small[0] == 0x5a && small[7] == 0x5a, "small snapshot buffer receives no partial JSON");

    unsigned char png[33] = {137,80,78,71,13,10,26,10,0,0,0,13,'I','H','D','R',0,0,0,1,0,0,0,1};
    unsigned int width, height;
    check(artwork_dimensions(png, sizeof(png), width, height) && width == 1 && height == 1, "PNG dimensions parsed before decoder");
    clearcast_media_audio_parameter(1, "image/png", png, sizeof(png));
    uint64_t art_revision = state.artwork_revision;
    unsigned char art[33] = {};
    check(clearcast_receiver_read_artwork(art_revision, art, sizeof(art)) == 33 && !memcmp(art,png,33), "revision-matched compressed artwork copied");
    check(clearcast_receiver_read_artwork(art_revision - 1, art, sizeof(art)) == -1, "stale artwork revision rejected");
    memset(art, 0x5a, sizeof(art));
    check(clearcast_receiver_read_artwork(art_revision, art, 1) == 33 && art[0] == 0x5a, "small artwork buffer receives no partial image");
    png[16] = 1;
    check(!artwork_dimensions(png, sizeof(png), width, height), "oversized PNG edge rejected");
    clearcast_media_audio_parameter(1, "image/png", png, sizeof(png));
    check(state.artwork_revision == art_revision, "invalid artwork preserves prior valid revision");
    for(const char* encoded:{"iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFklEQVR4nGPQytxHEmIY1TCqYfhqAACVPVEQvxHcdAAAAABJRU5ErkJggg==",
        "/9j/4AAQSkZJRgABAgAAAQABAAD//gAPTGF2YzYzLjEuMTAyAP/bAEMACAQEBAQEBQUFBQUFBgYGBgYGBgYGBgYGBgcHBwgICAcHBwYGBwcICAgICQkJCAgICAkJCgoKDAwLCw4ODhERFP/EAEsAAQEAAAAAAAAAAAAAAAAAAAAGAQEAAAAAAAAAAAAAAAAAAAAGEAEAAAAAAAAAAAAAAAAAAAAAEQEAAAAAAAAAAAAAAAAAAAAA/8AAEQgAEAAQAwEiAAIRAAMRAP/aAAwDAQACEQMRAD8AngDUYf/Z"}) {
        gsize size=0;guchar* actual=g_base64_decode(encoded,&size);
        check(artwork_dimensions(actual,size,width,height)&&width==16&&height==16,"actual decodable PNG/JPEG passes dimension guard");
        clearcast_media_audio_parameter(1,"image/jpeg",actual,int(size));
        std::vector<unsigned char> copy(size);
        check(clearcast_receiver_read_artwork(state.artwork_revision,copy.data(),int(copy.size()))==int(size)&&!memcmp(copy.data(),actual,size),"actual PNG/JPEG compressed bytes preserved by read API");
        g_free(actual);
    }
    png[16] = 0; png[18] = 16; png[19] = 0; png[22] = 16; png[23] = 0;
    check(!artwork_dimensions(png, sizeof(png), width, height), "PNG decompression pixel budget enforced");
    unsigned char jpeg[] = {0xff,0xd8,0xff,0xc0,0,8,8,0,2,0,3,1,0xff,0xd9};
    check(artwork_dimensions(jpeg,sizeof(jpeg),width,height) && width == 3 && height == 2, "JPEG SOF dimensions parsed");
    jpeg[4] = 0xff;
    check(!artwork_dimensions(jpeg,sizeof(jpeg),width,height), "malformed JPEG segment rejected");

    const char progress[] = "progress: 4294967000/43804/87904\r\n";
    clearcast_media_audio_parameter(1,"text/parameters",progress,sizeof(progress)-1);
    check(state.position == 1000 && state.duration == 2000, "RTP progress handles uint32 wrap at44100Hz");
    const char invalid_progress[] = "progress: 1/999999999999999999999/3\r\n";
    revision = state.revision;
    clearcast_media_audio_parameter(1,"text/parameters",invalid_progress,sizeof(invalid_progress)-1);
    check(state.revision == revision, "overflowed progress rejected");
    clearcast_media_audio_stop(1,0);
    check(state.active && !state.playing && snapshot().find("\"kind\":\"audio\"") != std::string::npos, "FLUSH pauses while retaining audio presentation");
    clearcast_media_audio_packet(state.owner); check(state.playing, "fresh packet resumes observed playback");
    state.last_packet = GetTickCount64() - 3000; snapshot();
    check(!state.playing, "stale packets do not claim continued playback");
    check(clearcast_receiver_media_command(0) == ERROR_INVALID_PARAMETER && clearcast_receiver_media_command(4) == ERROR_INVALID_PARAMETER, "control command allowlist enforced");
    check(clearcast_receiver_media_command(1) == ERROR_NOT_SUPPORTED, "controls unavailable without discovered sender endpoint");
    check(credentials("AABBCCDDEEFF0011","123456") && !credentials("AABB\r\n", "123") && !credentials("AA", "1\r\nInjected: yes") && !credentials("AA","4294967296"), "DACP credentials bounded and header injection rejected");
    auto caps=tag("cmgt",tag("caps",{3}));int cap_count=0;
    check(player_state(caps.data(),caps.size(),cap_count)==3,"DACP paused playerstate parsed");
    caps.push_back(0);cap_count=0;
    check(player_state(caps.data(),caps.size(),cap_count)==-1,"malformed DACP suffix invalidates state");
    caps=tag("cmgt",tag("caps",{4}));sender_state_locked(3);
    const int64_t paused_position=state.position;
    clearcast_media_audio_packet(state.owner);
    check(!state.playing&&position_at(GetTickCount64())==paused_position,"paused sender status freezes native progress despite received packets");
    sender_state_locked(4);check(state.playing,"observed sender playing state resumes progress");

    std::vector<unsigned char> dns(12,0); dns[2] = 0x84; dns[7] = 1;
    std::string service="itunes_ctrl_aabb._dacp._tcp.local";
    size_t offset=0;
    while (offset<service.size()) { size_t end=service.find('.',offset); if(end==std::string::npos)end=service.size(); dns.push_back(static_cast<unsigned char>(end-offset)); dns.insert(dns.end(),service.begin()+offset,service.begin()+end); offset=end+1; }
    dns.insert(dns.end(),{0,0,33,0,1,0,0,0,120,0,19,0,0,0,0,0x0e,0x69,5,'m','u','s','i','c',5,'l','o','c','a','l',0});
    check(dns_port(dns.data(),dns.size(),service)==3689, "DACP SRV resolves only exact service owner");
    check(dns_port(dns.data(),dns.size(),"itunes_ctrl_other._dacp._tcp.local")==0, "different DACP-ID response rejected");
    for(size_t length=0; length<dns.size(); ++length) check(dns_port(dns.data(),length,service)==0, "truncated DNS response safely rejected");
    unsigned char loop[] = {0xc0,0}; offset=0; std::string name;
    check(!dns_name(loop,sizeof(loop),offset,name), "DNS compression pointer loop rejected");
    clearcast_media_closed(1);
    check(!state.active && !state.configured && state.artwork.empty(), "owner closure clears transient metadata and art");
    check(clearcast_media_peer_verified(peer,4,0), "verified sibling upgrade grace retained");
    Peer address; address.assign(peer,4,0); verified[address.key()].closed=GetTickCount64()-60001;
    check(!clearcast_media_peer_verified(peer,4,0), "closed peer authorization expires");
    clearcast_media_audio_setup(9,other,4,0,0,nullptr,nullptr);
    check(!state.configured, "unverified peer cannot configure media");
    if(argc>1&&!strcmp(argv[1],"--network")) {
        WSADATA data;check(WSAStartup(MAKEWORD(2,2),&data)==0,"Winsock test host initialized");
        control_case("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",0);
        control_case("HTTP/1.1 204 No Content\r\n\r\n",0,2);
        control_case("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n",ERROR_ACCESS_DENIED);
        control_case("HTTP/1.1 501 Not Implemented\r\nContent-Length: 0\r\n\r\n",ERROR_NOT_SUPPORTED,3);
        control_case("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.2:1/arbitrary\r\n\r\n",ERROR_NOT_SUPPORTED);
        control_case("HTTP/1.1 200 OK\r\nX-Large: "+std::string(5000,'x')+"\r\n\r\n",ERROR_INVALID_DATA);
        control_case("",WSAETIMEDOUT,1,1800);
        control_case("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",0,1,0,true);
        for(unsigned char value:{3,4}) {
            auto body=tag("cmgt",tag("caps",{value}));
            control_case("HTTP/1.1 200 OK\r\nContent-Length: "+std::to_string(body.size())+"\r\n\r\n"+std::string(body.begin(),body.end()),0,0,0,false,value);
        }
        control_case("HTTP/1.1 200 OK\r\nContent-Length: 65537\r\n\r\n",ERROR_INVALID_DATA,0);
        control_case("HTTP/1.1 200 OK\r\nContent-Length: 1\r\nContent-Length: 1\r\n\r\nx",ERROR_INVALID_DATA,0);
        control_case("HTTP/1.1 200 OK\r\nContent-Length: 20\r\n\r\nx",ERROR_INVALID_DATA,0);
        WSACleanup();
    }
    activity_cases();
    beat_cases();
    printf("Audio media parser/state: %d checks, %d failures\n",checks,failures);
    return failures ? 1 : 0;
}
