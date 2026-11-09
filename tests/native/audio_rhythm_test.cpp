// Synthetic PCM only; no device, network, files or retained audio.
// SPDX-License-Identifier: GPL-3.0-or-later
#include "clearcast_audio_rhythm.h"
#include <cstdio>
#include <limits>
#include <vector>
#include <chrono>

static int checks = 0, failures = 0;
static void check(bool value, const char *name) {
    ++checks;
    if (!value) { ++failures; fprintf(stderr, "FAIL: %s\n", name); }
}
struct Metric { double energy = 0, density = 0, beat_strength = 0; uint64_t onsets = 0, beats = 0; };
static double wave(double time, double frequency, int type) {
    constexpr double pi = 3.14159265358979323846;
    if (type == 0) return 0;
    if (type == 1) return sin(2 * pi * 80 * time);
    if (type == 2) return sin(2 * pi * 440 * time);
    if (type == 6) {
        uint32_t value=uint32_t(time*44100)*1664525u+1013904223u;
        value^=value>>13;value*=2246822519u;
        return (double(value)/4294967295.0)*2-1;
    }
    if (type == 7) {
        double phase=fmod(time,.5), envelope=phase<.12?exp(-phase/.04):0;
        return envelope*(.3*sin(2*pi*120*time)+.7*sin(2*pi*700*time)+.6*sin(2*pi*1300*time));
    }
    if (type == 3 || type == 5) {
        double phase = fmod(time, 1 / frequency);
        return phase < .1 ? exp(-phase / .025) * sin(2 * pi * (type == 5 ? 1600 : 80) * phase) : 0;
    }
    // A large, slow gain change must not act like a dense pulse train.
    return sin(2 * pi * 80 * time) * (time < 12 ? .2 : 1);
}
static Metric measure(double frequency, int type, double gain = 1, unsigned int rate = 44100, bool antiphase = false) {
    clearcast_rhythm_t state;
    check(clearcast_rhythm_reset(&state, rate, 2), "supported PCM setup");
    const uint64_t frames = uint64_t(rate) * 24;
    double power = 0;
    for (uint64_t frame = 0; frame < frames; ++frame) {
        double value = wave(double(frame) / rate, frequency, type);
        power += value * value;
    }
    double scale = power > 0 ? .05 * sqrt(double(frames) / power) * gain : 0;
    Metric result;
    unsigned int windows = 0;
    bool bounded = true;
    for (uint64_t frame = 0; frame < frames; ++frame) {
        double sample = wave(double(frame) / rate, frequency, type) * scale;
        double channels[] = {sample, antiphase ? -sample : sample};
        if (clearcast_rhythm_frame(&state, channels)) {
            if (state.beat_ready) { ++result.beats; result.beat_strength += state.beat_strength; }
            bounded &= isfinite(state.energy) && isfinite(state.density) &&
                state.energy >= 0 && state.energy <= 1 && state.density >= 0 && state.density <= 1;
            if (frame > uint64_t(rate) * 14) { result.energy += state.energy; result.density += state.density; ++windows; }
        }
    }
    check(bounded && windows > 400, "every completed PCM window is finite and normalized");
    result.energy /= windows; result.density /= windows; result.onsets = state.onsets;
    if (result.beats) result.beat_strength /= result.beats;
    return result;
}
int main() {
    auto start = std::chrono::steady_clock::now();
    const auto silence = measure(0, 0), sustained = measure(0, 1), soft = measure(0, 2, .1);
    const auto bpm120 = measure(2, 3), bpm180 = measure(3, 3), eighths = measure(4, 3), dense = measure(6, 3);
    const auto quiet = measure(2, 3, .25), loud = measure(2, 3, 2), stepped = measure(0, 4);
    const auto reverse = measure(2, 3, 1, 44100, true), rate48 = measure(2, 3, 1, 48000);
    const auto high = measure(3,5), double_kick = measure(8,3);
    const auto noise=measure(0,6,.001), harmonic_voice=measure(0,7);
    check(silence.energy == 0 && silence.density == 0 && silence.onsets == 0, "silence has no invented activity");
    check(bpm120.density > .20 && bpm180.density > bpm120.density + .05 && eighths.density > bpm180.density + .05 &&
        dense.density > eighths.density + .05, "equal-RMS rhythm density increases for 120,180,eighths and dense pulses");
    check(sustained.density < bpm120.density * .25 && soft.density < bpm120.density * .25,
        "steady loud or gentle tones do not invent repeated percussion");
    check(fabs(quiet.density - bpm120.density) < .03 && fabs(loud.density - bpm120.density) < .03,
        "pulse density is robust to input gain changes");
    check(soft.energy < sustained.energy && quiet.energy < bpm120.energy && loud.energy > bpm120.energy,
        "energy still reflects PCM loudness separately from density");
    check(stepped.density < bpm120.density * .25, "a large volume step does not resemble a dense rhythm");
    check(fabs(reverse.density - bpm120.density) < .001 && fabs(reverse.energy - bpm120.energy) < .001,
        "opposite-polarity stereo cannot cancel the analyzer");
    check(fabs(rate48.density - bpm120.density) < .03, "44.1kHz and48kHz rhythm comparisons agree");
    check(bpm120.beats >= 46 && bpm180.beats >= 69 && eighths.beats >= 93,
        "low-frequency transient beats follow actual120/180BPM and eighth-note kicks");
    check(quiet.beats >= 46 && quiet.beat_strength + .10 < bpm120.beat_strength &&
        bpm120.beat_strength < loud.beat_strength && quiet.beat_strength < bpm120.beat_strength * .5,
        "soft,normal andstrong kicks retain amplitude-dependent finite strengths");
    check(!silence.beats && !sustained.beats && !soft.beats && !stepped.beats && !high.beats,
        "silence,sustained bass,high-frequency andgain steps do not emit heavy beats");
    check(double_kick.beats >= 170, "dense125ms double-kick pulses survive the beat refractory interval");
    check(!noise.beats&&!harmonic_voice.beats,"noise-floor andsynthetic harmonic vocal attacks do not emit low-frequency heavy beats");
    check(reverse.beats == bpm120.beats && fabs(reverse.beat_strength - bpm120.beat_strength) < .001,
        "heavy-beat detection is invariant to opposite-polarity stereo");
    clearcast_rhythm_t state;
    check(!clearcast_rhythm_reset(&state, 0, 2) && !clearcast_rhythm_reset(&state, 44100, 0) &&
        !clearcast_rhythm_reset(&state, 44100, 9), "unsupported PCM setup fails closed");
    clearcast_rhythm_reset(&state, 44100, 2);
    double invalid[] = {std::numeric_limits<double>::quiet_NaN(), 0};
    check(!clearcast_rhythm_frame(&state, invalid) && state.frames == 0 && state.density == 0,
        "nonfinite samples clear rather than poison the state");
    const double milliseconds = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
    printf("{\"checks\":%d,\"passed\":%s,\"syntheticSeconds\":384,\"elapsedMilliseconds\":%.3f,"
        "\"silence\":%.5f,\"sustained\":%.5f,\"soft\":%.5f,\"bpm120\":%.5f,\"bpm180\":%.5f,"
        "\"eighths\":%.5f,\"dense\":%.5f,\"quiet120\":%.5f,\"loud120\":%.5f,\"gainStep\":%.5f,"
        "\"beats120\":%llu,\"beats180\":%llu,\"doubleKick\":%llu,\"quietStrength\":%.5f,\"normalStrength\":%.5f,\"loudStrength\":%.5f,\"highFrequencyBeats\":%llu}\n",
        checks, failures ? "false" : "true", milliseconds, silence.density, sustained.density, soft.density,
        bpm120.density, bpm180.density, eighths.density, dense.density, quiet.density, loud.density, stepped.density,
        (unsigned long long)bpm120.beats,(unsigned long long)bpm180.beats,(unsigned long long)double_kick.beats,
        quiet.beat_strength,bpm120.beat_strength,loud.beat_strength,(unsigned long long)high.beats);
    return failures ? 1 : 0;
}
