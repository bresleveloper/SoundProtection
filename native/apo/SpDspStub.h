// SpDspStub.h - the DSP contract of native\dsp\SpDsp.h with pass-through bodies.
//
// Used only when the plug-in is built with /DSP_DSP_STUB (build-apo.cmd stub), so that the plug-in plumbing
// (COM, formats, runtime, telemetry, config, guard) can be built and tested before the real DSP exists or
// when the real DSP is not at hand. The public API below must stay identical to the contract in the plan.
//
// What the stub does and does not do:
//   - Leveler copies the audio unchanged (in == out allowed). It applies no gain and no limiting.
//   - SafetyCap delays the audio by LatencyFrames() frames and does nothing else (in == out allowed): no gain, no
//     limiting. LatencyFrames() is round(5 ms * fs) + 8, the number the real DSP reports. The delay line is there
//     so that the plug-in's handling of a look-ahead tail (what the DSP still holds when the sound stops, and what
//     Reset throws away) can be checked without the real DSP: after Prepare or Reset the first LatencyFrames()
//     output frames are zeros, and every sample comes out LatencyFrames() frames after it went in.
//   - Meters report the floor. Nothing here measures loudness.
//   - Every guarantee about the output (finite samples, Leveler within +-1.0, Safety Cap within the ceiling)
//     comes from the plug-in's own final sanitise-and-clamp, never from this stub.
#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <vector>

namespace sp {

constexpr float kFloorDb = -120.0f;

inline float DbToLinear(float db) { return std::pow(10.0f, db / 20.0f); }

inline float LinearToDb(float linear) {
    if (!(linear > 1e-6f) || !(linear < INFINITY)) return kFloorDb;  // <= 1e-6, NaN and Inf
    return 20.0f * std::log10(linear);
}

inline float CeilingDb(float capDb, float marginDb) {
    const float c = capDb + marginDb;
    return c < -1.0f ? c : -1.0f;
}

struct Biquad { double b0, b1, b2, a1, a2; };

inline void KWeighting(double /*sampleRate*/, Biquad& shelf, Biquad& highPass) {
    shelf = Biquad{ 1.0, 0.0, 0.0, 0.0, 0.0 };
    highPass = Biquad{ 1.0, 0.0, 0.0, 0.0, 0.0 };
}

inline void ChannelWeightsFromMask(uint32_t /*channelMask*/, uint32_t channels, float* weightsOut) {
    for (uint32_t i = 0; i < channels; ++i) weightsOut[i] = 1.0f;
}

class LoudnessMeter {
public:
    void Prepare(uint32_t /*sampleRate*/, uint32_t /*channels*/, const float* /*channelWeights*/) {}
    void Reset() {}
    void Add(const float* /*interleaved*/, uint32_t /*frames*/) {}
    float MomentaryLufs() const { return kFloorDb; }
};

struct LevelerParams {
    float capDb = -20.0f;
    bool enabled = true;
    float attackMs = 100.0f;
    float releaseMs = 1000.0f;
    float maxReductionDb = 40.0f;
};

class Leveler {
public:
    void Prepare(uint32_t /*sampleRate*/, uint32_t channels, const float* /*channelWeights*/) { channels_ = channels; }
    void Reset() {}
    void SetParams(const LevelerParams& /*p*/) {}
    void Process(const float* in, float* out, uint32_t frames) {
        if (in != out) std::memmove(out, in, sizeof(float) * static_cast<size_t>(frames) * channels_);
    }
    float InputLoudnessDb() const { return kFloorDb; }
    float GainReductionDb() const { return 0.0f; }

private:
    uint32_t channels_ = 0;
};

struct SafetyCapParams {
    float ceilingDb = -8.0f;
    bool enabled = true;
    float lookaheadMs = 5.0f;
    float holdMs = 20.0f;
    float releaseFastMs = 80.0f;
    float releaseSlowMs = 400.0f;
    float slowAfterMs = 200.0f;
    float relaxRampMs = 100.0f;
};

class SafetyCap {
public:
    void Prepare(uint32_t sampleRate, uint32_t channels) {
        channels_ = channels;
        latencyFrames_ = static_cast<uint32_t>(0.005 * static_cast<double>(sampleRate) + 0.5) + 8u;
        line_.assign(static_cast<size_t>(latencyFrames_) * channels_, 0.0f);  // may throw std::bad_alloc, like the real one
        pos_ = 0;
    }
    void Reset() {
        std::fill(line_.begin(), line_.end(), 0.0f);
        pos_ = 0;
    }
    void SetParams(const SafetyCapParams& /*p*/) {}
    void Process(const float* in, float* out, uint32_t frames) {
        if (line_.empty()) {  // not prepared: pass the audio on
            if (in != out) std::memmove(out, in, sizeof(float) * static_cast<size_t>(frames) * channels_);
            return;
        }
        for (uint32_t f = 0; f < frames; ++f) {
            float* slot = &line_[static_cast<size_t>(pos_) * channels_];
            const size_t base = static_cast<size_t>(f) * channels_;
            for (uint32_t c = 0; c < channels_; ++c) {
                const float x = in[base + c];  // read before the write: in == out is allowed
                out[base + c] = slot[c];
                slot[c] = x;
            }
            if (++pos_ == latencyFrames_) pos_ = 0;
        }
    }
    uint32_t LatencyFrames() const { return latencyFrames_; }
    float InputPeakDb() const { return kFloorDb; }
    float OutputPeakDb() const { return kFloorDb; }
    float GainReductionDb() const { return 0.0f; }
    uint32_t ClipperHits() const { return 0; }

private:
    uint32_t channels_ = 0;
    uint32_t latencyFrames_ = 0;
    uint32_t pos_ = 0;
    std::vector<float> line_;  // latencyFrames_ frames, interleaved: the audio that is still on its way out
};

}  // namespace sp
