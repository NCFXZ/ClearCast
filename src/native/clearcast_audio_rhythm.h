/* Fixed-state PCM analysis for a background animation, not a tempo estimator.
 * No audio is retained beyond per-channel filter state and a 20ms energy sum.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#ifndef CLEARCAST_AUDIO_RHYTHM_H
#define CLEARCAST_AUDIO_RHYTHM_H
#include <math.h>
#include <stdbool.h>
#include <stdint.h>
#include <string.h>

#define CLEARCAST_RHYTHM_MAX_CHANNELS 8
typedef struct clearcast_rhythm_s {
    unsigned int rate, channels, window_frames, window_used;
    uint64_t frames, last_onset_frame, onsets, windows;
    double hp_alpha, lp_alpha;
    double previous[CLEARCAST_RHYTHM_MAX_CHANNELS];
    double highpass[CLEARCAST_RHYTHM_MAX_CHANNELS];
    double lowpass[CLEARCAST_RHYTHM_MAX_CHANNELS];
    double sum_full, sum_low, previous_full, previous_low, mean_full, mean_low;
    double energy, density;
    double beat_hp_alpha, beat_lp_alpha;
    double beat_previous[CLEARCAST_RHYTHM_MAX_CHANNELS];
    double beat_highpass[CLEARCAST_RHYTHM_MAX_CHANNELS];
    double beat_lowpass[CLEARCAST_RHYTHM_MAX_CHANNELS], beat_lowpass2[CLEARCAST_RHYTHM_MAX_CHANNELS];
    double sum_beat, previous_beat, mean_beat;
    bool beat_candidate, beat_ready;
    uint64_t beat_candidate_start, beat_candidate_frame, beat_peak_frame, beat_frame, beat_count;
    double beat_peak, beat_full_peak, beat_ratio, beat_flux, beat_strength;
} clearcast_rhythm_t;

static double clearcast_rhythm_unit(double value) {
    return value < 0 ? 0 : value > 1 ? 1 : value;
}
static bool clearcast_rhythm_reset(clearcast_rhythm_t *state, unsigned int rate, unsigned int channels) {
    memset(state, 0, sizeof(*state));
    if (rate < 8000 || rate > 192000 || !channels || channels > CLEARCAST_RHYTHM_MAX_CHANNELS) return false;
    state->rate = rate; state->channels = channels; state->window_frames = rate / 50;
    state->hp_alpha = exp(-2.0 * 3.14159265358979323846 * 30 / rate);
    state->lp_alpha = 1 - exp(-2.0 * 3.14159265358979323846 * 180 / rate);
    state->beat_hp_alpha = exp(-2.0 * 3.14159265358979323846 * 45 / rate);
    state->beat_lp_alpha = 1 - exp(-2.0 * 3.14159265358979323846 * 160 / rate);
    return true;
}

/* Called only with decoded, normalized PCM frames. True means a completed
 * window is available. Rejected nonfinite input clears the partial history. */
static bool clearcast_rhythm_frame(clearcast_rhythm_t *state, const double *samples) {
    if (!state->rate || !samples) return false;
    double full = 0, low = 0, beat = 0;
    for (unsigned int channel = 0; channel < state->channels; ++channel) {
        double x = samples[channel];
        if (!isfinite(x)) {
            clearcast_rhythm_reset(state, state->rate, state->channels);
            return false;
        }
        x = x < -1 ? -1 : x > 1 ? 1 : x;
        state->highpass[channel] = state->hp_alpha * (state->highpass[channel] + x - state->previous[channel]);
        state->previous[channel] = x;
        state->lowpass[channel] += state->lp_alpha * (state->highpass[channel] - state->lowpass[channel]);
        full += x * x;
        low += state->lowpass[channel] * state->lowpass[channel];
        state->beat_highpass[channel] = state->beat_hp_alpha *
            (state->beat_highpass[channel] + x - state->beat_previous[channel]);
        state->beat_previous[channel] = x;
        state->beat_lowpass[channel] += state->beat_lp_alpha * (state->beat_highpass[channel] - state->beat_lowpass[channel]);
        state->beat_lowpass2[channel] += state->beat_lp_alpha * (state->beat_lowpass[channel] - state->beat_lowpass2[channel]);
        beat += state->beat_lowpass2[channel] * state->beat_lowpass2[channel];
    }
    state->sum_full += full / state->channels;
    state->sum_low += low / state->channels;
    state->sum_beat += beat / state->channels;
    ++state->frames;
    if (++state->window_used < state->window_frames) return false;

    const double full_rms = sqrt(state->sum_full / state->window_used);
    const double low_rms = sqrt(state->sum_low / state->window_used);
    const double beat_rms = sqrt(state->sum_beat / state->window_used);
    const double beat_ratio = state->sum_beat / (state->sum_full + 1e-12);
    const double beat_flux = (beat_rms - state->previous_beat) / (fmax(beat_rms, state->previous_beat) + 1e-6);
    state->beat_ready = false;
    // This independent low-band detector does not use the broadband transient
    // OR that intentionally contributes to the existing density estimator.
    if (state->beat_candidate) {
        if (beat_rms > state->beat_peak) {
            state->beat_peak = beat_rms; state->beat_full_peak = full_rms;
            state->beat_ratio = beat_ratio; state->beat_peak_frame = state->frames;
        }
        if (state->frames > state->beat_peak_frame && beat_rms < state->beat_peak * .67 &&
            full_rms < state->beat_full_peak * .85) {
            // Keep absolute low-band amplitude in the punch strength. A quiet
            // kick is not amplified to the same normalized peak as a loud one.
            double level = clearcast_rhythm_unit((state->beat_peak - .004) / .176);
            double dominance = clearcast_rhythm_unit((state->beat_ratio - .15) / .35);
            double attack = clearcast_rhythm_unit((state->beat_flux - .30) / .70);
            state->beat_strength = clearcast_rhythm_unit(level * sqrt(dominance) * (.65 + .35 * attack));
            if (state->beat_strength > .06) {
                state->beat_frame = state->beat_candidate_frame;
                state->beat_ready = true; ++state->beat_count;
            }
            state->beat_candidate = false;
        } else if (state->frames - state->beat_candidate_start >= state->rate * 8 / 100) {
            state->beat_candidate = false;
        }
    }
    const uint64_t onset_frame = state->frames - state->window_frames / 2;
    if (!state->beat_candidate && full_rms > .003 && beat_rms > .002 && beat_ratio > .20 &&
        beat_flux > .40 && beat_rms > state->mean_beat * 1.6 &&
        (!state->beat_count || onset_frame - state->beat_frame >= state->rate * 12 / 100)) {
        state->beat_candidate = true;
        state->beat_candidate_start = state->beat_peak_frame = state->frames;
        state->beat_candidate_frame = onset_frame;
        state->beat_peak = beat_rms; state->beat_full_peak = full_rms;
        state->beat_ratio = beat_ratio; state->beat_flux = beat_flux;
    }
    state->mean_beat += .04 * (beat_rms - state->mean_beat);
    state->previous_beat = beat_rms;
    const double low_flux = (low_rms - state->previous_low) / (fmax(low_rms, state->previous_low) + 1e-6);
    const double full_flux = (full_rms - state->previous_full) / (fmax(full_rms, state->previous_full) + 1e-6);
    const bool transient = full_rms > 0.0003 &&
        ((low_rms > 0.0002 && low_flux > 0.30 && low_rms > state->mean_low * 1.35) ||
         (full_flux > 0.45 && full_rms > state->mean_full * 1.8));
    const bool onset = transient && (!state->onsets || state->frames - state->last_onset_frame >= state->rate / 10);
    const double seconds = (double)state->window_used / state->rate;
    // A two-second event-rate envelope: 2,3,4 onsets/s approach .33,.50,.67.
    state->density *= exp(-seconds / 2.0);
    if (onset) {
        state->density += 1.0 / 12.0;
        state->last_onset_frame = state->frames; ++state->onsets;
    }
    state->density = clearcast_rhythm_unit(state->density);
    const double energy = clearcast_rhythm_unit((20 * log10(full_rms + 1e-12) + 60) / 50);
    state->energy += (energy > state->energy ? 0.5 : 0.15) * (energy - state->energy);
    state->mean_full += 0.08 * (full_rms - state->mean_full);
    state->mean_low += 0.08 * (low_rms - state->mean_low);
    state->previous_full = full_rms; state->previous_low = low_rms;
    state->sum_full = state->sum_low = state->sum_beat = 0; state->window_used = 0; ++state->windows;
    return true;
}
#endif
