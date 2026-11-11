// SpDsp.h - the DSP library of the Sound Protection plug-in. Header-only, C++17, no OS calls.
//
//   Leveler    turns a loud stream down to a loudness cap (ITU-R BS.1770 momentary loudness, slow gain).
//   SafetyCap  look-ahead true-peak limiter with a hard final clamp, one gain for all channels.
//
// Rules this library keeps (they are what protects the listener's ears):
//   * Every gain is <= 1. Nothing is ever made louder than its input.
//   * Every output sample is finite: NaN and Inf in the input become 0.
//   * Leveler output is clamped to +-1.0. SafetyCap output is clamped to +-ceiling.
//   * Process, Add and SetParams never allocate, free, lock, wait or call the OS. Only Prepare allocates
//     (and may throw std::bad_alloc, which leaves the object unprepared, see the last rule). All state is in
//     plain members.
//   * One thread at a time per object. Compile with /fp:precise (no fast-math); the code relies on IEEE
//     semantics only through its own bit test (detail::IsFinite), never through std::isfinite.
//   * in == out (in-place) is allowed in Process. Partly overlapping buffers are not.
//   * An object that is not prepared FAILS CLOSED: it never hands its input on unchecked. Prepare accepts
//     8 kHz to 768 kHz and 1 to 64 channels (limits on what it allocates). A format outside that, or a
//     std::bad_alloc, leaves the object unprepared. Every Prepare call first records the channel count it
//     was given. Until a Prepare succeeds, Process on a Leveler or SafetyCap writes frames x that many
//     samples: NaN and Inf become 0 and every other sample is clamped, with no gain and no delay, to +-1.0
//     (Leveler) or to +-the ceiling in force (SafetyCap, whose ceiling still follows SetParams).
//     SafetyCap::LatencyFrames() is 0 then, and ClipperHits() counts the samples the clamp changed. A later
//     successful Prepare returns to normal processing. Process writes nothing (a caller error: the size of
//     the buffer is unknown) when Prepare was never called, or when the last call was given 0 channels or
//     more than 65535. LoudnessMeter has no output: unprepared, Add is ignored and it reads kFloorDb.
//
// Notes on points the written contract leaves open (each one is explained again in the Task 1 report, and
// again in a comment at the place in the code where it applies; (a) to (d) are the labels used there):
//   * (a) SafetyCap slow release: the 200 ms that switch the release from "fast" to "slow" count the time the
//     limiter is actually demanding gain reduction (required gain below 0.999), not the release tail.
//     The count is kept across short gaps inside one limiting episode and restarts once the envelope has
//     recovered. A short burst therefore releases fast.
//   * (b) SafetyCap release ends in finite time: the envelope aims 0.003 beyond its target and stops on
//     arrival, so a recovered limiter is exactly unity (a pure one-pole never reaches it) and the output is
//     again an exact copy of the input, delayed by LatencyFrames.
//   * (c) A SafetyCap that has not processed any audio since Prepare/Reset takes its first ceiling at once
//     (nothing is playing, so there is nothing to ramp). Later: lower applies at once, higher ramps.
//   * (d) SafetyCap lookaheadMs and holdMs size its buffers, so they are read at Prepare; changing them in
//     SetParams afterwards only takes effect at the next Prepare. LatencyFrames never changes between
//     two Prepare calls.
//   * ClipperHits is cumulative since Prepare. Reset keeps it.

#pragma once

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <vector>

namespace sp {

constexpr float kFloorDb = -120.0f;

namespace detail {

constexpr double kPi = 3.14159265358979323846;
constexpr double kNepersPerDb = 0.11512925464970228420;   // ln(10) / 20

// Rate and channel limits that Prepare accepts (bounds on what it allocates, not audio limits).
constexpr std::uint32_t kMinRate = 8000;
constexpr std::uint32_t kMaxRate = 768000;
constexpr std::uint32_t kMaxChannels = 64;

// The largest channel count that a Prepare call which failed is still believed: Process fails closed over that many
// samples per frame. A WAVEFORMATEX channel count is 16 bits wide, so a larger one cannot describe a real buffer.
constexpr std::uint32_t kMaxFailClosedChannels = 65535;

inline bool IsFinite(float x) {
    std::uint32_t bits = 0;
    std::memcpy(&bits, &x, sizeof bits);
    return (bits & 0x7F800000u) != 0x7F800000u;
}

// NaN and +-Inf become 0.
inline float Sanitize(float x) { return IsFinite(x) ? x : 0.0f; }

inline double DbToLinD(double db) { return std::exp(db * kNepersPerDb); }

inline float ClampF(float v, float lo, float hi) { return std::min(std::max(v, lo), hi); }

inline bool RateOk(std::uint32_t rate) { return rate >= kMinRate && rate <= kMaxRate; }

// The format check of every Prepare.
inline bool FormatOk(std::uint32_t rate, std::uint32_t channels) {
    return RateOk(rate) && channels >= 1 && channels <= kMaxChannels;
}

// What a Prepare call records of the channel count it was given, whether it succeeds or not: the count itself when it
// is 1..kMaxFailClosedChannels, otherwise 0 = unknown (Process then writes nothing).
inline std::uint32_t FailClosedChannels(std::uint32_t channels) {
    return (channels >= 1 && channels <= kMaxFailClosedChannels) ? channels : 0u;
}

// Modified Bessel function of the first kind, order 0 (for the Kaiser window).
inline double BesselI0(double x) {
    const double q = 0.25 * x * x;
    double term = 1.0;
    double sum = 1.0;
    for (int k = 1; k < 120; ++k) {
        term *= q / (static_cast<double>(k) * static_cast<double>(k));
        sum += term;
        if (term < sum * 1e-17) break;
    }
    return sum;
}

// round(ms * fs / 1000), at least 0.
inline std::uint32_t MsToFrames(double ms, double fs) {
    const double v = std::floor(ms * 0.001 * fs + 0.5);
    return v <= 0.0 ? 0u : static_cast<std::uint32_t>(v);
}

// Coefficient of a one-pole smoother with time constant ms: 1 - exp(-1 / (tau * fs)). 1 means "at once".
inline double OnePoleCoef(double ms, double fs) {
    if (!(ms > 0.0)) return 1.0;
    return 1.0 - std::exp(-1.0 / (ms * 0.001 * fs));
}

// The largest float that is <= v (v > 0).
inline float FloatAtMost(double v) {
    float f = static_cast<float>(v);
    if (static_cast<double>(f) > v) f = std::nextafter(f, 0.0f);
    return f;
}

}  // namespace detail

// ------------------------------------------------------------------------------------------------
// Pure functions
// ------------------------------------------------------------------------------------------------

inline float DbToLinear(float db) {                       // 10^(db/20)
    return static_cast<float>(std::pow(10.0, static_cast<double>(db) / 20.0));
}

inline float LinearToDb(float linear) {                   // 20*log10; kFloorDb for <= 1e-6, NaN or Inf
    if (!detail::IsFinite(linear) || linear <= 1e-6f) return kFloorDb;
    return static_cast<float>(20.0 * std::log10(static_cast<double>(linear)));
}

inline float CeilingDb(float capDb, float marginDb) {    // min(capDb + marginDb, -1.0f); NaN gives -1
    const float s = capDb + marginDb;
    return (s < -1.0f) ? s : -1.0f;
}

struct Biquad { double b0, b1, b2, a1, a2; };             // a0 = 1; y = b0x + b1x1 + b2x2 - a1y1 - a2y2

// ITU-R BS.1770 K-weighting for any sample rate (the libebur128 formulas, which reproduce the standard's
// 48 kHz coefficients): stage 1 high-shelf, stage 2 high-pass.
inline void KWeighting(double sampleRate, Biquad& shelf, Biquad& highPass) {
    {
        const double f0 = 1681.974450955533;
        const double gainDb = 3.999843853973347;
        const double q = 0.7071752369554196;
        const double k = std::tan(detail::kPi * f0 / sampleRate);
        const double vh = std::pow(10.0, gainDb / 20.0);
        const double vb = std::pow(vh, 0.4996667741545416);
        const double a0 = 1.0 + k / q + k * k;
        shelf.b0 = (vh + vb * k / q + k * k) / a0;
        shelf.b1 = 2.0 * (k * k - vh) / a0;
        shelf.b2 = (vh - vb * k / q + k * k) / a0;
        shelf.a1 = 2.0 * (k * k - 1.0) / a0;
        shelf.a2 = (1.0 - k / q + k * k) / a0;
    }
    {
        const double f0 = 38.13547087602444;
        const double q = 0.5003270373238773;
        const double k = std::tan(detail::kPi * f0 / sampleRate);
        const double a0 = 1.0 + k / q + k * k;
        highPass.b0 = 1.0;
        highPass.b1 = -2.0;
        highPass.b2 = 1.0;
        highPass.a1 = 2.0 * (k * k - 1.0) / a0;
        highPass.a2 = (1.0 - k / q + k * k) / a0;
    }
}

// One weight per channel, from a WAVEFORMATEXTENSIBLE channel mask: the set bits are walked in ascending
// order, one per channel. LFE (0x8) -> 0, back and side (0x10, 0x20, 0x200, 0x400) -> 1.41, any other
// bit -> 1.0. Channels beyond the set bits, and every channel when the mask is 0, get 1.0.
inline void ChannelWeightsFromMask(std::uint32_t channelMask, std::uint32_t channels, float* weightsOut) {
    if (weightsOut == nullptr) return;
    std::uint32_t c = 0;
    for (std::uint32_t bit = 0; bit < 32 && c < channels; ++bit) {
        const std::uint32_t flag = 1u << bit;
        if ((channelMask & flag) == 0) continue;
        float w = 1.0f;
        if (flag == 0x8u) w = 0.0f;
        else if (flag == 0x10u || flag == 0x20u || flag == 0x200u || flag == 0x400u) w = 1.41f;
        weightsOut[c++] = w;
    }
    for (; c < channels; ++c) weightsOut[c] = 1.0f;
}

// ------------------------------------------------------------------------------------------------
// LoudnessMeter: BS.1770 momentary loudness (400 ms window, 10 ms blocks)
// ------------------------------------------------------------------------------------------------

class LoudnessMeter {
public:
    // May allocate (and throw std::bad_alloc). channelWeights (one per channel) may be null: every weight is then 1.0.
    // A refused format or a failed allocation leaves the meter unprepared: Add ignores its input and the reading is
    // kFloorDb. (The meter has no output to protect, so it does not fail closed like Leveler and SafetyCap.)
    void Prepare(std::uint32_t sampleRate, std::uint32_t channels, const float* channelWeights) {
        ready_ = false;
        lufs_ = kFloorDb;
        if (!detail::FormatOk(sampleRate, channels)) return;
        std::vector<double> ring(static_cast<std::size_t>(kBlocks) * channels, 0.0);
        std::vector<double> state(static_cast<std::size_t>(channels) * 4, 0.0);
        std::vector<double> acc(channels, 0.0);
        std::vector<double> weights(channels, 1.0);
        if (channelWeights != nullptr)
            for (std::uint32_t c = 0; c < channels; ++c) {
                // A real 0.0 (LFE) means "do not measure this channel". Garbage (NaN, Inf, negative) must never
                // silence a channel in the measurement, so it counts at full weight.
                const float w = channelWeights[c];
                weights[c] = (detail::IsFinite(w) && w >= 0.0f) ? static_cast<double>(w) : 1.0;
            }
        ring_.swap(ring);
        state_.swap(state);
        acc_.swap(acc);
        weights_.swap(weights);
        channels_ = channels;
        blockFrames_ = std::max<std::uint32_t>(1u, detail::MsToFrames(10.0, static_cast<double>(sampleRate)));
        KWeighting(static_cast<double>(sampleRate), shelf_, highPass_);
        ready_ = true;
        Reset();
    }

    void Reset() {
        std::fill(ring_.begin(), ring_.end(), 0.0);
        std::fill(state_.begin(), state_.end(), 0.0);
        std::fill(acc_.begin(), acc_.end(), 0.0);
        head_ = 0;
        blockPos_ = 0;
        lufs_ = kFloorDb;
    }

    // Real-time safe. Non-finite samples count as 0.
    void Add(const float* interleaved, std::uint32_t frames) {
        if (!ready_ || interleaved == nullptr) return;
        const std::size_t ch = channels_;
        for (std::uint32_t f = 0; f < frames; ++f) {
            const float* frame = interleaved + static_cast<std::size_t>(f) * ch;
            for (std::size_t c = 0; c < ch; ++c) {
                double* st = &state_[c * 4];
                const double x = static_cast<double>(detail::Sanitize(frame[c]));
                const double y1 = shelf_.b0 * x + st[0];                      // stage 1 (transposed form II)
                st[0] = shelf_.b1 * x - shelf_.a1 * y1 + st[1];
                st[1] = shelf_.b2 * x - shelf_.a2 * y1;
                const double y2 = highPass_.b0 * y1 + st[2];                  // stage 2
                st[2] = highPass_.b1 * y1 - highPass_.a1 * y2 + st[3];
                st[3] = highPass_.b2 * y1 - highPass_.a2 * y2;
                acc_[c] += y2 * y2;
            }
            if (++blockPos_ >= blockFrames_) FinishBlock();
        }
    }

    // kFloorDb until the first 10 ms block is complete, and whenever the window holds only silence.
    float MomentaryLufs() const { return lufs_; }

private:
    static constexpr std::uint32_t kBlocks = 40;   // 40 x 10 ms = 400 ms

    void FinishBlock() {
        const std::size_t ch = channels_;
        double* slot = &ring_[static_cast<std::size_t>(head_) * ch];
        for (std::size_t c = 0; c < ch; ++c) {
            slot[c] = acc_[c];
            acc_[c] = 0.0;
        }
        head_ = (head_ + 1) % kBlocks;
        blockPos_ = 0;
        // After a long silence the filter states decay into the denormal range; flush them once per block.
        for (double& s : state_)
            if (std::fabs(s) < 1e-200) s = 0.0;
        // Always divide by a full 400 ms: until the ring has filled, the reading ramps up like a true window.
        const double denom = static_cast<double>(kBlocks) * static_cast<double>(blockFrames_);
        double total = 0.0;
        for (std::size_t c = 0; c < ch; ++c) {
            double sum = 0.0;
            for (std::size_t b = 0; b < kBlocks; ++b) sum += ring_[b * ch + c];
            total += weights_[c] * (sum / denom);
        }
        if (total > 0.0) lufs_ = std::max(kFloorDb, static_cast<float>(-0.691 + 10.0 * std::log10(total)));
        else lufs_ = kFloorDb;
    }

    bool ready_ = false;
    std::uint32_t channels_ = 0;
    std::uint32_t blockFrames_ = 1;
    std::uint32_t blockPos_ = 0;
    std::uint32_t head_ = 0;
    float lufs_ = kFloorDb;
    Biquad shelf_{1.0, 0.0, 0.0, 0.0, 0.0};
    Biquad highPass_{1.0, 0.0, 0.0, 0.0, 0.0};
    std::vector<double> ring_;      // kBlocks x channels block energies
    std::vector<double> state_;     // 4 filter states per channel
    std::vector<double> acc_;       // energy of the block in progress, per channel
    std::vector<double> weights_;
};

// ------------------------------------------------------------------------------------------------
// Leveler: slow gain that holds the loudness at a cap
// ------------------------------------------------------------------------------------------------

struct LevelerParams {
    float capDb = -20.0f;
    bool enabled = true;
    float attackMs = 100.0f;
    float releaseMs = 1000.0f;
    float maxReductionDb = 40.0f;
};

class Leveler {
public:
    // May allocate (and throw std::bad_alloc). A refused format or a failed allocation leaves the Leveler unprepared:
    // Process then fails closed (see the top of this file) over the channel count given here.
    void Prepare(std::uint32_t sampleRate, std::uint32_t channels, const float* channelWeights) {
        ready_ = false;
        failClosedChannels_ = detail::FailClosedChannels(channels);   // first of all, so that a throw below keeps it
        grDb_ = 0.0;                                              // no stale telemetry behind, even if the meter throws
        gain_ = 1.0;
        gainForGr_ = 0.0;
        meter_.Prepare(sampleRate, channels, channelWeights);     // may throw std::bad_alloc
        if (!detail::FormatOk(sampleRate, channels)) return;
        fs_ = sampleRate;
        channels_ = channels;
        UpdateCoefficients();
        ready_ = true;
        Reset();
    }

    void Reset() {
        meter_.Reset();
        grDb_ = 0.0;
        gain_ = 1.0;
        gainForGr_ = 0.0;
    }

    // Real-time safe. Non-finite or out-of-range fields are ignored or clamped. Calling it again with
    // unchanged values does nothing.
    void SetParams(const LevelerParams& p) {
        const LevelerParams s = Sanitise(p, params_);
        if (Same(s, params_)) return;
        params_ = s;
        UpdateCoefficients();
    }

    // Real-time safe; in == out allowed. Unprepared: fails closed instead (see the top of this file).
    void Process(const float* in, float* out, std::uint32_t frames) {
        if (in == nullptr || out == nullptr) return;
        if (!ready_) {
            FailClosed(in, out, frames);
            return;
        }
        const std::size_t ch = channels_;
        const double cap = static_cast<double>(params_.capDb);
        const double maxReduction = static_cast<double>(params_.maxReductionDb);
        for (std::uint32_t f = 0; f < frames; ++f) {
            const float* src = in + static_cast<std::size_t>(f) * ch;
            float* dst = out + static_cast<std::size_t>(f) * ch;

            meter_.Add(src, 1);                                   // reads the whole frame before anything is written
            if (params_.enabled) {
                double target = static_cast<double>(meter_.MomentaryLufs()) - cap;
                target = std::min(std::max(target, 0.0), maxReduction);
                grDb_ += (target - grDb_) * (target > grDb_ ? attackCoef_ : releaseCoef_);
                if (target == 0.0 && grDb_ < 1e-7) grDb_ = 0.0;   // exactly unity once recovered
            } else {
                grDb_ = std::max(0.0, grDb_ - fallDbPerFrame_);   // linear fall, 40 dB per 100 ms by default
            }
            if (grDb_ != gainForGr_) {
                gain_ = std::min(1.0, detail::DbToLinD(-grDb_));
                gainForGr_ = grDb_;
            }
            const float g = static_cast<float>(gain_);            // <= 1.0f

            for (std::size_t c = 0; c < ch; ++c) {
                const float x = src[c];
                dst[c] = detail::IsFinite(x) ? detail::ClampF(x * g, -1.0f, 1.0f) : 0.0f;
            }
        }
    }

    float InputLoudnessDb() const { return meter_.MomentaryLufs(); }
    float GainReductionDb() const { return static_cast<float>(grDb_); }          // >= 0

private:
    // Process on a Leveler that is not prepared: NaN and Inf become 0, every other sample is clamped to +-1.0. No gain
    // (the meter is not running). failClosedChannels_ == 0: the size of the buffer is unknown and nothing is written.
    void FailClosed(const float* in, float* out, std::uint32_t frames) const {
        const std::size_t n = static_cast<std::size_t>(frames) * failClosedChannels_;
        for (std::size_t i = 0; i < n; ++i) {
            const float x = in[i];                                // read before out[i] is written: in == out is safe
            out[i] = detail::IsFinite(x) ? detail::ClampF(x, -1.0f, 1.0f) : 0.0f;
        }
    }

    static LevelerParams Sanitise(const LevelerParams& p, const LevelerParams& old) {
        LevelerParams s = old;
        s.enabled = p.enabled;
        if (detail::IsFinite(p.capDb)) s.capDb = detail::ClampF(p.capDb, -120.0f, 60.0f);
        if (detail::IsFinite(p.attackMs) && p.attackMs > 0.0f) s.attackMs = detail::ClampF(p.attackMs, 0.1f, 600000.0f);
        if (detail::IsFinite(p.releaseMs) && p.releaseMs > 0.0f) s.releaseMs = detail::ClampF(p.releaseMs, 0.1f, 600000.0f);
        if (detail::IsFinite(p.maxReductionDb)) s.maxReductionDb = detail::ClampF(p.maxReductionDb, 0.0f, 120.0f);
        return s;
    }
    static bool Same(const LevelerParams& a, const LevelerParams& b) {
        return a.capDb == b.capDb && a.enabled == b.enabled && a.attackMs == b.attackMs &&
               a.releaseMs == b.releaseMs && a.maxReductionDb == b.maxReductionDb;
    }
    void UpdateCoefficients() {
        if (fs_ == 0) return;
        const double fs = static_cast<double>(fs_);
        attackCoef_ = detail::OnePoleCoef(static_cast<double>(params_.attackMs), fs);
        releaseCoef_ = detail::OnePoleCoef(static_cast<double>(params_.releaseMs), fs);
        // Disabled: maxReductionDb falls in 100 ms. The floor of 1 dB keeps a tiny maximum from freezing the gain.
        fallDbPerFrame_ = std::max(1.0, static_cast<double>(params_.maxReductionDb)) / (0.1 * fs);
    }

    bool ready_ = false;
    std::uint32_t fs_ = 0;
    std::uint32_t channels_ = 0;
    std::uint32_t failClosedChannels_ = 0;   // channels of the last Prepare call (1..65535), 0 = unknown; read only while unprepared
    LevelerParams params_;
    LoudnessMeter meter_;
    double attackCoef_ = 0.0;
    double releaseCoef_ = 0.0;
    double fallDbPerFrame_ = 0.0;
    double grDb_ = 0.0;         // gain reduction, dB, >= 0
    double gain_ = 1.0;         // 10^(-grDb_/20), cached for the grDb_ in gainForGr_
    double gainForGr_ = 0.0;
};

// ------------------------------------------------------------------------------------------------
// SafetyCap: look-ahead true-peak limiter
// ------------------------------------------------------------------------------------------------

struct SafetyCapParams {
    float ceilingDb = -8.0f;
    bool enabled = true;
    float lookaheadMs = 5.0f;      // read at Prepare (interpretation d): SetParams stores it, the next Prepare applies it
    float holdMs = 20.0f;          // read at Prepare (interpretation d), like lookaheadMs
    float releaseFastMs = 80.0f;
    float releaseSlowMs = 400.0f;
    float slowAfterMs = 200.0f;
    float relaxRampMs = 100.0f;
};

class SafetyCap {
public:
    SafetyCap() { UpdateCeiling(); }

    // May allocate (and throw std::bad_alloc). Keeps the parameters from earlier SetParams calls; the ceiling state
    // restarts from them. A refused format or a failed allocation leaves the cap unprepared: Process then fails closed
    // (see the top of this file) over the channel count given here, at the ceiling that was in force before this call.
    void Prepare(std::uint32_t sampleRate, std::uint32_t channels) {
        ready_ = false;
        failClosedChannels_ = detail::FailClosedChannels(channels);   // first of all, so that a throw below keeps it
        inPeakDb_ = kFloorDb;                                     // no stale telemetry behind, however this call ends
        outPeakDb_ = kFloorDb;
        grDb_ = 0.0f;
        hits_ = 0;
        if (!detail::FormatOk(sampleRate, channels)) return;
        const double fs = static_cast<double>(sampleRate);
        // Interpretation (d): lookaheadMs and holdMs are read here, and only here. They size the delay line, the
        // sliding-minimum window and the averaging ring, and SetParams must never allocate. A later SetParams
        // stores new values, but they take effect at the next Prepare; LatencyFrames() cannot change in between.
        const std::uint32_t lookahead = detail::MsToFrames(static_cast<double>(params_.lookaheadMs), fs);
        const std::uint32_t hold = detail::MsToFrames(static_cast<double>(params_.holdMs), fs);
        const std::size_t ch = channels;
        const std::size_t window = static_cast<std::size_t>(lookahead) + hold + 1;

        // Allocate everything first; commit only when all of it succeeded.
        std::vector<float> dly((static_cast<std::size_t>(lookahead) + kGroupDelay + 1) * ch, 0.0f);
        std::vector<double> hist(ch * 32, 0.0);
        std::vector<std::uint64_t> dqIdx(window + 1, 0);
        std::vector<double> dqVal(window + 1, 1.0);
        std::vector<double> avg(static_cast<std::size_t>(lookahead) + 1, 1.0);
        dly_.swap(dly);
        hist_.swap(hist);
        dqIdx_.swap(dqIdx);
        dqVal_.swap(dqVal);
        avg_.swap(avg);

        fs_ = sampleRate;
        channels_ = channels;
        lookahead_ = lookahead;
        window_ = window;
        BuildTaps();
        UpdateTimes();
        ready_ = true;
        Reset();
    }

    // Clears every audio state: delay, filters, envelope, telemetry. The ceiling returns to its target without a
    // ramp, and the next SetParams (before audio) takes effect at once. ClipperHits keeps counting.
    void Reset() {
        std::fill(dly_.begin(), dly_.end(), 0.0f);
        std::fill(hist_.begin(), hist_.end(), 0.0);
        std::fill(avg_.begin(), avg_.end(), 1.0);
        dlyPos_ = 0;
        histPos_ = 0;
        dqHead_ = 0;
        dqCount_ = 0;
        t_ = 0;
        avgPos_ = 0;
        avgSum_ = static_cast<double>(avg_.size());
        env_ = 1.0;
        limitedFrames_ = 0;
        curGain_ = 1.0;
        fresh_ = true;
        curDb_ = targetDb_;
        ramping_ = false;
        rampStep_ = 0.0;
        UpdateCeiling();
        inPeakDb_ = kFloorDb;
        outPeakDb_ = kFloorDb;
        grDb_ = 0.0f;
    }

    // Real-time safe. Unchanged parameters do nothing (a running ramp is never restarted). A lower ceiling
    // applies at once; a higher one, or disabled (0 dBFS), ramps linearly in dB over relaxRampMs. Exceptions to
    // that rule: the first ceiling after Prepare/Reset applies at once (interpretation c, below), and lookaheadMs
    // and holdMs are stored but only read at the next Prepare (interpretation d, see Prepare). It works the same
    // while the cap is unprepared: the fail-closed clamp follows the ceiling it sets.
    void SetParams(const SafetyCapParams& p) {
        const SafetyCapParams s = Sanitise(p, params_);
        if (Same(s, params_)) return;
        params_ = s;
        UpdateTimes();
        const double target = s.enabled ? static_cast<double>(s.ceilingDb) : 0.0;
        if (fresh_) {
            // Interpretation (c): the first ceiling after Prepare (or Reset), set before any audio has gone through
            // Process, applies at once, even when it is higher than the one now in force. Nothing is playing, so
            // there is nothing to ramp, and a stream that starts with the cap disabled or set above the -8 dBFS
            // default must not be limited at the default for its first relaxRampMs. From the first Process call on,
            // the rule above applies (tighter at once, looser by ramp).
            targetDb_ = curDb_ = target;
            ramping_ = false;
            UpdateCeiling();
        } else if (target == targetDb_) {
            // the ceiling is unchanged (some other field moved): leave a running ramp alone
        } else if (target < curDb_) {                  // tighter: at once
            targetDb_ = curDb_ = target;
            ramping_ = false;
            UpdateCeiling();
        } else if (target > curDb_) {                  // looser: ramp
            targetDb_ = target;
            if (rampFrames_ == 0) {
                curDb_ = target;
                ramping_ = false;
                UpdateCeiling();
            } else {
                rampStep_ = (target - curDb_) / static_cast<double>(rampFrames_);
                ramping_ = true;
            }
        } else {
            targetDb_ = target;
            ramping_ = false;
        }
    }

    // Real-time safe; in == out allowed. Output frame n is input frame n - LatencyFrames() times the gain.
    // Unprepared: fails closed instead, with no gain and no delay (see the top of this file).
    void Process(const float* in, float* out, std::uint32_t frames) {
        if (in == nullptr || out == nullptr || frames == 0) return;
        if (!ready_) {
            FailClosed(in, out, frames);
            return;
        }
        const std::size_t ch = channels_;
        const std::size_t dlyFrames = dly_.size() / ch;
        const std::size_t dqCap = dqVal_.size();
        fresh_ = false;
        double callIn = 0.0;
        double callOut = 0.0;

        for (std::uint32_t f = 0; f < frames; ++f) {
            const float* src = in + static_cast<std::size_t>(f) * ch;
            float* dst = out + static_cast<std::size_t>(f) * ch;
            if (ramping_) AdvanceCeiling();

            // 1. Take the frame in (non-finite -> 0): into the delay line and the true-peak history. The whole
            //    input frame is read here, before any output is written, so in == out is safe.
            float* slot = &dly_[dlyPos_ * ch];
            const std::size_t w = histPos_;
            for (std::size_t c = 0; c < ch; ++c) {
                const float x = detail::Sanitize(src[c]);
                slot[c] = x;
                double* h = &hist_[c * 32];
                h[w] = static_cast<double>(x);
                h[w + 16] = static_cast<double>(x);
            }
            histPos_ = (w + 1) & 15u;

            // 2. True-peak estimate of frame k = n - G, over all channels: the sample itself and the 4x
            //    interpolated points k + 1/4, k + 2/4, k + 3/4 (3 phases x 16 taps).
            double peak = 0.0;
            for (std::size_t c = 0; c < ch; ++c) {
                const double* h = &hist_[c * 32 + w + 1];       // h[0..15] = x[n-15 .. n], oldest first
                double a0 = 0.0, a1 = 0.0, a2 = 0.0;
                for (int j = 0; j < 16; ++j) {
                    a0 += taps_[0][j] * h[j];
                    a1 += taps_[1][j] * h[j];
                    a2 += taps_[2][j] * h[j];
                }
                peak = std::max(peak, std::max(std::fabs(h[7]),
                                      std::max(std::fabs(a0), std::max(std::fabs(a1), std::fabs(a2)))));
            }
            callIn = std::max(callIn, peak);

            // 3. Gain that frame k needs under the current ceiling.
            const double r = (peak > aimC_) ? aimC_ / peak : 1.0;

            // 4. held = minimum of r over the last D + H + 1 frames (monotonic deque in a preallocated ring).
            while (dqCount_ > 0 && dqIdx_[dqHead_] + window_ <= t_) {
                dqHead_ = (dqHead_ + 1) % dqCap;
                --dqCount_;
            }
            while (dqCount_ > 0 && dqVal_[(dqHead_ + dqCount_ - 1) % dqCap] >= r) --dqCount_;
            {
                const std::size_t pos = (dqHead_ + dqCount_) % dqCap;
                dqIdx_[pos] = t_;
                dqVal_[pos] = r;
                ++dqCount_;
            }
            const double held = dqVal_[dqHead_];
            ++t_;

            // 5. Envelope: down at once, up with a one-pole release (fast, or slow after sustained limiting)
            //    that ends in finite time.
            //
            //    Interpretation (b): the release aims kReleaseOvershoot past its target and is cut off at the target
            //    by the "if (env_ > held)" line below, so the envelope lands exactly on it after finite time instead
            //    of creeping towards it for ever as a plain one-pole does. Once nothing is demanding reduction the envelope is exactly
            //    1.0, so the gain returns to 1.0 and the output is again an exact copy of the input, delayed by
            //    LatencyFrames(). The time constant is unchanged to within that margin.
            if (held < env_) {
                env_ = held;
            } else if (held > env_) {
                env_ += (held - env_ + kReleaseOvershoot) * (limitedFrames_ > slowAfterFrames_ ? relSlow_ : relFast_);
                if (env_ > held) env_ = held;
            }
            // Interpretation (a): the slow-release timer limitedFrames_ counts the frames that demand reduction
            // (held, the required gain over the look-ahead + hold window, is below kEngaged), NOT the release tail.
            // The frames in which the envelope is merely climbing back (held at or above kEngaged, env_ still below)
            // add nothing. The count is kept across short gaps inside one limiting episode and is cleared only
            // once the envelope is back at kEngaged or above. A short burst therefore releases fast, and more than
            // slowAfterMs of limiting in one episode arms the slow release.
            if (held < kEngaged) {
                if (limitedFrames_ < 0x7FFFFFFFu) ++limitedFrames_;
            } else if (env_ >= kEngaged) {
                limitedFrames_ = 0;
            }

            // 6. Gain: moving average of the envelope over the last D + 1 frames (running sum, exact resync at
            //    every wrap of the ring).
            avgSum_ += env_ - avg_[avgPos_];
            avg_[avgPos_] = env_;
            if (++avgPos_ >= avg_.size()) {
                avgPos_ = 0;
                double sum = 0.0;
                for (const double v : avg_) sum += v;
                avgSum_ = sum;
            }
            double gain = avgSum_ / static_cast<double>(avg_.size());
            gain = std::min(1.0, std::max(0.0, gain));
            curGain_ = gain;

            // 7. Output: the frame delayed by D + G, times the gain, then the final clamp.
            const float* old = &dly_[((dlyPos_ + 1) % dlyFrames) * ch];
            for (std::size_t c = 0; c < ch; ++c) {
                float y = static_cast<float>(static_cast<double>(old[c]) * gain);
                if (y > clampC_) {
                    y = clampC_;
                    if (hits_ != 0xFFFFFFFFu) ++hits_;
                } else if (y < -clampC_) {
                    y = -clampC_;
                    if (hits_ != 0xFFFFFFFFu) ++hits_;
                }
                dst[c] = y;
                callOut = std::max(callOut, static_cast<double>(std::fabs(y)));
            }
            dlyPos_ = (dlyPos_ + 1) % dlyFrames;
        }

        // The estimate can sit above the float range when the input is garbage near FLT_MAX; it then reads as FLT_MAX
        // (a huge, finite number of dB) instead of overflowing the conversion.
        inPeakDb_ = LinearToDb(static_cast<float>(std::min(callIn, static_cast<double>(std::numeric_limits<float>::max()))));
        outPeakDb_ = LinearToDb(static_cast<float>(callOut));
        const double grDb = curGain_ >= 1.0 ? 0.0 : (curGain_ <= 1e-6 ? 120.0 : -20.0 * std::log10(curGain_));
        grDb_ = grDb < 1e-4 ? 0.0f : static_cast<float>(grDb);
    }

    // lookahead + true-peak filter delay; 0 while unprepared (the fail-closed clamp adds no delay). Fixed between two
    // Prepare calls (lookaheadMs is read at Prepare, interpretation d).
    std::uint32_t LatencyFrames() const { return ready_ ? lookahead_ + kGroupDelay : 0u; }
    float InputPeakDb() const { return inPeakDb_; }       // largest input true-peak estimate in the last Process call
                                                          // (unprepared: the largest finite input sample, no estimate)
    float OutputPeakDb() const { return outPeakDb_; }     // largest |output| in the last Process call
    float GainReductionDb() const { return grDb_; }       // current, >= 0 (unprepared: 0, there is no gain)
    std::uint32_t ClipperHits() const { return hits_; }   // cumulative samples changed by the final clamp
                                                          // (unprepared: by the fail-closed clamp, counted from Prepare)

private:
    static constexpr std::uint32_t kGroupDelay = 8;       // true-peak filter delay, frames
    static constexpr double kAim = 1.0 - 1e-5;            // the limiter aims 0.00009 dB under the ceiling, so float rounding never trips the clamp
    static constexpr double kEngaged = 0.999;             // below this, the limiter counts as limiting (interpretation a)
    static constexpr double kReleaseOvershoot = 0.003;    // release aims this far past its target so that it lands on it (interpretation b)

    // Process on a cap that is not prepared: NaN and Inf become 0 and every other sample is clamped to +-the ceiling
    // in force. No gain, no delay, no look-ahead (the buffers of a failed Prepare are not trusted). The ceiling in force
    // follows SetParams as a prepared cap's does (lower at once, higher by the ramp, whose length is the one of the
    // last successful Prepare). failClosedChannels_ == 0: the size of the buffer is unknown and nothing is written.
    void FailClosed(const float* in, float* out, std::uint32_t frames) {
        const std::size_t ch = failClosedChannels_;
        if (ch == 0) return;
        fresh_ = false;                                       // audio has gone through: a higher ceiling now ramps (interpretation c)
        double callIn = 0.0;                                  // largest finite |input sample|: a sample peak, not a true-peak estimate
        double callOut = 0.0;
        for (std::uint32_t f = 0; f < frames; ++f) {
            if (ramping_) AdvanceCeiling();
            const float* src = in + static_cast<std::size_t>(f) * ch;
            float* dst = out + static_cast<std::size_t>(f) * ch;
            for (std::size_t c = 0; c < ch; ++c) {
                float y = detail::Sanitize(src[c]);           // read before dst[c] is written: in == out is safe
                callIn = std::max(callIn, static_cast<double>(std::fabs(y)));
                if (y > clampC_) {
                    y = clampC_;
                    if (hits_ != 0xFFFFFFFFu) ++hits_;
                } else if (y < -clampC_) {
                    y = -clampC_;
                    if (hits_ != 0xFFFFFFFFu) ++hits_;
                }
                dst[c] = y;
                callOut = std::max(callOut, static_cast<double>(std::fabs(y)));
            }
        }
        inPeakDb_ = LinearToDb(static_cast<float>(callIn));
        outPeakDb_ = LinearToDb(static_cast<float>(callOut));
    }

    static SafetyCapParams Sanitise(const SafetyCapParams& p, const SafetyCapParams& old) {
        SafetyCapParams s = old;
        s.enabled = p.enabled;
        if (detail::IsFinite(p.ceilingDb)) s.ceilingDb = detail::ClampF(p.ceilingDb, -100.0f, 0.0f);
        // lookaheadMs and holdMs are only stored here; Prepare reads them (interpretation d).
        if (detail::IsFinite(p.lookaheadMs)) s.lookaheadMs = detail::ClampF(p.lookaheadMs, 0.0f, 50.0f);
        if (detail::IsFinite(p.holdMs)) s.holdMs = detail::ClampF(p.holdMs, 0.0f, 500.0f);
        if (detail::IsFinite(p.releaseFastMs) && p.releaseFastMs > 0.0f) s.releaseFastMs = detail::ClampF(p.releaseFastMs, 0.1f, 60000.0f);
        if (detail::IsFinite(p.releaseSlowMs) && p.releaseSlowMs > 0.0f) s.releaseSlowMs = detail::ClampF(p.releaseSlowMs, 0.1f, 60000.0f);
        if (detail::IsFinite(p.slowAfterMs)) s.slowAfterMs = detail::ClampF(p.slowAfterMs, 0.0f, 60000.0f);
        if (detail::IsFinite(p.relaxRampMs)) s.relaxRampMs = detail::ClampF(p.relaxRampMs, 0.0f, 10000.0f);
        return s;
    }
    static bool Same(const SafetyCapParams& a, const SafetyCapParams& b) {
        return a.ceilingDb == b.ceilingDb && a.enabled == b.enabled && a.lookaheadMs == b.lookaheadMs &&
               a.holdMs == b.holdMs && a.releaseFastMs == b.releaseFastMs && a.releaseSlowMs == b.releaseSlowMs &&
               a.slowAfterMs == b.slowAfterMs && a.relaxRampMs == b.relaxRampMs;
    }

    // 3 interpolation phases (1/4, 2/4, 3/4) x 16 taps of Kaiser-windowed sinc (beta 8), each phase normalised to a
    // sum of 1. Tap j reads x[k - 7 + j]; the newest sample needed is x[k + 8], hence the group delay of 8.
    void BuildTaps() {
        const double beta = 8.0;
        const double halfWidth = 8.0;
        const double i0Beta = detail::BesselI0(beta);
        for (int phase = 0; phase < 3; ++phase) {
            const double d = static_cast<double>(phase + 1) / 4.0;
            double h[16] = {};
            double sum = 0.0;
            for (int j = 0; j < 16; ++j) {
                const double u = static_cast<double>(j) - 7.0 - d;        // tap position relative to k + d
                const double rel = u / halfWidth;
                const double win = detail::BesselI0(beta * std::sqrt(std::max(0.0, 1.0 - rel * rel))) / i0Beta;
                h[j] = std::sin(detail::kPi * u) / (detail::kPi * u) * win;
                sum += h[j];
            }
            for (int j = 0; j < 16; ++j) taps_[phase][j] = h[j] / sum;
        }
    }

    void UpdateTimes() {
        if (fs_ == 0) return;
        const double fs = static_cast<double>(fs_);
        relFast_ = detail::OnePoleCoef(static_cast<double>(params_.releaseFastMs), fs);
        relSlow_ = detail::OnePoleCoef(static_cast<double>(params_.releaseSlowMs), fs);
        slowAfterFrames_ = detail::MsToFrames(static_cast<double>(params_.slowAfterMs), fs);
        rampFrames_ = detail::MsToFrames(static_cast<double>(params_.relaxRampMs), fs);
    }

    // Recomputes the values derived from the current ceiling.
    void UpdateCeiling() {
        const double c = detail::DbToLinD(curDb_);
        aimC_ = c * kAim;
        clampC_ = detail::FloatAtMost(c);
    }

    void AdvanceCeiling() {
        curDb_ += rampStep_;
        if (curDb_ >= targetDb_) {
            curDb_ = targetDb_;
            ramping_ = false;
        }
        UpdateCeiling();
    }

    bool ready_ = false;
    std::uint32_t fs_ = 0;
    std::size_t channels_ = 0;
    std::uint32_t failClosedChannels_ = 0; // channels of the last Prepare call (1..65535), 0 = unknown; read only while unprepared
    std::uint32_t lookahead_ = 0;          // D, frames; set by Prepare only (interpretation d)
    std::size_t window_ = 1;               // D + H + 1, frames
    SafetyCapParams params_;

    // coefficients
    double relFast_ = 0.0;
    double relSlow_ = 0.0;
    std::uint32_t slowAfterFrames_ = 0;
    std::uint32_t rampFrames_ = 0;
    double taps_[3][16] = {};

    // ceiling
    double targetDb_ = -8.0;               // where the ceiling is heading (dBFS)
    double curDb_ = -8.0;                  // the ceiling in force
    double rampStep_ = 0.0;
    bool ramping_ = false;
    bool fresh_ = true;                    // no audio since Prepare/Reset: the next SetParams ceiling applies at once (interpretation c)
    double aimC_ = 0.0;                   // the linear ceiling times kAim: what the detector aims for
    float clampC_ = 0.0f;                  // the largest float <= the linear ceiling: the final clamp

    // audio state
    std::vector<float> dly_;               // (D + G + 1) frames
    std::size_t dlyPos_ = 0;
    std::vector<double> hist_;             // per channel: the last 16 input samples, stored twice (stride 32)
    std::size_t histPos_ = 0;
    std::vector<std::uint64_t> dqIdx_;     // monotonic deque: frame numbers
    std::vector<double> dqVal_;            // monotonic deque: required gains
    std::size_t dqHead_ = 0;
    std::size_t dqCount_ = 0;
    std::uint64_t t_ = 0;                  // frames processed since Prepare/Reset
    std::vector<double> avg_;              // last D + 1 envelope values
    std::size_t avgPos_ = 0;
    double avgSum_ = 0.0;
    double env_ = 1.0;
    std::uint32_t limitedFrames_ = 0;      // frames demanding reduction (held below kEngaged), counted since the envelope last
                                           // recovered; the release tail does not count (interpretation a)
    double curGain_ = 1.0;

    // telemetry
    float inPeakDb_ = kFloorDb;
    float outPeakDb_ = kFloorDb;
    float grDb_ = 0.0f;
    std::uint32_t hits_ = 0;
};

}  // namespace sp
