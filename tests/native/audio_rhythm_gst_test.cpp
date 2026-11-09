// Installed AAC/ALAC codecs and the production read-only PCM tap; no sound output.
// SPDX-License-Identifier: GPL-3.0-or-later
#include <winsock2.h>
#include <windows.h>
#include <atomic>
#include <mutex>
#include <cstdio>
#include <string>
#include <vector>
#include <limits>
#include <gst/app/gstappsrc.h>
#include "clearcast_media_audio.h"
#include "clearcast_audio_activity_gst.h"

namespace {
static bool clearcast_mirror_media_active() { return false; }
static bool clearcast_direct_video_snapshot(bool &playing, int64_t &position, int64_t &duration) {
    playing = false; position = duration = 0; return false;
}
}
extern "C" void clearcast_direct_video_stop() {}

struct Context {
    ClearCastAudioTap tap = {};
    int mode = 0;
    std::atomic<unsigned int> encoded{0}, rendered{0};
    unsigned int published = 0, wrong_epoch = 0;
    double energy_sum = 0, density_sum = 0;
    double last_density = 0;
    std::string format;
    uint64_t source_id = 1001, valid_epoch = 0;
    uint64_t last_beat_sequence = 0, max_batch_gap = 0;
    unsigned int beats = 0, max_batch_beats = 0;
    double beat_strength_sum = 0;
};
static GstPadProbeReturn inject_epoch(GstPad *pad, GstPadProbeInfo *probe, gpointer pointer) {
    auto *context = static_cast<Context *>(pointer);
    ++context->encoded;
    if (context->mode == 1) return GST_PAD_PROBE_OK; // Missing tags must leave playback intact.
    GstBuffer *buffer = gst_buffer_make_writable(GST_PAD_PROBE_INFO_BUFFER(probe));
    GST_PAD_PROBE_INFO_DATA(probe) = buffer;
    uint64_t epoch = clearcast_media_audio_activity_epoch_for_source(context->source_id);
    clearcast_audio_epoch_add(buffer, context->mode == 2 ? context->valid_epoch - 1 : epoch);
    if (context->mode == 3) clearcast_audio_epoch_add(buffer, context->valid_epoch - 1);
    return GST_PAD_PROBE_OK;
}
static GstPadProbeReturn observe_activity(GstPad *pad, GstPadProbeInfo *probe, gpointer pointer) {
    auto *context = static_cast<Context *>(pointer);
    using namespace clearcast_media;
    Lock lock(activity_lock);
    if (activity.tick && activity.epoch == activity_generation && activity.owner == 1001) {
        ++context->published;
        context->energy_sum += activity.energy; context->density_sum += activity.density;
        context->last_density = activity.density;
        if (clearcast_audio_buffer_epoch(GST_PAD_PROBE_INFO_BUFFER(probe)) != activity.epoch) ++context->wrong_epoch;
        unsigned int new_beats = 0; uint64_t first_tick = 0, last_tick = 0;
        AcquireSRWLockShared(&beat_lock);
        for(size_t i=0;i<beat_count;++i) {
            const auto &event=beat_history[(beat_head+i)%beat_history.size()];
            if(event.epoch==activity.epoch&&event.sequence>context->last_beat_sequence) {
                context->last_beat_sequence=event.sequence;
                context->beat_strength_sum+=event.strength;++context->beats;++new_beats;
                if(!first_tick)first_tick=event.tick;last_tick=event.tick;
            }
        }
        ReleaseSRWLockShared(&beat_lock);
        context->max_batch_beats=MAX(context->max_batch_beats,new_beats);
        if(new_beats>1&&last_tick>=first_tick)context->max_batch_gap=MAX(context->max_batch_gap,last_tick-first_tick);
    }
    return GST_PAD_PROBE_OK;
}
static void handoff(GstElement *sink, GstBuffer *buffer, GstPad *pad, gpointer pointer) {
    ++static_cast<Context *>(pointer)->rendered;
}
static int failures = 0, checks = 0;
static void check(bool value, const char *name) {
    ++checks;
    if (!value) { ++failures; fprintf(stderr, "FAIL: %s\n", name); }
}
struct Result { std::string codec, format; int mode; double volume, energy, density; unsigned int published, rendered, beats, batch_beats; double beat_strength; uint64_t batch_gap; };
static Result run(const char *codec, int mode, double volume) {
    Context context; context.mode = mode;
    const unsigned char peer[] = {127,0,0,1};
    clearcast_media_verified(1000,peer,4,0); clearcast_media_audio_setup(1000,peer,4,0,0,nullptr,nullptr);
    clearcast_media_audio_packet(1000);
    clearcast_media_verified(1001,peer,4,0); clearcast_media_audio_setup(1001,peer,4,0,0,nullptr,nullptr);
    clearcast_media_audio_packet(1000);
    check(!clearcast_media::state.active && !clearcast_media_audio_activity_epoch_for_source(1000),
        "old producer cannot activate or tag the new owner before PCM reaches the pipeline");
    clearcast_media_audio_packet(1001);
    context.valid_epoch = clearcast_media_audio_activity_epoch_for_source(1001);
    if (mode == 4) context.source_id = 1000;
    std::string launch = std::string("appsrc name=source ! audioconvert ! avenc_") + codec +
        " name=encoder ! avdec_" + codec +
        " ! audioconvert ! audioresample ! volume name=volume ! fakesink name=sink sync=false signal-handoffs=true";
    GError *error = nullptr;
    GstElement *pipeline = gst_parse_launch(launch.c_str(), &error);
    check(pipeline && !error, "installed codec pipeline can be built");
    if (error) { fprintf(stderr, "pipeline: %s\n", error->message); g_clear_error(&error); }
    if (!pipeline) return {codec,"",mode,volume,0,0,0,0};
    GstElement *source = gst_bin_get_by_name(GST_BIN(pipeline), "source");
    GstElement *encoder = gst_bin_get_by_name(GST_BIN(pipeline), "encoder");
    GstElement *gain = gst_bin_get_by_name(GST_BIN(pipeline), "volume");
    GstElement *sink = gst_bin_get_by_name(GST_BIN(pipeline), "sink");
    GstCaps *caps = gst_caps_from_string("audio/x-raw,format=F32LE,rate=44100,channels=2,layout=interleaved");
    g_object_set(source,"caps",caps,"format",GST_FORMAT_TIME,"block",TRUE,"max-bytes",(guint64)65536,NULL);
    gst_caps_unref(caps);
    g_object_set(gain,"volume",volume,NULL);
    g_signal_connect(sink,"handoff",G_CALLBACK(handoff),&context);
    GstPad *encoded_pad = gst_element_get_static_pad(encoder,"src");
    GstPad *pcm_pad = gst_element_get_static_pad(gain,"sink");
    gulong injection = gst_pad_add_probe(encoded_pad,GST_PAD_PROBE_TYPE_BUFFER,inject_epoch,&context,NULL);
    gulong tap = clearcast_audio_tap_attach(pcm_pad,&context.tap);
    gulong observer = gst_pad_add_probe(pcm_pad,GST_PAD_PROBE_TYPE_BUFFER,observe_activity,&context,NULL);
    GstBus *bus = gst_element_get_bus(pipeline);
    check(gst_element_set_state(pipeline,GST_STATE_PLAYING) != GST_STATE_CHANGE_FAILURE,"codec playback pipeline starts");
    const unsigned int rate = 44100, frames = rate * 12, chunk = 441;
    bool pushed = true;
    for (unsigned int frame = 0; frame < frames && pushed; frame += chunk) {
        unsigned int count = MIN(chunk,frames-frame);
        GstBuffer *buffer = gst_buffer_new_allocate(nullptr,count*2*sizeof(float),nullptr);
        GstMapInfo map;
        if (!gst_buffer_map(buffer,&map,GST_MAP_WRITE)) { gst_buffer_unref(buffer); pushed = false; break; }
        float *samples = reinterpret_cast<float *>(map.data);
        for (unsigned int i = 0; i < count; ++i) {
            double phase = fmod(double(frame+i)/rate,.5);
            float x = float(phase < .1 ? .45*exp(-phase/.025)*sin(2*3.14159265358979323846*80*phase) : 0);
            samples[2*i] = x; samples[2*i+1] = -x;
        }
        gst_buffer_unmap(buffer,&map);
        GST_BUFFER_PTS(buffer) = gst_util_uint64_scale(frame,GST_SECOND,rate);
        GST_BUFFER_DURATION(buffer) = gst_util_uint64_scale(count,GST_SECOND,rate);
        pushed = gst_app_src_push_buffer(GST_APP_SRC(source),buffer) == GST_FLOW_OK;
    }
    gst_app_src_end_of_stream(GST_APP_SRC(source));
    GstMessage *message = gst_bus_timed_pop_filtered(bus,15*GST_SECOND,
        (GstMessageType)(GST_MESSAGE_ERROR|GST_MESSAGE_EOS));
    bool eos = message && GST_MESSAGE_TYPE(message)==GST_MESSAGE_EOS;
    if (message && GST_MESSAGE_TYPE(message)==GST_MESSAGE_ERROR) {
        GError *failure = nullptr; gchar *debug = nullptr;
        gst_message_parse_error(message,&failure,&debug);
        fprintf(stderr,"decode: %s\n",failure ? failure->message : "unknown error");
        g_clear_error(&failure); g_free(debug);
    }
    check(pushed && eos,"synthetic encoded audio reaches EOS through actual decoder and resampler");
    if (message) gst_message_unref(message);
    gst_element_set_state(pipeline,GST_STATE_NULL);
    check(context.encoded > 100 && context.rendered > 100,"analysis never prevents decoded audio reaching fakesink");
    check(context.wrong_epoch == 0,"no wrong-owner epoch is published");
    if (mode == 0) check(context.published > 50 && context.density_sum/context.published > .15,
        "real decoded PCM preserves metadata and detects actual rhythmic pulses");
    else check(context.published == 0,"missing, stale, mixed or wrong-producer epoch metadata fails closed");
    check(mode==0?context.beats>=20:context.beats==0,"actual codecs preserve only current-source low-frequency beat events");
    std::string format = context.tap.supported ? GST_AUDIO_INFO_NAME(&context.tap.info) : "unsupported";
    Result result{codec,format,mode,volume,
        context.published ? context.energy_sum/context.published : 0,
        context.published ? context.density_sum/context.published : 0,
        context.published,context.rendered.load(),context.beats,context.max_batch_beats,
        context.beats?context.beat_strength_sum/context.beats:0,context.max_batch_gap};
    gst_pad_remove_probe(encoded_pad,injection); gst_pad_remove_probe(pcm_pad,tap); gst_pad_remove_probe(pcm_pad,observer);
    gst_object_unref(encoded_pad); gst_object_unref(pcm_pad); gst_object_unref(source); gst_object_unref(encoder);
    gst_object_unref(gain); gst_object_unref(sink); gst_object_unref(bus); gst_object_unref(pipeline);
    return result;
}
static Result raw_case(const char *format_name, int channels, int chunk, int edge = 0, double *last = nullptr, double beat_rate = 2) {
    Context context;
    const unsigned char peer[] = {127,0,0,1};
    clearcast_media_verified(1001,peer,4,0); clearcast_media_audio_setup(1001,peer,4,0,0,nullptr,nullptr);
    clearcast_media_audio_packet(1001); clearcast_media_audio_activity_boundary();
    std::string description = std::string("audio/x-raw,format=") + format_name +
        ",rate=44100,channels=" + std::to_string(channels) + ",layout=" +
        (edge == 1 ? "non-interleaved" : "interleaved");
    if (channels > 2) description += ",channel-mask=(bitmask)0";
    GstCaps *caps = gst_caps_from_string(description.c_str());
    GstAudioInfo info; gst_audio_info_init(&info);
    check(gst_audio_info_from_caps(&info,caps),"synthetic raw format caps are valid");
    GstElement *pipeline = gst_parse_launch("appsrc name=source ! fakesink name=sink sync=false signal-handoffs=true",nullptr);
    GstElement *source = gst_bin_get_by_name(GST_BIN(pipeline),"source");
    GstElement *sink = gst_bin_get_by_name(GST_BIN(pipeline),"sink");
    g_object_set(source,"caps",caps,"format",GST_FORMAT_TIME,"block",TRUE,"max-bytes",(guint64)65536,NULL);
    gst_caps_unref(caps);
    g_signal_connect(sink,"handoff",G_CALLBACK(handoff),&context);
    GstPad *pad = gst_element_get_static_pad(sink,"sink");
    gulong tap = clearcast_audio_tap_attach(pad,&context.tap);
    gulong observer = gst_pad_add_probe(pad,GST_PAD_PROBE_TYPE_BUFFER,observe_activity,&context,NULL);
    GstBus *bus = gst_element_get_bus(pipeline);
    gst_element_set_state(pipeline,GST_STATE_PLAYING);
    unsigned int total = edge == 3 ? 8193 : edge == 4 ? 8192 : 44100 * 4;
    uint64_t initial_sequence;
    { clearcast_media::Lock lock(clearcast_media::beat_lock); initial_sequence=clearcast_media::beat_sequence; }
    bool pushed = true;
    for (unsigned int frame = 0; frame < total && pushed; frame += chunk) {
        unsigned int count = MIN((unsigned int)chunk,total-frame);
        GstBuffer *buffer = gst_buffer_new_allocate(nullptr,count*GST_AUDIO_INFO_BPF(&info),nullptr);
        GstMapInfo map; gst_buffer_map(buffer,&map,GST_MAP_WRITE);
        std::vector<double> real(count*channels);
        std::vector<gint32> integer(count*channels);
        for (unsigned int i=0;i<count;++i) {
            double phase=fmod(double(frame+i)/44100,1/beat_rate);
            double x=phase<.1?.45*exp(-phase/.025)*sin(2*3.14159265358979323846*80*phase):0;
            if (edge==2||(edge==4&&frame+i==total-1)) x=std::numeric_limits<double>::quiet_NaN();
            for (int channel=0;channel<channels;++channel) {
                real[i*channels+channel]=channel%2?-x:x;
                integer[i*channels+channel]=isfinite(real[i*channels+channel])?gint32(real[i*channels+channel]*2147483647.0):0;
            }
        }
        info.finfo->pack_func(info.finfo,GST_AUDIO_PACK_FLAG_NONE,
            info.finfo->unpack_format==GST_AUDIO_FORMAT_S32?(const void*)integer.data():(const void*)real.data(),
            map.data,count*channels);
        gst_buffer_unmap(buffer,&map);
        clearcast_audio_epoch_add(buffer,clearcast_media_audio_activity_epoch_for_source(1001));
        GST_BUFFER_PTS(buffer)=gst_util_uint64_scale(frame,GST_SECOND,44100);
        GST_BUFFER_DURATION(buffer)=gst_util_uint64_scale(count,GST_SECOND,44100);
        pushed=gst_app_src_push_buffer(GST_APP_SRC(source),buffer)==GST_FLOW_OK;
    }
    gst_app_src_end_of_stream(GST_APP_SRC(source));
    GstMessage *message=gst_bus_timed_pop_filtered(bus,5*GST_SECOND,(GstMessageType)(GST_MESSAGE_ERROR|GST_MESSAGE_EOS));
    check(pushed&&message&&GST_MESSAGE_TYPE(message)==GST_MESSAGE_EOS&&context.rendered>0,
        "raw PCM reaches fakesink regardless of analysis support");
    if(message)gst_message_unref(message);
    gst_element_set_state(pipeline,GST_STATE_NULL);
    check(edge ? context.published==0 : context.published>5,
        "supported formats analyze; planar, nonfinite and oversized buffers fail closed");
    check(edge?context.beats==0:context.beats>=unsigned(beat_rate*4)-1,"raw format andsplit handling preserve strong-beat event counts");
    if(edge==4)check(clearcast_media::beat_sequence==initial_sequence,
        "a NaN atbuffer tail commits none ofthe valid-prefix beat candidates");
    if(last)*last=context.last_density;
    Result result{std::string("raw-")+format_name,context.tap.supported?GST_AUDIO_INFO_NAME(&context.tap.info):"unsupported",
        edge,1,context.published?context.energy_sum/context.published:0,
        context.published?context.density_sum/context.published:0,context.published,context.rendered.load(),
        context.beats,context.max_batch_beats,context.beats?context.beat_strength_sum/context.beats:0,context.max_batch_gap};
    gst_pad_remove_probe(pad,tap);gst_pad_remove_probe(pad,observer);
    gst_object_unref(pad);gst_object_unref(source);gst_object_unref(sink);gst_object_unref(bus);gst_object_unref(pipeline);
    return result;
}
static void flush_start_case() {
    using namespace clearcast_media;
    const unsigned char peer[]={127,0,0,1};
    clearcast_media_verified(1001,peer,4,0);clearcast_media_audio_setup(1001,peer,4,0,0,nullptr,nullptr);
    clearcast_media_audio_packet(1001);
    ClearCastAudioTap tap={};tap.epoch=clearcast_media_audio_activity_epoch_for_source(1001);
    clearcast_media_audio_activity_publish(tap.epoch,.5,.6);
    g_atomic_pointer_set(&tap.atomic_epoch,(gpointer)(guintptr)tap.epoch);
    tap.rhythm.frames=12345;
    GstPadProbeInfo probe={};probe.type=GST_PAD_PROBE_TYPE_EVENT_DOWNSTREAM;
    GstEvent *event=gst_event_new_flush_start();probe.data=event;
    clearcast_audio_tap_probe(nullptr,&probe,&tap);gst_event_unref(event);
    check(g_atomic_int_get(&tap.flushing)&&tap.rhythm.frames==12345,
        "nonserialized FLUSH_START does not touch streaming DSP state");
    clearcast_media_audio_activity_publish(tap.epoch,.9,.9);
    check(activity.epoch!=activity_generation||!activity.tick,
        "FLUSH_START revokes the epoch so an in-flight publish cannot restore old activity");
    event=gst_event_new_flush_stop(FALSE);probe.data=event;
    clearcast_audio_tap_probe(nullptr,&probe,&tap);gst_event_unref(event);
    check(!g_atomic_int_get(&tap.flushing)&&!tap.epoch&&!tap.rhythm.frames,
        "serialized FLUSH_STOP clears the history before new PCM");
}
int main(int argc,char **argv) {
    if (argc != 2) return 2;
    gchar *plugins = g_build_filename(argv[1],"lib","gstreamer-1.0",NULL);
    g_setenv("GST_PLUGIN_SYSTEM_PATH_1_0",plugins,TRUE); g_setenv("GST_PLUGIN_PATH_1_0","",TRUE);
    g_setenv("GST_REGISTRY","clearcast-rhythm-fixture-registry.bin",TRUE);
    gst_registry_fork_set_enabled(FALSE); gst_init(nullptr,nullptr); g_free(plugins);
    std::vector<Result> results;
    for (const char *codec : {"alac","aac"}) {
        results.push_back(run(codec,0,1));
        results.push_back(run(codec,0,.05));
        check(fabs(results[results.size()-1].density-results[results.size()-2].density)<.01 &&
            fabs(results[results.size()-1].energy-results[results.size()-2].energy)<.01,
            "pre-volume activity is independent of receiver output volume");
        for (int mode : {1,2,3,4}) results.push_back(run(codec,mode,1));
    }
    for(const char *format:{"S16LE","S24LE","S32BE","F32BE","F64LE"})results.push_back(raw_case(format,2,882));
    results.push_back(raw_case("F32LE",1,882));results.push_back(raw_case("S16LE",6,882));
    double small=0,large=0;
    results.push_back(raw_case("F32LE",2,308,0,&small));results.push_back(raw_case("F32LE",2,4983,0,&large));
    check(fabs(small-large)<.001,"7ms and113ms buffer splits retain the same final density");
    results.push_back(raw_case("F32LE",2,882,1));results.push_back(raw_case("F32LE",2,882,2));
    results.push_back(raw_case("F32LE",2,8193,3));flush_start_case();
    results.push_back(raw_case("F32LE",2,8192,0,nullptr,8));
    check(results.back().batch_beats>=2&&results.back().batch_gap>=100,
        "two low-frequency beats in one decodedbuffer keep distinct sample-offset times andIDs");
    results.push_back(raw_case("F32LE",2,8192,4,nullptr,8));
    printf("{\"checks\":%d,\"passed\":%s,\"cases\":[",checks,failures?"false":"true");
    for (size_t i=0;i<results.size();++i) {
        const auto &r=results[i];
        printf("%s{\"codec\":\"%s\",\"pcmFormat\":\"%s\",\"mode\":%d,\"volume\":%.3f,\"energy\":%.5f,\"density\":%.5f,\"activityBuffers\":%u,\"renderedBuffers\":%u,\"beats\":%u,\"beatStrength\":%.5f,\"maxBatchBeats\":%u,\"batchGapMs\":%llu}",
            i?",":"",r.codec.c_str(),r.format.c_str(),r.mode,r.volume,r.energy,r.density,r.published,r.rendered,
            r.beats,r.beat_strength,r.batch_beats,(unsigned long long)r.batch_gap);
    }
    printf("]}\n");
    return failures?1:0;
}
