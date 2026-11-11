/* Read-only decoded PCM tap. Local epoch metadata follows the input through
 * decoding so queued audio cannot acquire a newer session's identity.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#ifndef CLEARCAST_AUDIO_ACTIVITY_GST_H
#define CLEARCAST_AUDIO_ACTIVITY_GST_H
#include <gst/gst.h>
#include <gst/audio/audio.h>
#include "clearcast_audio_rhythm.h"

#ifdef __cplusplus
extern "C" {
#endif
uint64_t clearcast_media_audio_activity_epoch(void);
void clearcast_media_audio_pipeline(GstElement *pipeline, uint64_t epoch);
uint64_t clearcast_media_audio_activity_epoch_for_source(uint64_t source_id);
void clearcast_media_audio_activity_publish(uint64_t epoch, double energy, double density);
void clearcast_media_audio_activity_beat(uint64_t epoch, double strength, uint64_t sample_age_ms);
void clearcast_media_audio_activity_invalidate(uint64_t epoch);
void clearcast_media_audio_activity_boundary(void);
#ifdef __cplusplus
}
#endif

typedef struct { GstMeta meta; guint64 epoch; } ClearCastAudioEpoch;
static GType clearcast_audio_epoch_api(void) {
    static gsize type = 0;
    if (g_once_init_enter(&type)) {
        static const gchar *tags[] = {NULL};
        GType found = g_type_from_name("ClearCastAudioEpochAPI");
        g_once_init_leave(&type, found ? found : gst_meta_api_type_register("ClearCastAudioEpochAPI", tags));
    }
    return (GType)type;
}
static const GstMetaInfo *clearcast_audio_epoch_info(void);
static gboolean clearcast_audio_epoch_init(GstMeta *meta, gpointer params, GstBuffer *buffer) {
    ((ClearCastAudioEpoch *)meta)->epoch = 0;
    return TRUE;
}
static gboolean clearcast_audio_epoch_copy(GstBuffer *out, GstMeta *meta, GstBuffer *in, GQuark type, gpointer data) {
    if (!GST_META_TRANSFORM_IS_COPY(type)) return FALSE;
    ClearCastAudioEpoch *copy = (ClearCastAudioEpoch *)gst_buffer_add_meta(out, clearcast_audio_epoch_info(), NULL);
    if (!copy) return FALSE;
    copy->epoch = ((ClearCastAudioEpoch *)meta)->epoch;
    return TRUE;
}
static const GstMetaInfo *clearcast_audio_epoch_info(void) {
    static gsize info = 0;
    if (g_once_init_enter(&info)) {
        const GstMetaInfo *found = gst_meta_get_info("ClearCastAudioEpoch");
        g_once_init_leave(&info, (gsize)(found ? found : gst_meta_register(clearcast_audio_epoch_api(),
            "ClearCastAudioEpoch", sizeof(ClearCastAudioEpoch), clearcast_audio_epoch_init, NULL, clearcast_audio_epoch_copy)));
    }
    return (const GstMetaInfo *)info;
}
static gboolean clearcast_audio_epoch_add(GstBuffer *buffer, guint64 epoch) {
    ClearCastAudioEpoch *meta = (ClearCastAudioEpoch *)gst_buffer_add_meta(buffer, clearcast_audio_epoch_info(), NULL);
    if (!meta) return FALSE;
    meta->epoch = epoch;
    return TRUE;
}
static guint64 clearcast_audio_buffer_epoch(GstBuffer *buffer) {
    gpointer state = NULL;
    guint64 epoch = 0;
    guint count = 0;
    GstMeta *meta;
    while ((meta = gst_buffer_iterate_meta_filtered(buffer, &state, clearcast_audio_epoch_api()))) {
        guint64 value = ((ClearCastAudioEpoch *)meta)->epoch;
        if (++count > 64 || !value || (epoch && epoch != value)) return 0;
        epoch = value;
    }
    return epoch;
}

typedef struct {
    GstAudioInfo info;
    clearcast_rhythm_t rhythm;
    guint64 epoch, last_buffer_us;
    gpointer atomic_epoch;
    gint flushing;
    gboolean supported;
} ClearCastAudioTap;
static void clearcast_audio_tap_reset(ClearCastAudioTap *tap) {
    clearcast_media_audio_activity_invalidate(tap->epoch);
    clearcast_rhythm_reset(&tap->rhythm, tap->supported ? GST_AUDIO_INFO_RATE(&tap->info) : 0,
        tap->supported ? GST_AUDIO_INFO_CHANNELS(&tap->info) : 0);
    tap->epoch = tap->last_buffer_us = 0;
    g_atomic_pointer_set(&tap->atomic_epoch, NULL);
}
static GstPadProbeReturn clearcast_audio_tap_probe(GstPad *pad, GstPadProbeInfo *probe, gpointer context) {
    ClearCastAudioTap *tap = (ClearCastAudioTap *)context;
    if (GST_PAD_PROBE_INFO_TYPE(probe) & GST_PAD_PROBE_TYPE_EVENT_DOWNSTREAM) {
        GstEvent *event = GST_PAD_PROBE_INFO_EVENT(probe);
        if (GST_EVENT_TYPE(event) == GST_EVENT_FLUSH_START) {
            // FLUSH_START is not serialized with streaming. Touch only atomics
            // and revoke this pipeline's epoch; no DSP or caps state is read.
            g_atomic_int_set(&tap->flushing, 1);
            clearcast_media_audio_activity_invalidate((guint64)(guintptr)g_atomic_pointer_get(&tap->atomic_epoch));
            return GST_PAD_PROBE_OK;
        }
        if (GST_EVENT_TYPE(event) == GST_EVENT_CAPS) {
            GstCaps *caps = NULL;
            gst_event_parse_caps(event, &caps);
            gst_audio_info_init(&tap->info);
            tap->supported = caps && gst_audio_info_from_caps(&tap->info, caps) &&
                GST_AUDIO_INFO_LAYOUT(&tap->info) == GST_AUDIO_LAYOUT_INTERLEAVED &&
                GST_AUDIO_INFO_CHANNELS(&tap->info) >= 1 && GST_AUDIO_INFO_CHANNELS(&tap->info) <= CLEARCAST_RHYTHM_MAX_CHANNELS &&
                GST_AUDIO_INFO_RATE(&tap->info) >= 8000 && GST_AUDIO_INFO_RATE(&tap->info) <= 192000 &&
                tap->info.finfo->unpack_func && (tap->info.finfo->unpack_format == GST_AUDIO_FORMAT_S32 ||
                    tap->info.finfo->unpack_format == GST_AUDIO_FORMAT_F64);
            clearcast_audio_tap_reset(tap);
        } else if (GST_EVENT_TYPE(event) == GST_EVENT_FLUSH_STOP ||
                   GST_EVENT_TYPE(event) == GST_EVENT_SEGMENT || GST_EVENT_TYPE(event) == GST_EVENT_EOS) {
            clearcast_audio_tap_reset(tap);
            if (GST_EVENT_TYPE(event) == GST_EVENT_FLUSH_STOP) g_atomic_int_set(&tap->flushing, 0);
        }
        return GST_PAD_PROBE_OK;
    }
    if (!(GST_PAD_PROBE_INFO_TYPE(probe) & GST_PAD_PROBE_TYPE_BUFFER)) return GST_PAD_PROBE_OK;
    if (g_atomic_int_get(&tap->flushing)) return GST_PAD_PROBE_OK;
    GstBuffer *buffer = GST_PAD_PROBE_INFO_BUFFER(probe);
    guint64 epoch = clearcast_audio_buffer_epoch(buffer);
    if (!tap->supported || !epoch || epoch != clearcast_media_audio_activity_epoch()) {
        clearcast_audio_tap_reset(tap);
        return GST_PAD_PROBE_OK;
    }
    guint64 now = (guint64)g_get_monotonic_time();
    if (tap->epoch != epoch || (tap->last_buffer_us && now - tap->last_buffer_us > 900000) ||
        GST_BUFFER_FLAG_IS_SET(buffer, GST_BUFFER_FLAG_DISCONT)) {
        clearcast_audio_tap_reset(tap);
        tap->epoch = epoch;
        g_atomic_pointer_set(&tap->atomic_epoch, (gpointer)(guintptr)epoch);
    }
    tap->last_buffer_us = now;
    if (GST_BUFFER_FLAG_IS_SET(buffer, GST_BUFFER_FLAG_GAP)) {
        clearcast_audio_tap_reset(tap);
        return GST_PAD_PROBE_OK;
    }
    const gsize bpf = GST_AUDIO_INFO_BPF(&tap->info), size = gst_buffer_get_size(buffer);
    if (!size || !bpf || size % bpf || size / bpf > 8192) {
        clearcast_audio_tap_reset(tap);
        return GST_PAD_PROBE_OK;
    }
    GstMapInfo map;
    if (!gst_buffer_map(buffer, &map, GST_MAP_READ)) {
        clearcast_audio_tap_reset(tap);
        return GST_PAD_PROBE_OK;
    }
    if (map.size < size) {
        gst_buffer_unmap(buffer, &map);
        clearcast_audio_tap_reset(tap);
        return GST_PAD_PROBE_OK;
    }
    const unsigned int channels = GST_AUDIO_INFO_CHANNELS(&tap->info);
    const uint64_t buffer_end_frame = tap->rhythm.frames + size / bpf;
    const GstAudioFormatInfo *format = tap->info.finfo;
    union { gint32 integer[128 * CLEARCAST_RHYTHM_MAX_CHANNELS]; gdouble real[128 * CLEARCAST_RHYTHM_MAX_CHANNELS]; } unpacked;
    struct { uint64_t frame; double strength; } beats[16];
    unsigned int beat_count = 0;
    gboolean published = FALSE, valid = TRUE;
    for (gsize offset = 0; offset < size && valid; ) {
        gsize frames = MIN((gsize)128, (size - offset) / bpf);
        format->unpack_func(format, GST_AUDIO_PACK_FLAG_NONE, &unpacked, map.data + offset, (gint)(frames * channels));
        for (gsize frame = 0; frame < frames; ++frame) {
            double samples[CLEARCAST_RHYTHM_MAX_CHANNELS];
            for (unsigned int channel = 0; channel < channels; ++channel) {
                gsize index = frame * channels + channel;
                samples[channel] = format->unpack_format == GST_AUDIO_FORMAT_S32 ?
                    (double)unpacked.integer[index] / 2147483648.0 : unpacked.real[index];
                valid = valid && isfinite(samples[channel]);
            }
            if (!valid) break;
            if (clearcast_rhythm_frame(&tap->rhythm, samples)) {
                published = TRUE;
                if (tap->rhythm.beat_ready && !g_atomic_int_get(&tap->flushing)) {
                    // Estimate each onset's own decoded time, including the
                    // short confirmation delay and its offset in this buffer.
                    // This is not the audio device's presentation clock.
                    if (beat_count == 16) { valid = FALSE; break; }
                    beats[beat_count].frame = tap->rhythm.beat_frame;
                    beats[beat_count++].strength = tap->rhythm.beat_strength;
                }
            }
        }
        offset += frames * bpf;
    }
    gst_buffer_unmap(buffer, &map);
    if (!valid) clearcast_audio_tap_reset(tap);
    else if (published && !g_atomic_int_get(&tap->flushing) && epoch == clearcast_media_audio_activity_epoch()) {
        clearcast_media_audio_activity_publish(epoch, tap->rhythm.energy, tap->rhythm.density);
        for (unsigned int i=0;i<beat_count&&!g_atomic_int_get(&tap->flushing);++i) {
            uint64_t age = (buffer_end_frame - beats[i].frame) * 1000 / tap->rhythm.rate;
            clearcast_media_audio_activity_beat(epoch,beats[i].strength,age);
        }
    }
    return GST_PAD_PROBE_OK;
}
static gulong clearcast_audio_tap_attach(GstPad *pad, ClearCastAudioTap *tap) {
    memset(tap, 0, sizeof(*tap));
    return gst_pad_add_probe(pad, (GstPadProbeType)(GST_PAD_PROBE_TYPE_BUFFER |
        GST_PAD_PROBE_TYPE_EVENT_DOWNSTREAM | GST_PAD_PROBE_TYPE_EVENT_FLUSH), clearcast_audio_tap_probe, tap, NULL);
}
#endif
