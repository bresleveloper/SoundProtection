// DspTests.cpp - native tests for native\dsp\SpDsp.h (the Sound Protection DSP library).
//
// Self-contained runner, no framework. For each of the 27 tests it prints "PASS name" or "FAIL name: message",
// then "ALL 27 PASSED" (exit code 0) or "FAILED: n of 27 tests" (exit code 1).
//
// Real-time guard: this file replaces the global operator new/delete with counting versions. Every call into the
// library's real-time surface (Process, Add, SetParams) runs inside a NoAlloc scope, and a heap allocation or
// free inside such a scope fails the running test. Prepare() is the only call allowed to allocate. The same
// operator new can also be told to fail one allocation (g_failAllocIn): the only way to see what Prepare does when
// it runs out of memory (test 24, part E).
//
// Signals are 48 kHz, stereo, identical channels, unless a test says otherwise.

#include "SpDsp.h"

#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <new>
#include <vector>

using std::size_t;
using std::uint32_t;

// ================================================================================================
// Real-time guard: counting operator new / delete
// ================================================================================================

static bool g_counting = false;               // true only inside a NoAlloc scope
static unsigned long g_heapEvents = 0;        // allocations and frees seen in the current scope
static unsigned long g_rtViolations = 0;      // events seen in all scopes of the running test
static long g_failAllocIn = -1;               // >= 0: the allocation made after this many more allocations throws bad_alloc (once)

void* operator new(std::size_t size) {
    if (g_counting) ++g_heapEvents;
    if (g_failAllocIn >= 0 && g_failAllocIn-- == 0) throw std::bad_alloc();
    void* p = std::malloc(size != 0 ? size : 1);
    if (p == nullptr) throw std::bad_alloc();
    return p;
}
void operator delete(void* p) noexcept {
    if (g_counting && p != nullptr) ++g_heapEvents;
    std::free(p);
}
void operator delete(void* p, std::size_t) noexcept {
    if (g_counting && p != nullptr) ++g_heapEvents;
    std::free(p);
}

struct NoAlloc {
    NoAlloc() { g_heapEvents = 0; g_counting = true; }
    ~NoAlloc() { g_counting = false; g_rtViolations += g_heapEvents; }
};

// ================================================================================================
// Check macros
// ================================================================================================

static const int kMaxMessages = 6;
static char g_messages[kMaxMessages][600];
static int g_failCount = 0;                   // failed checks in the running test

static void Fail(int line, const char* fmt, ...) {
    if (g_failCount < kMaxMessages) {
        char body[480];
        va_list ap;
        va_start(ap, fmt);
        std::vsnprintf(body, sizeof body, fmt, ap);
        va_end(ap);
        std::snprintf(g_messages[g_failCount], sizeof g_messages[0], "(line %d) %s", line, body);
    }
    ++g_failCount;
}

#define CHECK(cond, ...) do { if (!(cond)) Fail(__LINE__, __VA_ARGS__); } while (0)

static bool Near(double a, double b, double tol) { return std::fabs(a - b) <= tol; }

// ================================================================================================
// Signal helpers
// ================================================================================================

static const double kPi = 3.14159265358979323846;
static const double kTwoPi = 6.28318530717958647692;

static size_t Frames(double seconds, double fs) { return static_cast<size_t>(seconds * fs + 0.5); }

static std::vector<float> Zeros(size_t frames, uint32_t ch) { return std::vector<float>(frames * ch, 0.0f); }

static void Append(std::vector<float>& a, const std::vector<float>& b) { a.insert(a.end(), b.begin(), b.end()); }

// freq Hz sine of the given amplitude, the same value on every channel. startFrame keeps the phase continuous
// across separately generated pieces.
static std::vector<float> Sine(double fs, uint32_t ch, double freq, double amp, size_t frames,
                               double phase = 0.0, size_t startFrame = 0) {
    std::vector<float> v(frames * ch);
    for (size_t i = 0; i < frames; ++i) {
        const float x = static_cast<float>(
            amp * std::sin(kTwoPi * freq * static_cast<double>(i + startFrame) / fs + phase));
        for (uint32_t c = 0; c < ch; ++c) v[i * ch + c] = x;
    }
    return v;
}

// Fixed-seed linear congruential generator, uniform in [0, 1).
struct Lcg {
    uint32_t s;
    explicit Lcg(uint32_t seed) : s(seed) {}
    double Next() {
        s = s * 1664525u + 1013904223u;
        return static_cast<double>(s >> 8) * (1.0 / 16777216.0);
    }
};

// Noise whose level jumps every segSeconds to a random value between -60 and 0 dBFS (identical channels).
static std::vector<float> SteppedNoise(double fs, uint32_t ch, double seconds, double segSeconds, uint32_t seed) {
    const size_t frames = Frames(seconds, fs);
    const size_t seg = Frames(segSeconds, fs);
    std::vector<float> v(frames * ch);
    Lcg rng(seed);
    double amp = 0.0;
    for (size_t i = 0; i < frames; ++i) {
        if (i % seg == 0) amp = std::pow(10.0, (-60.0 + 60.0 * rng.Next()) / 20.0);
        const float x = static_cast<float>(amp * (2.0 * rng.Next() - 1.0));
        for (uint32_t c = 0; c < ch; ++c) v[i * ch + c] = x;
    }
    return v;
}

// Uniform white noise, an independent sequence on every channel.
static std::vector<float> WhiteNoise(size_t frames, uint32_t ch, double amp, uint32_t seed) {
    Lcg rng(seed);
    std::vector<float> v(frames * ch);
    for (float& s : v) s = static_cast<float>(amp * (2.0 * rng.Next() - 1.0));
    return v;
}

// A programme with loud and quiet parts: 250 ms segments of white noise (independent on every channel) whose
// amplitude cycles through a fixed list, so the processors are certain to have work to do.
static std::vector<float> Programme(double fs, uint32_t ch, double seconds, uint32_t seed) {
    static const double amps[] = {0.002, 0.9, 0.05, 0.9, 0.4, 0.01, 0.7, 0.9};
    const size_t frames = Frames(seconds, fs);
    const size_t seg = Frames(0.250, fs);
    Lcg rng(seed);
    std::vector<float> v(frames * ch);
    for (size_t i = 0; i < frames; ++i) {
        const double a = amps[(i / seg) % (sizeof amps / sizeof amps[0])];
        for (uint32_t c = 0; c < ch; ++c) v[i * ch + c] = static_cast<float>(a * (2.0 * rng.Next() - 1.0));
    }
    return v;
}

// A sound that starts at once, with no fade-in: silence, then a burst of the given shape, then silence again.
//   0: 1 kHz cosine, amplitude 0.891 (the very first burst sample is already at the crest)
//   1: DC step to 0.9
//   2: 2 kHz square wave, +-0.95
//   3: square wave at half the sample rate, +-0.95 (the sign flips on every sample)
//   4: a single sample of 0.9 (one isolated peak, nothing else in the burst)
static const int kAbruptShapes = 5;
static std::vector<float> AbruptBurst(int shape, uint32_t ch, size_t silence, size_t burst, size_t tail) {
    std::vector<float> v((silence + burst + tail) * ch, 0.0f);
    for (size_t i = 0; i < burst; ++i) {
        double x = 0.0;
        switch (shape) {
            case 0: x = 0.891 * std::sin(kTwoPi * 1000.0 * static_cast<double>(i) / 48000.0 + kPi / 2.0); break;
            case 1: x = 0.9; break;
            case 2: x = ((i / 12) % 2 == 0) ? 0.95 : -0.95; break;
            case 3: x = (i % 2 == 0) ? 0.95 : -0.95; break;
            default: x = (i == 0) ? 0.9 : 0.0; break;
        }
        for (uint32_t c = 0; c < ch; ++c) v[(silence + i) * ch + c] = static_cast<float>(x);
    }
    return v;
}

// True when some sample above minIn comes out smaller than factor times its (delayed) input: proof that the
// processor really did reduce something, so a "never louder than the input" check cannot pass vacuously.
static bool AnyReduced(const std::vector<float>& in, const std::vector<float>& out, size_t delayFrames, uint32_t ch,
                       double minIn, double factor) {
    const size_t frames = in.size() / ch;
    for (size_t n = delayFrames; n < frames; ++n)
        for (uint32_t c = 0; c < ch; ++c) {
            const double x = std::fabs(static_cast<double>(in[(n - delayFrames) * ch + c]));
            if (x > minIn && std::fabs(static_cast<double>(out[n * ch + c])) < factor * x) return true;
        }
    return false;
}

static double MaxAbsDiff(const std::vector<float>& a, const std::vector<float>& b) {
    if (a.size() != b.size()) return 1e30;
    double worst = 0.0;
    for (size_t i = 0; i < a.size(); ++i) worst = std::max(worst, std::fabs(static_cast<double>(a[i]) - b[i]));
    return worst;
}

static bool AllFinite(const std::vector<float>& v) {
    for (float x : v) if (!std::isfinite(x)) return false;
    return true;
}

static double PeakAbs(const std::vector<float>& v, size_t fromIndex = 0, size_t toIndex = ~static_cast<size_t>(0)) {
    double peak = 0.0;
    toIndex = std::min(toIndex, v.size());
    for (size_t i = fromIndex; i < toIndex; ++i) peak = std::max(peak, static_cast<double>(std::fabs(v[i])));
    return peak;
}

// ================================================================================================
// Drivers: every real-time call runs inside a NoAlloc scope
// ================================================================================================

static void MeterFeed(sp::LoudnessMeter& m, const std::vector<float>& s, uint32_t ch, size_t fromFrame,
                      size_t frames) {
    const size_t total = s.size() / ch;
    if (fromFrame + frames > total) frames = total - fromFrame;
    for (size_t pos = 0; pos < frames; pos += 480) {
        const size_t n = std::min<size_t>(480, frames - pos);
        NoAlloc guard;
        m.Add(s.data() + (fromFrame + pos) * ch, static_cast<uint32_t>(n));
    }
}
static void MeterFeedAll(sp::LoudnessMeter& m, const std::vector<float>& s, uint32_t ch) {
    MeterFeed(m, s, ch, 0, s.size() / ch);
}

static void LvSet(sp::Leveler& lv, const sp::LevelerParams& p) {
    NoAlloc guard;
    lv.SetParams(p);
}
// Appends the processed signal to out.
static void LvRun(sp::Leveler& lv, const std::vector<float>& in, uint32_t ch, std::vector<float>& out,
                  size_t chunk = 480) {
    const size_t frames = in.size() / ch;
    const size_t base = out.size();
    out.resize(base + in.size());
    for (size_t pos = 0; pos < frames; pos += chunk) {
        const size_t n = std::min(chunk, frames - pos);
        NoAlloc guard;
        lv.Process(in.data() + pos * ch, out.data() + base + pos * ch, static_cast<uint32_t>(n));
    }
}
static void LvRunInPlace(sp::Leveler& lv, std::vector<float>& buf, uint32_t ch, size_t chunk = 480) {
    const size_t frames = buf.size() / ch;
    for (size_t pos = 0; pos < frames; pos += chunk) {
        const size_t n = std::min(chunk, frames - pos);
        NoAlloc guard;
        lv.Process(buf.data() + pos * ch, buf.data() + pos * ch, static_cast<uint32_t>(n));
    }
}
static double LoudnessOf(const std::vector<float>& s, uint32_t fs, uint32_t ch) {
    sp::LoudnessMeter m;
    m.Prepare(fs, ch, nullptr);
    MeterFeedAll(m, s, ch);
    return m.MomentaryLufs();
}

static void CapSet(sp::SafetyCap& cap, const sp::SafetyCapParams& p) {
    NoAlloc guard;
    cap.SetParams(p);
}
static void CapInit(sp::SafetyCap& cap, uint32_t fs, uint32_t ch, float ceilingDb, bool enabled = true) {
    cap.Prepare(fs, ch);
    sp::SafetyCapParams p;
    p.ceilingDb = ceilingDb;
    p.enabled = enabled;
    CapSet(cap, p);
}
// Appends the processed signal to out.
static void CapRun(sp::SafetyCap& cap, const std::vector<float>& in, uint32_t ch, std::vector<float>& out,
                   size_t chunk = 480) {
    const size_t frames = in.size() / ch;
    const size_t base = out.size();
    out.resize(base + in.size());
    for (size_t pos = 0; pos < frames; pos += chunk) {
        const size_t n = std::min(chunk, frames - pos);
        NoAlloc guard;
        cap.Process(in.data() + pos * ch, out.data() + base + pos * ch, static_cast<uint32_t>(n));
    }
}
static void CapRunInPlace(sp::SafetyCap& cap, std::vector<float>& buf, uint32_t ch, size_t chunk = 480) {
    const size_t frames = buf.size() / ch;
    for (size_t pos = 0; pos < frames; pos += chunk) {
        const size_t n = std::min(chunk, frames - pos);
        NoAlloc guard;
        cap.Process(buf.data() + pos * ch, buf.data() + pos * ch, static_cast<uint32_t>(n));
    }
}

// ================================================================================================
// An independent true-peak meter for test 17: 16x oversampling, 64 taps per phase of Kaiser-windowed sinc
// (beta 8). It shares no code with the library, so it can catch the library's estimator being wrong.
// ================================================================================================

static double BesselI0Ref(double x) {
    double sum = 1.0, term = 1.0;
    for (int k = 1; k < 80; ++k) {
        term *= (x * x / 4.0) / (static_cast<double>(k) * k);
        sum += term;
    }
    return sum;
}

class TruePeak16 {
public:
    TruePeak16() {
        const double beta = 8.0, half = 32.0;
        for (int ph = 1; ph < 16; ++ph) {
            const double d = ph / 16.0;
            double sum = 0.0;
            for (int j = 0; j < 64; ++j) {
                const double u = d + 31.0 - j;                       // tap j reads x[k - 31 + j]
                const double r = u / half;
                const double w = BesselI0Ref(beta * std::sqrt(std::max(0.0, 1.0 - r * r))) / BesselI0Ref(beta);
                c_[ph][j] = std::sin(kPi * u) / (kPi * u) * w;
                sum += c_[ph][j];
            }
            for (int j = 0; j < 64; ++j) c_[ph][j] /= sum;
        }
    }
    // Largest |value| over the samples k in [from, to) and the 15 interpolated points after each of them.
    double Peak(const std::vector<float>& x, size_t from, size_t to) const {
        double peak = 0.0;
        from = std::max<size_t>(from, 31);
        to = std::min(to, x.size() >= 33 ? x.size() - 33 : static_cast<size_t>(0));
        for (size_t k = from; k < to; ++k) {
            peak = std::max(peak, static_cast<double>(std::fabs(x[k])));
            for (int ph = 1; ph < 16; ++ph) {
                double acc = 0.0;
                for (int j = 0; j < 64; ++j) acc += c_[ph][j] * x[k - 31 + static_cast<size_t>(j)];
                peak = std::max(peak, std::fabs(acc));
            }
        }
        return peak;
    }
private:
    double c_[16][64] = {};
};

static std::vector<float> ChannelOf(const std::vector<float>& v, uint32_t ch, uint32_t c) {
    std::vector<float> r(v.size() / ch);
    for (size_t i = 0; i < r.size(); ++i) r[i] = v[i * ch + c];
    return r;
}

// ================================================================================================
// Tests 1-6: K-weighting and the loudness meter
// ================================================================================================

static void T01_kweighting_48k_matches_bs1770() {
    sp::Biquad shelf{}, hp{};
    sp::KWeighting(48000.0, shelf, hp);
    const double tol = 1e-5;
    CHECK(Near(shelf.b0, 1.53512485958697, tol), "shelf b0 = %.14f", shelf.b0);
    CHECK(Near(shelf.b1, -2.69169618940638, tol), "shelf b1 = %.14f", shelf.b1);
    CHECK(Near(shelf.b2, 1.19839281085285, tol), "shelf b2 = %.14f", shelf.b2);
    CHECK(Near(shelf.a1, -1.69065929318241, tol), "shelf a1 = %.14f", shelf.a1);
    CHECK(Near(shelf.a2, 0.73248077421585, tol), "shelf a2 = %.14f", shelf.a2);
    CHECK(Near(hp.b0, 1.0, tol), "high-pass b0 = %.14f", hp.b0);
    CHECK(Near(hp.b1, -2.0, tol), "high-pass b1 = %.14f", hp.b1);
    CHECK(Near(hp.b2, 1.0, tol), "high-pass b2 = %.14f", hp.b2);
    CHECK(Near(hp.a1, -1.99004745483398, tol), "high-pass a1 = %.14f", hp.a1);
    CHECK(Near(hp.a2, 0.99007225036621, tol), "high-pass a2 = %.14f", hp.a2);
}

static void T02_loudness_1k_sine_stereo_minus20() {
    sp::LoudnessMeter m;
    m.Prepare(48000, 2, nullptr);
    MeterFeedAll(m, Sine(48000.0, 2, 1000.0, 0.1, 48000), 2);
    const double l = m.MomentaryLufs();
    CHECK(Near(l, -20.0, 0.2), "1 s of a 0.1 sine reads %.3f LUFS, want -20.0 +- 0.2", l);

    // Garbage channel weights (NaN, negative) must never silence a channel: they count at full weight.
    sp::LoudnessMeter garbage;
    const float badWeights[2] = {std::numeric_limits<float>::quiet_NaN(), -1.0f};
    garbage.Prepare(48000, 2, badWeights);
    MeterFeedAll(garbage, Sine(48000.0, 2, 1000.0, 0.1, 48000), 2);
    CHECK(Near(garbage.MomentaryLufs(), -20.0, 0.2), "garbage weights read %.3f LUFS, want -20.0 +- 0.2",
          garbage.MomentaryLufs());

    // Non-finite samples count as 0 and must never poison the meter: three bad frames in a second of sine leave
    // the reading at -20 (a NaN that reached the filters would freeze the meter at its floor for good).
    sp::LoudnessMeter dirty;
    dirty.Prepare(48000, 2, nullptr);
    std::vector<float> dirtyIn = Sine(48000.0, 2, 1000.0, 0.1, 48000);
    const float bad[3] = {std::numeric_limits<float>::quiet_NaN(), std::numeric_limits<float>::infinity(),
                          -std::numeric_limits<float>::infinity()};
    for (int k = 0; k < 3; ++k) { dirtyIn[(5000 + 7 * k) * 2] = bad[k]; dirtyIn[(5000 + 7 * k) * 2 + 1] = bad[k]; }
    MeterFeedAll(dirty, dirtyIn, 2);
    CHECK(Near(dirty.MomentaryLufs(), -20.0, 0.3), "a sine with three non-finite frames reads %.3f LUFS, want -20.0 +- 0.3",
          dirty.MomentaryLufs());
}

static void T03_loudness_mono_minus20_reads_minus23() {
    sp::LoudnessMeter m;
    m.Prepare(48000, 1, nullptr);
    MeterFeedAll(m, Sine(48000.0, 1, 1000.0, 0.1, 48000), 1);
    const double l = m.MomentaryLufs();
    CHECK(Near(l, -23.0, 0.2), "mono 0.1 sine reads %.3f LUFS, want -23.0 +- 0.2", l);

    // A channel weighted 0 (the LFE) is not measured: stereo with the second channel at 0 reads like mono.
    sp::LoudnessMeter lfe;
    const float weights[2] = {1.0f, 0.0f};
    lfe.Prepare(48000, 2, weights);
    MeterFeedAll(lfe, Sine(48000.0, 2, 1000.0, 0.1, 48000), 2);
    CHECK(Near(lfe.MomentaryLufs(), -23.0, 0.2), "stereo with weights (1, 0) reads %.3f LUFS, want -23.0 +- 0.2",
          lfe.MomentaryLufs());
}

static void T04_loudness_silence_is_floor() {
    sp::LoudnessMeter m;
    m.Prepare(48000, 2, nullptr);
    CHECK(m.MomentaryLufs() <= -100.0f, "before any audio the meter reads %.3f, want <= -100", m.MomentaryLufs());
    MeterFeedAll(m, Zeros(48000, 2), 2);
    const double l = m.MomentaryLufs();
    CHECK(l <= -100.0, "1 s of zeros reads %.3f LUFS, want <= -100", l);
    // The meter is alive (a meter stuck at the floor would pass the checks above): sound after the silence is
    // measured, and Reset() brings the floor back.
    MeterFeedAll(m, Sine(48000.0, 2, 1000.0, 0.1, 48000), 2);
    CHECK(Near(m.MomentaryLufs(), -20.0, 0.3), "1 s of sine after the silence reads %.3f LUFS, want -20.0 +- 0.3",
          m.MomentaryLufs());
    m.Reset();
    CHECK(m.MomentaryLufs() <= -100.0f, "after Reset() the meter reads %.3f, want <= -100", m.MomentaryLufs());
}

static void T05_loudness_other_rates_agree() {
    const uint32_t rates[] = {44100, 96000};
    for (uint32_t fs : rates) {
        sp::LoudnessMeter m;
        m.Prepare(fs, 2, nullptr);
        MeterFeedAll(m, Sine(static_cast<double>(fs), 2, 1000.0, 0.1, fs), 2);
        const double l = m.MomentaryLufs();
        CHECK(Near(l, -20.0, 0.3), "%u Hz: reads %.3f LUFS, want -20.0 +- 0.3", fs, l);
    }
}

static void T06_loudness_window_is_400ms() {
    sp::LoudnessMeter m;
    m.Prepare(48000, 2, nullptr);
    MeterFeedAll(m, Zeros(48000, 2), 2);                                  // 1 s of silence first
    const std::vector<float> sine = Sine(48000.0, 2, 1000.0, 0.1, Frames(0.450, 48000.0));
    MeterFeed(m, sine, 2, 0, Frames(0.200, 48000.0));
    const double at200 = m.MomentaryLufs();
    CHECK(Near(at200, -23.0, 0.5), "200 ms into the sine: %.3f LUFS, want -23.0 +- 0.5", at200);
    MeterFeed(m, sine, 2, Frames(0.200, 48000.0), Frames(0.250, 48000.0));
    const double at450 = m.MomentaryLufs();
    CHECK(Near(at450, -20.0, 0.3), "450 ms into the sine: %.3f LUFS, want -20.0 +- 0.3", at450);
}

// ================================================================================================
// Tests 7-14: Leveler
// ================================================================================================

static void T07_leveler_quiet_input_untouched() {
    sp::Leveler lv;
    lv.Prepare(48000, 2, nullptr);                                        // default params: cap -20 dB
    const std::vector<float> in = Sine(48000.0, 2, 1000.0, 0.0316, 96000);
    std::vector<float> out;
    LvRun(lv, in, 2, out);
    const double worst = MaxAbsDiff(in, out);
    CHECK(worst <= 1e-7, "quiet input changed by up to %.3g, want <= 1e-7", worst);
    CHECK(lv.GainReductionDb() == 0.0f, "gain reduction is %.6f dB, want exactly 0", lv.GainReductionDb());
    CHECK(Near(lv.InputLoudnessDb(), -30.0, 0.3), "input loudness reads %.3f, want about -30", lv.InputLoudnessDb());
}

static void T08_leveler_loud_input_settles_at_cap() {
    sp::Leveler lv;
    lv.Prepare(48000, 2, nullptr);
    const std::vector<float> in = Sine(48000.0, 2, 1000.0, 0.562, 144000);
    std::vector<float> out;
    LvRun(lv, in, 2, out);
    const double outLoudness = LoudnessOf(out, 48000, 2);
    CHECK(Near(outLoudness, -20.0, 1.0), "output reads %.3f LUFS at 3 s, want -20.0 +- 1.0", outLoudness);
    CHECK(Near(lv.GainReductionDb(), 15.0, 1.0), "gain reduction is %.3f dB at 3 s, want 15 +- 1", lv.GainReductionDb());

    // Beyond the brief: the same at the other sample rates, and with 8 channels and the channel mask 0x63F that
    // the plug-in host uses (the output is measured with the same channel weights).
    const uint32_t rates[] = {44100, 96000};
    for (uint32_t fs : rates) {
        sp::Leveler other;
        other.Prepare(fs, 2, nullptr);
        std::vector<float> o2;
        LvRun(other, Sine(static_cast<double>(fs), 2, 1000.0, 0.562, 3 * static_cast<size_t>(fs)), 2, o2);
        CHECK(Near(LoudnessOf(o2, fs, 2), -20.0, 1.0), "%u Hz: output reads %.3f LUFS at 3 s, want -20.0 +- 1.0", fs,
              LoudnessOf(o2, fs, 2));
        CHECK(Near(other.GainReductionDb(), 15.0, 1.0), "%u Hz: gain reduction is %.3f dB at 3 s, want 15 +- 1", fs,
              other.GainReductionDb());
    }
    float w8[8] = {};
    sp::ChannelWeightsFromMask(0x63F, 8, w8);
    sp::Leveler eight;
    eight.Prepare(48000, 8, w8);
    std::vector<float> o8;
    LvRun(eight, Sine(48000.0, 8, 1000.0, 0.562, 144000), 8, o8);
    sp::LoudnessMeter m8;
    m8.Prepare(48000, 8, w8);
    MeterFeedAll(m8, o8, 8);
    CHECK(Near(m8.MomentaryLufs(), -20.0, 1.0), "8 channels, mask 0x63F: output reads %.3f LUFS at 3 s, want -20.0 +- 1.0",
          m8.MomentaryLufs());

    // Part C (not in the brief): the parameters reach the processing when SetParams is called after Prepare. A cap of
    // -30 dB with at most 20 dB of reduction holds the reduction at exactly 20; with the 40 dB maximum back it
    // settles at 25. A 10 ms attack is well ahead of the default 100 ms 150 ms into the loud sound.
    sp::Leveler tuned;
    tuned.Prepare(48000, 2, nullptr);
    sp::LevelerParams tp;
    tp.capDb = -30.0f;
    tp.maxReductionDb = 20.0f;
    LvSet(tuned, tp);
    std::vector<float> o3;
    LvRun(tuned, Sine(48000.0, 2, 1000.0, 0.562, 144000), 2, o3);
    CHECK(Near(tuned.GainReductionDb(), 20.0, 0.1), "cap -30 with at most 20 dB of reduction: %.3f dB, want 20.0",
          tuned.GainReductionDb());
    tp.maxReductionDb = 40.0f;
    LvSet(tuned, tp);
    LvRun(tuned, Sine(48000.0, 2, 1000.0, 0.562, 144000, 0.0, 144000), 2, o3);
    CHECK(Near(tuned.GainReductionDb(), 25.0, 1.0), "cap -30 with the 40 dB maximum: %.3f dB, want 25 +- 1",
          tuned.GainReductionDb());

    sp::Leveler quickAttack, defaultAttack;
    quickAttack.Prepare(48000, 2, nullptr);
    defaultAttack.Prepare(48000, 2, nullptr);
    sp::LevelerParams ap;
    ap.attackMs = 10.0f;
    LvSet(quickAttack, ap);
    const std::vector<float> onset = Sine(48000.0, 2, 1000.0, 0.562, Frames(0.150, 48000.0));
    std::vector<float> o4, o5;
    LvRun(quickAttack, onset, 2, o4);
    LvRun(defaultAttack, onset, 2, o5);
    CHECK(quickAttack.GainReductionDb() > defaultAttack.GainReductionDb() + 2.0f,
          "150 ms into the loud sound a 10 ms attack has reduced %.3f dB, the 100 ms default %.3f dB: want at least 2 dB more",
          quickAttack.GainReductionDb(), defaultAttack.GainReductionDb());
}

static void T09_leveler_never_boosts() {
    sp::Leveler lv;
    lv.Prepare(48000, 2, nullptr);
    const std::vector<float> in = SteppedNoise(48000.0, 2, 5.0, 0.25, 12345u);
    std::vector<float> out;
    LvRun(lv, in, 2, out);
    double worstExcess = -1.0;
    for (size_t i = 0; i < in.size(); ++i)
        worstExcess = std::max(worstExcess, static_cast<double>(std::fabs(out[i])) - std::fabs(in[i]));
    CHECK(worstExcess <= 1e-7, "an output sample is louder than its input by %.3g, want <= 1e-7", worstExcess);
    CHECK(AnyReduced(in, out, 0, 2, 0.05, 0.7), "the leveler never reduced anything: the test signal proves nothing");
}

static void T10_leveler_release_returns_to_unity() {
    // Controller's ruling (after the NEEDS_CONTEXT report): the brief pinned 3 s of quiet after the 2 s of loud, but its
    // own pinned 1000 ms one-pole release cannot fall from about 15 dB to under 0.5 dB in 3 s (15*exp(-3) = 0.75 dB at
    // the very best, and the meter's 400 ms window delays the start of the release). The release time comes from the
    // spec and the test length does not, so the quiet phase is 4 s. The bar (< 0.5 dB) and releaseMs = 1000 are kept.
    sp::Leveler lv;
    lv.Prepare(48000, 2, nullptr);
    std::vector<float> out;
    LvRun(lv, Sine(48000.0, 2, 1000.0, 0.562, 96000), 2, out);            // 2 s loud
    const float held = lv.GainReductionDb();
    CHECK(held > 10.0f, "gain reduction after the loud part is only %.3f dB", held);
    const size_t quietStart = 192000;                                     // frames in 4 s of quiet
    LvRun(lv, Sine(48000.0, 2, 1000.0, 0.01, quietStart), 2, out);        // 4 s quiet
    CHECK(lv.GainReductionDb() < 0.5f, "gain reduction is still %.3f dB after 4 s of quiet, want < 0.5",
          lv.GainReductionDb());
    // Part B (not in the brief): the same reading must not be far under the bar either. A 1000 ms one-pole release
    // leaves about 0.37 dB here; a release that is much faster than the spec would read well below 0.25.
    CHECK(lv.GainReductionDb() > 0.25f,
          "gain reduction is only %.3f dB after 4 s of quiet, want about 0.37 (a 1000 ms one-pole release)",
          lv.GainReductionDb());

    // Part C (not in the brief): a recovered leveler is exactly transparent. With the 1 s release, 19 more seconds
    // of quiet bring the reduction to exactly 0 dB, and the output is then a bit-exact copy of the input.
    LvRun(lv, Sine(48000.0, 2, 1000.0, 0.01, 912000, 0.0, quietStart), 2, out);
    CHECK(lv.GainReductionDb() == 0.0f, "gain reduction is %.9f dB after 23 s of quiet, want exactly 0",
          lv.GainReductionDb());
    const std::vector<float> tail = Sine(48000.0, 2, 1000.0, 0.01, 4800, 0.0, quietStart + 912000);
    std::vector<float> tailOut;
    LvRun(lv, tail, 2, tailOut);
    CHECK(MaxAbsDiff(tail, tailOut) == 0.0, "the recovered leveler still changes the signal (by %.3g)",
          MaxAbsDiff(tail, tailOut));

    // Part D (not in the brief): the release time comes from SetParams. With releaseMs = 250, 3 s of quiet bring the
    // reduction to almost nothing, where the 1000 ms default still has about 1 dB left after the same 3 s.
    sp::Leveler quick;
    quick.Prepare(48000, 2, nullptr);
    sp::LevelerParams fast;
    fast.releaseMs = 250.0f;
    LvSet(quick, fast);
    std::vector<float> o2;
    LvRun(quick, Sine(48000.0, 2, 1000.0, 0.562, 96000), 2, o2);
    LvRun(quick, Sine(48000.0, 2, 1000.0, 0.01, 144000), 2, o2);
    CHECK(quick.GainReductionDb() < 0.05f, "with releaseMs = 250 the reduction is still %.4f dB after 3 s of quiet, want < 0.05",
          quick.GainReductionDb());
}

static void T11_leveler_disabled_ramps_to_unity() {
    sp::Leveler lv;
    lv.Prepare(48000, 2, nullptr);
    std::vector<float> out;
    LvRun(lv, Sine(48000.0, 2, 1000.0, 0.562, 144000), 2, out);           // settled, as in test 8
    const float settled = lv.GainReductionDb();
    CHECK(Near(settled, 15.0, 1.0), "gain reduction before disabling is %.3f dB, want about 15", settled);

    sp::LevelerParams p;
    p.enabled = false;
    LvSet(lv, p);

    const size_t t = 144000;
    LvRun(lv, Sine(48000.0, 2, 1000.0, 0.562, 480, 0.0, t), 2, out);     // 10 ms later
    // The fall is linear, 40 dB per 100 ms: 4 dB in 10 ms.
    CHECK(Near(lv.GainReductionDb(), settled - 4.0, 0.05), "10 ms after disabling the reduction is %.3f dB, want %.3f",
          lv.GainReductionDb(), settled - 4.0);

    LvRun(lv, Sine(48000.0, 2, 1000.0, 0.562, 6720, 0.0, t + 480), 2, out);   // 150 ms in total
    CHECK(lv.GainReductionDb() == 0.0f, "150 ms after disabling the reduction is %.6f dB, want exactly 0",
          lv.GainReductionDb());

    const std::vector<float> tail = Sine(48000.0, 2, 1000.0, 0.562, 2400, 0.0, t + 7200);
    std::vector<float> tailOut;
    LvRun(lv, tail, 2, tailOut);
    const double worst = MaxAbsDiff(tail, tailOut);
    CHECK(worst <= 1e-7, "after 150 ms the output differs from the input by %.3g, want <= 1e-7", worst);
}

static void T12_leveler_nonfinite_input_outputs_zero() {
    sp::Leveler lv;
    lv.Prepare(48000, 2, nullptr);
    std::vector<float> in = Sine(48000.0, 2, 1000.0, 0.01, 6000);
    const float nan = std::numeric_limits<float>::quiet_NaN();
    const float inf = std::numeric_limits<float>::infinity();
    const float bad[3] = {nan, inf, -inf};
    for (int k = 0; k < 3; ++k) { in[(1000 + k) * 2] = bad[k]; in[(1000 + k) * 2 + 1] = bad[k]; }
    std::vector<float> out;
    LvRun(lv, in, 2, out);
    CHECK(AllFinite(out), "an output sample is NaN or infinite");
    for (int k = 0; k < 3; ++k) {
        CHECK(out[(1000 + k) * 2] == 0.0f && out[(1000 + k) * 2 + 1] == 0.0f,
              "frame %d: output (%g, %g), want 0", 1000 + k, out[(1000 + k) * 2], out[(1000 + k) * 2 + 1]);
    }
    double worst = 0.0;
    for (size_t i = 2000 * 2; i < in.size(); ++i) worst = std::max(worst, std::fabs(static_cast<double>(out[i]) - in[i]));
    CHECK(worst <= 1e-7, "after the bad frames the output differs from the input by %.3g, want <= 1e-7", worst);
    CHECK(std::isfinite(lv.GainReductionDb()) && lv.GainReductionDb() >= 0.0f, "gain reduction is %g",
          lv.GainReductionDb());
    CHECK(std::isfinite(lv.InputLoudnessDb()), "input loudness is %g", lv.InputLoudnessDb());

    // Part B (not in the brief): the bad frames must not poison the loudness meter. A leveler that met NaN, +Inf and
    // -Inf while the sound was quiet still turns a loud sound down afterwards, to the same 15 dB as in test 8.
    sp::Leveler survivor;
    survivor.Prepare(48000, 2, nullptr);
    std::vector<float> in2 = Sine(48000.0, 2, 1000.0, 0.01, 6000);
    for (int k = 0; k < 3; ++k) { in2[(1000 + k) * 2] = bad[k]; in2[(1000 + k) * 2 + 1] = bad[k]; }
    Append(in2, Sine(48000.0, 2, 1000.0, 0.562, 144000, 0.0, 6000));
    std::vector<float> out2;
    LvRun(survivor, in2, 2, out2);
    CHECK(Near(survivor.GainReductionDb(), 15.0, 1.0), "after NaN and Inf frames the loud sound is reduced by only %.3f dB, want 15 +- 1",
          survivor.GainReductionDb());

    // Part C (not in the brief): finite garbage. A buffer of uninitialised memory is mostly finite nonsense; whatever
    // the values, the output never goes beyond full scale.
    const float huge[6] = {3.0f, -3.0f, 1e30f, -1e30f, std::numeric_limits<float>::max(), -std::numeric_limits<float>::max()};
    std::vector<float> junk(6 * 2);
    for (int k = 0; k < 6; ++k) { junk[static_cast<size_t>(k) * 2] = huge[k]; junk[static_cast<size_t>(k) * 2 + 1] = huge[k]; }
    sp::Leveler garbageIn;
    garbageIn.Prepare(48000, 2, nullptr);
    std::vector<float> junkOut;
    LvRun(garbageIn, junk, 2, junkOut);
    for (int k = 0; k < 6; ++k) {
        const float want = huge[k] > 0.0f ? 1.0f : -1.0f;
        CHECK(junkOut[static_cast<size_t>(k) * 2] == want && junkOut[static_cast<size_t>(k) * 2 + 1] == want,
              "input %g comes out as (%g, %g), want %g", huge[k], junkOut[static_cast<size_t>(k) * 2],
              junkOut[static_cast<size_t>(k) * 2 + 1], want);
    }
}

static void T13_leveler_in_place_matches_out_of_place() {
    const std::vector<float> in = Programme(48000.0, 2, 3.0, 4242u);   // independent noise on the two channels
    sp::Leveler a, b;
    a.Prepare(48000, 2, nullptr);
    b.Prepare(48000, 2, nullptr);
    std::vector<float> outOfPlace;
    LvRun(a, in, 2, outOfPlace);
    std::vector<float> inPlace = in;
    LvRunInPlace(b, inPlace, 2);
    const double worst = MaxAbsDiff(outOfPlace, inPlace);
    CHECK(worst <= 1e-7, "in-place output differs from out-of-place by %.3g, want <= 1e-7", worst);
    CHECK(MaxAbsDiff(in, outOfPlace) > 0.05, "the leveler left the signal alone: the test proves nothing");
}

static void T14_leveler_chunk_size_independent() {
    const std::vector<float> in = Programme(48000.0, 2, 3.0, 99u);
    const size_t chunks[] = {1, 7, 480, 4096};
    std::vector<float> reference;
    for (size_t chunk : chunks) {
        sp::Leveler lv;
        lv.Prepare(48000, 2, nullptr);
        std::vector<float> out;
        LvRun(lv, in, 2, out, chunk);
        if (reference.empty()) { reference = out; continue; }
        const double worst = MaxAbsDiff(reference, out);
        CHECK(worst <= 1e-6, "chunk size %zu differs from chunk size 1 by %.3g, want <= 1e-6", chunk, worst);
    }
    CHECK(MaxAbsDiff(in, reference) > 0.05, "the leveler left the signal alone: the test proves nothing");
    // Reset() returns the object to its just-prepared state.
    sp::Leveler lv;
    lv.Prepare(48000, 2, nullptr);
    std::vector<float> first, second;
    LvRun(lv, in, 2, first);
    lv.Reset();
    LvRun(lv, in, 2, second);
    const double worst = MaxAbsDiff(first, second);
    CHECK(worst <= 1e-6, "after Reset() the same input gives a different output (by %.3g)", worst);
}

// ================================================================================================
// Tests 15-25: Safety Cap
// ================================================================================================

static void T15_safetycap_burst_never_exceeds_ceiling() {
    sp::SafetyCap cap;
    CapInit(cap, 48000, 2, -20.0f);
    std::vector<float> in = Zeros(48000, 2);                              // 1 s of silence
    Append(in, Sine(48000.0, 2, 1000.0, 0.891, 2400));                    // 50 ms burst at -1 dBFS
    Append(in, Zeros(9600, 2));                                           // 200 ms of silence to flush the delay
    std::vector<float> out;
    CapRun(cap, in, 2, out);
    const double limit = sp::DbToLinear(-20.0f) + 1e-6;
    const double peak = PeakAbs(out, 48000 * 2);                          // from the burst's first sample on
    CHECK(peak <= limit, "output peak %.6f exceeds the ceiling %.6f", peak, limit);
    CHECK(AllFinite(out), "an output sample is not finite");
    // The limiter itself holds the ceiling (the final clamp is only the backstop), and it does not mute the burst.
    CHECK(cap.ClipperHits() == 0, "the final clamp changed %u samples, want 0", cap.ClipperHits());
    CHECK(peak >= 0.9 * sp::DbToLinear(-20.0f), "the burst comes out at only %.6f, expected close to %.6f", peak,
          sp::DbToLinear(-20.0f));

    // Part B (not in the brief): the ceiling must hold from the very first sample of a sound that starts at once
    // (no fade-in), for five burst shapes (the last is one isolated peak) and for the extremes of look-ahead and
    // hold time. The limiter itself has to do it: the final clamp must not be needed (ClipperHits stays 0).
    // Look-ahead and hold are read at Prepare, so they are set before it.
    // The release time is part of the grid because a fast release is what exposes a gain that is a frame late.
    const float lookaheads[] = {0.0f, 5.0f, 50.0f};
    const float holds[] = {0.0f, 20.0f, 500.0f};
    const float releases[] = {80.0f, 0.1f};
    for (float lookMs : lookaheads)
        for (float holdMs : holds)
            for (float relMs : releases)
                for (int shape = 0; shape < kAbruptShapes; ++shape) {
                    sp::SafetyCap shaped;
                    sp::SafetyCapParams p;
                    p.ceilingDb = -20.0f;
                    p.lookaheadMs = lookMs;
                    p.holdMs = holdMs;
                    p.releaseFastMs = relMs;
                    CapSet(shaped, p);
                    shaped.Prepare(48000, 2);
                    std::vector<float> o;
                    CapRun(shaped, AbruptBurst(shape, 2, 48000, 2400, 9600), 2, o);
                    const double pk = PeakAbs(o);
                    CHECK(pk <= limit, "look-ahead %g ms, hold %g ms, release %g ms, burst shape %d: output peak %.6f exceeds the ceiling %.6f",
                          lookMs, holdMs, relMs, shape, pk, limit);
                    CHECK(shaped.ClipperHits() == 0, "look-ahead %g ms, hold %g ms, release %g ms, burst shape %d: the final clamp changed %u samples, want 0",
                          lookMs, holdMs, relMs, shape, shaped.ClipperHits());
                    CHECK(pk >= 0.7 * sp::DbToLinear(-20.0f), "look-ahead %g ms, hold %g ms, release %g ms, burst shape %d: the burst comes out at only %.6f",
                          lookMs, holdMs, relMs, shape, pk);
                    const size_t wantLatency = static_cast<size_t>(std::floor(static_cast<double>(lookMs) * 48.0 + 0.5)) + 8;
                    CHECK(shaped.LatencyFrames() == wantLatency, "look-ahead %g ms: latency %u frames, want %zu", lookMs,
                          shaped.LatencyFrames(), wantLatency);
                }
}

static void T16_safetycap_below_ceiling_is_delayed_copy() {
    sp::SafetyCap cap;
    CapInit(cap, 48000, 2, -20.0f);
    const std::vector<float> in = Sine(48000.0, 2, 1000.0, 0.0316, 48000);
    std::vector<float> out;
    CapRun(cap, in, 2, out);
    const size_t latency = cap.LatencyFrames();
    CHECK(latency == 248, "latency is %zu frames, want 240 (5 ms look-ahead) + 8 (true-peak filter) = 248", latency);
    double worst = 0.0;
    for (size_t n = latency; n < in.size() / 2; ++n)
        for (size_t c = 0; c < 2; ++c)
            worst = std::max(worst, std::fabs(static_cast<double>(out[n * 2 + c]) - in[(n - latency) * 2 + c]));
    CHECK(worst <= 1e-6, "output differs from the delayed input by %.3g, want <= 1e-6", worst);
    CHECK(cap.GainReductionDb() == 0.0f, "gain reduction is %.6f dB, want 0", cap.GainReductionDb());
    CHECK(cap.ClipperHits() == 0, "clipper hits = %u, want 0", cap.ClipperHits());
}

static void T17_safetycap_true_peak_12k() {
    sp::SafetyCap cap;
    CapInit(cap, 48000, 2, -6.0f);
    // 12 kHz at 48 kHz is exactly four samples per period. With phase pi/4 the samples sit at +-0.7071 of the
    // amplitude: sample peak -4.0 dBFS, true peak -1.0 dBFS.
    const std::vector<float> in = Sine(48000.0, 2, 12000.0, 0.891, 14400, kPi / 4.0);
    std::vector<float> out;
    CapRun(cap, in, 2, out);
    const double inputPeakDb = cap.InputPeakDb();
    CHECK(Near(inputPeakDb, -1.0, 0.3), "InputPeakDb reads %.3f, want -1.0 +- 0.3", inputPeakDb);

    const std::vector<float> left = ChannelOf(out, 2, 0);
    const TruePeak16 meter;
    const double tp = meter.Peak(left, 2400, left.size());               // once the limiter has settled (50 ms)
    const double tpDb = 20.0 * std::log10(tp);
    CHECK(tpDb <= -6.0 + 0.7, "output true peak is %.3f dBFS, want <= -5.3", tpDb);
    CHECK(tpDb >= -6.0 - 1.5, "output true peak is %.3f dBFS: the limiter is over-attenuating", tpDb);
    // The sample peak alone would have let this signal through up to 2 dB too loud (-4.0 vs -6.0 dBFS).
    CHECK(PeakAbs(out, 2400 * 2) < sp::DbToLinear(-6.0f) + 1e-6, "an output sample is above the ceiling");

    // Telemetry agrees with what the output really did: OutputPeakDb is the largest |output| of the last Process
    // call (the last 480 frames), and the gain reduction is the input true peak over the ceiling.
    const double lastPeak = PeakAbs(out, out.size() - 480 * 2);
    CHECK(Near(cap.OutputPeakDb(), 20.0 * std::log10(lastPeak), 0.01), "OutputPeakDb reads %.3f, the last call's peak is %.3f dB",
          cap.OutputPeakDb(), 20.0 * std::log10(lastPeak));
    CHECK(Near(cap.GainReductionDb(), inputPeakDb + 6.0, 0.05), "GainReductionDb reads %.3f, want about %.3f (input peak over ceiling)",
          cap.GainReductionDb(), inputPeakDb + 6.0);
}

static void T18_safetycap_step_holds_ceiling() {
    sp::SafetyCap cap;
    CapInit(cap, 48000, 2, -10.0f);
    std::vector<float> in = Sine(48000.0, 2, 1000.0, 0.0316, 48000);       // 1 s quiet
    Append(in, Sine(48000.0, 2, 1000.0, 0.708, 48000, 0.0, 48000));        // 1 s loud, a step up
    std::vector<float> out;
    CapRun(cap, in, 2, out);
    const double limit = sp::DbToLinear(-10.0f) + 1e-6;
    const double peak = PeakAbs(out);
    CHECK(peak <= limit, "output peak %.6f exceeds the ceiling %.6f", peak, limit);
    CHECK(cap.ClipperHits() == 0, "the final clamp changed %u samples, want 0", cap.ClipperHits());
    CHECK(PeakAbs(out, 48000 * 2 + 48000) >= 0.9 * sp::DbToLinear(-10.0f),
          "the loud second comes out at only %.6f, expected close to %.6f", PeakAbs(out, 48000 * 2 + 48000),
          sp::DbToLinear(-10.0f));

    // Part B (not in the brief): the hold keeps the gain from following individual waves. A 12 Hz sine at
    // -1 dBFS spends about 460 frames per half-wave under the ceiling, longer than the look-ahead alone bridges.
    // With the 20 ms hold the gain stays put; without it the gain would recover and breathe with every wave.
    sp::SafetyCap steady;
    CapInit(steady, 48000, 2, -10.0f);
    const std::vector<float> wave = Sine(48000.0, 2, 12.0, 0.891, Frames(2.0, 48000.0));
    std::vector<float> waveOut(wave.size());
    double lowest = 1e9, highest = -1e9;
    for (size_t pos = 0; pos + 48 <= wave.size() / 2; pos += 48) {
        {
            NoAlloc guard;
            steady.Process(wave.data() + pos * 2, waveOut.data() + pos * 2, 48);
        }
        if (pos >= Frames(1.0, 48000.0)) {                                // judge the second second
            lowest = std::min(lowest, static_cast<double>(steady.GainReductionDb()));
            highest = std::max(highest, static_cast<double>(steady.GainReductionDb()));
        }
    }
    CHECK(lowest > 5.0, "the 12 Hz sine is hardly limited (reduction at least %.3f dB, want > 5)", lowest);
    CHECK(highest - lowest < 0.2, "the gain reduction swings by %.3f dB (%.3f to %.3f) with a 12 Hz sine: the gain follows individual waves",
          highest - lowest, lowest, highest);

    // Part C (not in the brief): the gain comes down smoothly during the look-ahead, while the quiet sine is still
    // playing. Without the look-ahead smoothing it would drop by more than 0.5 in a single frame, which is a click.
    const size_t latency = cap.LatencyFrames();
    double minGain = 1.0, maxStep = 0.0, prevGain = 1.0;
    size_t prevN = 0;
    bool havePrev = false;
    for (size_t n = 48000 - 600; n < 48000 + 1200; ++n) {                 // output frames around the step
        const double x = in[(n - latency) * 2];
        if (std::fabs(x) < 0.02) continue;                                // the gain is read where the sound is
        const double g = out[n * 2] / x;
        minGain = std::min(minGain, g);
        if (havePrev) maxStep = std::max(maxStep, std::fabs(g - prevGain) / static_cast<double>(n - prevN));
        prevGain = g;
        prevN = n;
        havePrev = true;
    }
    CHECK(minGain > 0.38 && minGain < 0.5, "the gain settles at %.3f, want about 0.446 (the ceiling over the sine's amplitude)",
          minGain);
    CHECK(maxStep < 0.005, "the gain changes by %.4f in one frame, want a smooth ramp (< 0.005)", maxStep);

    // Part D (not in the brief's test list; it pins step 6 of the algorithm): the look-ahead smoothing is a moving
    // average of the envelope over the last D + 1 frames. One quiet-then-loud step runs through a limiter without
    // look-ahead, whose gain is the raw envelope, and through limiters with D frames of look-ahead, whose gain must
    // be that raw gain averaged over D + 1 frames, whatever the staircase of the true-peak estimate looks like. The
    // long hold keeps the envelope on its way down (no release is involved), and the quiet DC level before the step
    // keeps the audio non-zero, so the gain can be read at every frame as output over delayed input.
    const size_t stepAt = 4800;
    std::vector<float> dcIn(stepAt * 2, 0.03f);
    Append(dcIn, std::vector<float>(9600 * 2, 0.7f));
    const auto gainOf = [&dcIn](float lookMs, std::vector<double>& gain) -> size_t {
        sp::SafetyCap c;
        sp::SafetyCapParams p;
        p.ceilingDb = -10.0f;
        p.lookaheadMs = lookMs;
        p.holdMs = 200.0f;
        CapSet(c, p);
        c.Prepare(48000, 2);
        std::vector<float> o;
        CapRun(c, dcIn, 2, o);
        const size_t lat = c.LatencyFrames();
        gain.assign(o.size() / 2, 1.0);
        for (size_t n = lat; n < gain.size(); ++n) gain[n] = static_cast<double>(o[n * 2]) / dcIn[(n - lat) * 2];
        return lat;
    };
    std::vector<double> rawGain;
    const size_t rawLat = gainOf(0.0f, rawGain);
    double rawLow = 1.0, rawStep = 0.0;
    for (size_t n = rawLat + 1; n < rawGain.size(); ++n) {
        rawLow = std::min(rawLow, rawGain[n]);
        rawStep = std::max(rawStep, std::fabs(rawGain[n] - rawGain[n - 1]));
    }
    CHECK(rawLat == 8, "without look-ahead the latency is %zu frames, want the 8 of the true-peak filter", rawLat);
    CHECK(rawLow > 0.3 && rawLow < 0.5, "without look-ahead the loud step settles at gain %.3f, want about 0.43", rawLow);
    CHECK(rawStep > 0.1, "without look-ahead the gain drops by only %.4f at its sharpest frame: the step has no edge to smooth",
          rawStep);
    const float lookaheads[] = {0.02f, 1.0f, 5.0f};                        // 1, 48 and 240 frames
    for (float lookMs : lookaheads) {
        std::vector<double> smoothGain;
        const size_t lat = gainOf(lookMs, smoothGain);
        const size_t d = lat - rawLat;
        double worst = 0.0;
        size_t compared = 0;
        for (size_t n = lat; n < stepAt + lat + 300; ++n, ++compared) {
            double mean = 0.0;
            for (size_t t = n - d; t <= n; ++t) mean += rawGain[t];
            mean /= static_cast<double>(d + 1);
            worst = std::max(worst, std::fabs(smoothGain[n] - mean));
        }
        CHECK(d >= 1 && compared > stepAt, "look-ahead %g ms: %zu frames of look-ahead, %zu frames compared", lookMs, d, compared);
        CHECK(worst <= 2e-6, "look-ahead %g ms: the gain differs from the %zu-frame moving average of the raw envelope by %.3g, want <= 2e-6",
              lookMs, d + 1, worst);
    }
}

// The burst of test 15 followed by a quiet sine.
static void T19_safetycap_release() {
    sp::SafetyCap cap;
    CapInit(cap, 48000, 2, -20.0f);
    std::vector<float> in = Zeros(48000, 2);
    Append(in, Sine(48000.0, 2, 1000.0, 0.891, 2400));                    // the burst
    const size_t afterBurst = in.size() / 2;
    Append(in, Sine(48000.0, 2, 1000.0, 0.0316, Frames(0.600, 48000.0))); // 600 ms of quiet sine
    std::vector<float> out;
    CapRun(cap, in, 2, out);
    // The burst itself comes out limited to the ceiling, and not muted.
    const double burstPeak = PeakAbs(out, 48000 * 2, (afterBurst + 600) * 2);
    CHECK(burstPeak > 0.05 && burstPeak <= sp::DbToLinear(-20.0f) + 1e-6,
          "the burst comes out at %.6f, want it limited to about %.6f", burstPeak, sp::DbToLinear(-20.0f));
    CHECK(cap.GainReductionDb() < 0.1f, "600 ms after the burst the reduction is still %.4f dB, want < 0.1",
          cap.GainReductionDb());
    const size_t latency = cap.LatencyFrames();
    double worst = 0.0;
    const size_t total = in.size() / 2;
    for (size_t n = total - Frames(0.050, 48000.0); n < total; ++n)       // the last 50 ms
        for (size_t c = 0; c < 2; ++c)
            worst = std::max(worst, std::fabs(static_cast<double>(out[n * 2 + c]) - in[(n - latency) * 2 + c]));
    CHECK(worst <= 1e-6, "600 ms after the burst the output is not a delayed copy yet (differs by %.3g)", worst);

    // Part B (not in the brief): after limiting that lasts longer than slowAfterMs (200 ms) the release is the slow
    // one (400 ms). 400 ms after a loud second the gain is therefore still well down (about 3.7 dB; the fast release
    // would be at 0), and it is back to unity within 3 s.
    sp::SafetyCap sustained;
    CapInit(sustained, 48000, 2, -20.0f);
    std::vector<float> long1 = Zeros(4800, 2);
    Append(long1, Sine(48000.0, 2, 1000.0, 0.891, 48000, 0.0, 4800));    // 1 s of loud sine (100 whole periods in)
    std::vector<float> o2;
    CapRun(sustained, long1, 2, o2);
    CapRun(sustained, Sine(48000.0, 2, 1000.0, 0.0316, Frames(0.400, 48000.0), 0.0, 52800), 2, o2);
    CHECK(sustained.GainReductionDb() > 2.0f,
          "400 ms after a loud second the reduction is only %.3f dB: the release after sustained limiting should be slow",
          sustained.GainReductionDb());
    CapRun(sustained, Sine(48000.0, 2, 1000.0, 0.0316, Frames(2.600, 48000.0), 0.0, 72000), 2, o2);
    CHECK(sustained.GainReductionDb() < 0.1f, "3 s after a loud second the reduction is still %.3f dB, want < 0.1",
          sustained.GainReductionDb());

    // Part C (not in the brief): the slow release is not sticky. After a complete recovery, a short burst releases
    // fast again, as in the first part of this test.
    sp::SafetyCap again;
    CapInit(again, 48000, 2, -20.0f);
    std::vector<float> seq = Zeros(4800, 2);
    Append(seq, Sine(48000.0, 2, 1000.0, 0.891, 48000, 0.0, 4800));       // 1 s loud: the slow release is armed
    Append(seq, Sine(48000.0, 2, 1000.0, 0.0316, Frames(4.0, 48000.0), 0.0, 52800));   // 4 s quiet: fully recovered
    const size_t secondBurst = seq.size() / 2;
    Append(seq, Sine(48000.0, 2, 1000.0, 0.891, 2400, 0.0, secondBurst));  // a 50 ms burst
    Append(seq, Sine(48000.0, 2, 1000.0, 0.0316, Frames(0.600, 48000.0), 0.0, secondBurst + 2400));
    std::vector<float> o3;
    CapRun(again, seq, 2, o3);
    CHECK(again.GainReductionDb() < 0.1f,
          "600 ms after a short burst that follows a long loud passage the reduction is %.3f dB, want < 0.1 (the slow release stayed armed)",
          again.GainReductionDb());

    // Part D (not in the brief): the release times come from SetParams. With a 20 ms fast release the reduction is
    // gone 200 ms after a short burst (the 80 ms default is still at about 0.9 dB then); with a 100 ms slow release it
    // is gone 700 ms after a loud second (the 400 ms default is still at about 1.5 dB).
    sp::SafetyCap quick;
    CapInit(quick, 48000, 2, -20.0f);
    sp::SafetyCapParams tuned;
    tuned.ceilingDb = -20.0f;
    tuned.releaseFastMs = 20.0f;
    CapSet(quick, tuned);
    std::vector<float> o4 = Zeros(48000, 2);
    Append(o4, Sine(48000.0, 2, 1000.0, 0.891, 2400));
    Append(o4, Sine(48000.0, 2, 1000.0, 0.0316, Frames(0.200, 48000.0)));
    std::vector<float> discard;
    CapRun(quick, o4, 2, discard);
    CHECK(quick.GainReductionDb() < 0.05f, "with releaseFastMs = 20 the reduction is still %.3f dB 200 ms after a short burst, want < 0.05",
          quick.GainReductionDb());

    sp::SafetyCap fastSlow;
    CapInit(fastSlow, 48000, 2, -20.0f);
    sp::SafetyCapParams tunedSlow;
    tunedSlow.ceilingDb = -20.0f;
    tunedSlow.releaseSlowMs = 100.0f;
    CapSet(fastSlow, tunedSlow);
    std::vector<float> o5 = Zeros(4800, 2);
    Append(o5, Sine(48000.0, 2, 1000.0, 0.891, 48000, 0.0, 4800));
    Append(o5, Sine(48000.0, 2, 1000.0, 0.0316, Frames(0.700, 48000.0), 0.0, 52800));
    CapRun(fastSlow, o5, 2, discard);
    CHECK(fastSlow.GainReductionDb() < 0.1f, "with releaseSlowMs = 100 the reduction is still %.3f dB 700 ms after a loud second, want < 0.1",
          fastSlow.GainReductionDb());
}

static void T20_safetycap_disabled_ramps_to_0dbfs() {
    sp::SafetyCap cap;
    CapInit(cap, 48000, 2, -20.0f, false);                                // ceiling -20, but disabled
    const std::vector<float> in = Sine(48000.0, 2, 1000.0, 0.5, Frames(0.300, 48000.0));
    std::vector<float> out;
    CapRun(cap, in, 2, out);
    const double peak = PeakAbs(out, Frames(0.150, 48000.0) * 2);
    CHECK(peak >= 0.49, "after 150 ms the output peak is %.4f, want >= 0.49", peak);
    CHECK(peak <= 0.5 + 1e-6, "the output peak %.6f is above the input", peak);

    // Part B (not in the brief): switched off in mid-stream. The ceiling must ramp up over 100 ms instead of
    // jumping, and the loud sine that arrives right after the switch must come through completely in the end.
    sp::SafetyCap live;
    CapInit(live, 48000, 2, -20.0f);                                      // enabled, ceiling -20 dB
    std::vector<float> o2;
    const size_t quietFrames = Frames(0.500, 48000.0);
    CapRun(live, Sine(48000.0, 2, 1000.0, 0.01, quietFrames), 2, o2);     // nothing to limit yet
    sp::SafetyCapParams off;
    off.ceilingDb = -20.0f;
    off.enabled = false;
    CapSet(live, off);
    CapRun(live, Sine(48000.0, 2, 1000.0, 0.5, Frames(1.500, 48000.0), 0.0, quietFrames), 2, o2);
    const size_t rampFrames = Frames(0.100, 48000.0);
    double worstOver = -1.0;
    for (size_t i = 0; i < rampFrames; ++i) {
        // The ceiling climbs linearly in dB from -20 to 0. A sample is judged 8 frames after it enters (the true-peak
        // filter delay), under the ceiling of that moment; one more frame of slack.
        const double db = -20.0 + 20.0 * static_cast<double>(std::min(i + 10, rampFrames)) / static_cast<double>(rampFrames);
        const double bound = std::pow(10.0, db / 20.0);
        for (size_t c = 0; c < 2; ++c)
            worstOver = std::max(worstOver, std::fabs(static_cast<double>(o2[(quietFrames + i) * 2 + c])) - bound);
    }
    CHECK(worstOver <= 1e-6, "during the 100 ms ramp an output sample is %.3g above the rising ceiling", worstOver);
    CHECK(PeakAbs(o2, o2.size() - Frames(0.100, 48000.0) * 2) >= 0.49, "1.5 s after switching off the sine is still reduced (peak %.4f)",
          PeakAbs(o2, o2.size() - Frames(0.100, 48000.0) * 2));

    // Part C (not in the brief): the ceiling does not rise more slowly than asked. The gain release hides the ramp in
    // the test above (hold and a release of 80 ms follow it), so this one takes both away: no look-ahead, no hold and
    // a 0.1 ms release make the output follow the ceiling closely. 90 ms after switching off, the ceiling is at
    // -2 dB, and the 0.5 sine passes in full.
    sp::SafetyCap tight;
    sp::SafetyCapParams tightParams;
    tightParams.ceilingDb = -20.0f;
    tightParams.lookaheadMs = 0.0f;
    tightParams.holdMs = 0.0f;
    tightParams.releaseFastMs = 0.1f;
    CapSet(tight, tightParams);
    tight.Prepare(48000, 2);
    std::vector<float> o3;
    CapRun(tight, Sine(48000.0, 2, 1000.0, 0.01, quietFrames), 2, o3);
    tightParams.enabled = false;
    CapSet(tight, tightParams);
    CapRun(tight, Sine(48000.0, 2, 1000.0, 0.5, Frames(0.300, 48000.0), 0.0, quietFrames), 2, o3);
    const double at90 = PeakAbs(o3, (quietFrames + Frames(0.090, 48000.0)) * 2, (quietFrames + Frames(0.100, 48000.0)) * 2);
    CHECK(at90 >= 0.49, "90 ms after switching off the peak is only %.4f: the ceiling rises more slowly than over 100 ms", at90);
}

static void T21_safetycap_tightening_applies_at_once() {
    sp::SafetyCap cap;
    CapInit(cap, 48000, 2, -10.0f);
    std::vector<float> scratch;
    CapRun(cap, Sine(48000.0, 2, 1000.0, 0.25, 48000), 2, scratch);
    CHECK(PeakAbs(scratch, 48000) > 0.2, "before the change the 0.25 sine should pass (peak %.4f)",
          PeakAbs(scratch, 48000));

    CHECK(cap.ClipperHits() == 0, "the final clamp changed %u samples before the ceiling moved, want 0", cap.ClipperHits());

    sp::SafetyCapParams p;
    p.ceilingDb = -20.0f;
    CapSet(cap, p);

    std::vector<float> out;
    const std::vector<float> after = Sine(48000.0, 2, 1000.0, 0.25, 48000, 0.0, 48000);
    const std::vector<float> head(after.begin(), after.begin() + 600 * 2);       // longer than the 248 frames in flight
    const std::vector<float> rest(after.begin() + 600 * 2, after.end());
    CapRun(cap, head, 2, out);
    const uint32_t hitsInFlight = cap.ClipperHits();
    CapRun(cap, rest, 2, out);
    const double limit = sp::DbToLinear(-20.0f) + 1e-6;
    const double peak = PeakAbs(out);
    CHECK(peak <= limit, "after tightening to -20 dB an output sample is %.6f, want <= %.6f", peak, limit);
    CHECK(PeakAbs(out, 24000) >= 0.9 * sp::DbToLinear(-20.0f), "the sine is muted after the change (peak %.6f)",
          PeakAbs(out, 24000));

    // The samples that were already inside the look-ahead when the ceiling moved are above the new ceiling and can
    // only be caught by the final clamp, which counts them. Only those: the limiter has the rest under control.
    CHECK(hitsInFlight > 0, "the final clamp counted no sample although the in-flight samples were above the new ceiling");
    CHECK(hitsInFlight <= 2 * cap.LatencyFrames(), "the final clamp counted %u samples, at most %u (2 channels x latency) can be in flight",
          hitsInFlight, 2 * cap.LatencyFrames());
    CHECK(cap.ClipperHits() == hitsInFlight, "the final clamp kept acting after the in-flight samples (%u -> %u)", hitsInFlight,
          cap.ClipperHits());
}

static void T22_safetycap_never_louder_than_input() {
    sp::SafetyCap cap;
    CapInit(cap, 48000, 2, -20.0f);
    const std::vector<float> in = SteppedNoise(48000.0, 2, 5.0, 0.25, 12345u);
    std::vector<float> out;
    CapRun(cap, in, 2, out);
    const size_t latency = cap.LatencyFrames();
    double worstExcess = -1.0;
    for (size_t n = latency; n < in.size() / 2; ++n)
        for (size_t c = 0; c < 2; ++c)
            worstExcess = std::max(worstExcess, static_cast<double>(std::fabs(out[n * 2 + c])) -
                                                    std::fabs(in[(n - latency) * 2 + c]));
    CHECK(worstExcess <= 1e-6, "an output sample is louder than its delayed input by %.3g, want <= 1e-6", worstExcess);
    CHECK(AnyReduced(in, out, latency, 2, 0.2, 0.7), "the safety cap never reduced anything: the test proves nothing");
    CHECK(cap.ClipperHits() == 0, "the final clamp changed %u samples, want 0 (the limiter itself holds the ceiling)",
          cap.ClipperHits());
}

static void T23_safetycap_nonfinite_safe() {
    sp::SafetyCap cap;
    CapInit(cap, 48000, 2, -20.0f);
    std::vector<float> in = Sine(48000.0, 2, 1000.0, 0.5, 14400);        // loud, so the limiter is working
    const float nan = std::numeric_limits<float>::quiet_NaN();
    const float inf = std::numeric_limits<float>::infinity();
    const float bad[3] = {nan, inf, -inf};
    for (int k = 0; k < 3; ++k) { in[(1000 + k) * 2] = bad[k]; in[(1000 + k) * 2 + 1] = bad[k]; }
    std::vector<float> out;
    CapRun(cap, in, 2, out);
    CHECK(AllFinite(out), "an output sample is NaN or infinite");
    const size_t latency = cap.LatencyFrames();
    for (int k = 0; k < 3; ++k) {
        const size_t n = 1000 + static_cast<size_t>(k) + latency;
        CHECK(out[n * 2] == 0.0f && out[n * 2 + 1] == 0.0f, "delayed position of bad frame %d: output (%g, %g), want 0",
              1000 + k, out[n * 2], out[n * 2 + 1]);
    }
    CHECK(PeakAbs(out) <= sp::DbToLinear(-20.0f) + 1e-6, "output peak %.6f exceeds the ceiling", PeakAbs(out));
    CHECK(PeakAbs(out, 8000 * 2) >= 0.9 * sp::DbToLinear(-20.0f), "the limiter went quiet after the bad frames");
    CHECK(std::isfinite(cap.GainReductionDb()) && cap.GainReductionDb() >= 0.0f, "gain reduction is %g",
          cap.GainReductionDb());
    CHECK(std::isfinite(cap.InputPeakDb()) && std::isfinite(cap.OutputPeakDb()), "a peak reading is not finite");

    // Part B (not in the brief): finite garbage, as in a buffer of uninitialised memory. Huge values are limited like
    // any loud sound: the output stays finite and under the ceiling, and the limiter is back to unity within 1.5 s
    // (the fast release needs about half a second to climb back from a gain of 1e-31).
    sp::SafetyCap junkCap;
    CapInit(junkCap, 48000, 2, -20.0f);
    std::vector<float> junk = Sine(48000.0, 2, 1000.0, 0.5, 4800);       // 100 ms of loud sound: the fast release applies
    const float huge[5] = {3.0f, 1e30f, -1e30f, std::numeric_limits<float>::max(), -std::numeric_limits<float>::max()};
    for (int k = 0; k < 5; ++k) { junk[(1000 + static_cast<size_t>(k)) * 2] = huge[k]; junk[(1000 + static_cast<size_t>(k)) * 2 + 1] = huge[k]; }
    Append(junk, Sine(48000.0, 2, 1000.0, 0.0316, 72000, 0.0, 4800));    // then 1.5 s of quiet sound
    std::vector<float> junkOut;
    CapRun(junkCap, junk, 2, junkOut);
    CHECK(AllFinite(junkOut), "an output sample is NaN or infinite after finite garbage");
    CHECK(PeakAbs(junkOut) <= sp::DbToLinear(-20.0f) + 1e-6, "output peak %.6f exceeds the ceiling after finite garbage",
          PeakAbs(junkOut));
    CHECK(junkCap.GainReductionDb() < 0.1f, "1.5 s after the garbage the reduction is still %.4f dB, want < 0.1",
          junkCap.GainReductionDb());

    // The telemetry survives the largest float as well. Samples (A, A, -A, -A, ...) with A = FLT_MAX are a sine at a
    // quarter of the sample rate whose true peak is 1.41 times FLT_MAX: off the float scale. It must read as a huge
    // finite number of dB, not as the floor (which would hide the loudest sound of all) and not as a conversion
    // overflow.
    sp::SafetyCap maxCap;
    CapInit(maxCap, 48000, 2, -20.0f);
    std::vector<float> run(480 * 2, 0.0f);
    for (size_t i = 100; i < 140; ++i)
        run[i * 2] = run[i * 2 + 1] = ((i / 2) % 2 == 0) ? std::numeric_limits<float>::max() : -std::numeric_limits<float>::max();
    std::vector<float> runOut;
    CapRun(maxCap, run, 2, runOut);
    CHECK(AllFinite(runOut) && PeakAbs(runOut) <= sp::DbToLinear(-20.0f) + 1e-6, "FLT_MAX in the input: output peak %.6f",
          PeakAbs(runOut));
    CHECK(std::isfinite(maxCap.InputPeakDb()) && maxCap.InputPeakDb() > 100.0f,
          "a 4.8e38 true peak is reported as %g dBFS, want a finite number above 100", maxCap.InputPeakDb());
}

// ------------------------------------------------------------------------------------------------
// Fail-closed helpers (test 24, parts D to F): a processor that could not be prepared must not hand its input on
// ------------------------------------------------------------------------------------------------

// Samples to give an unprepared processor, each chosen relative to the clamp level c (1.0 for the Leveler, the
// linear ceiling for the Safety Cap): garbage (NaN, +-Inf), values far over c (+-7.0, +-FLT_MAX), values clearly over
// it (1.1 c, -1.5 c) and values clearly under it (0, 0.5 c, -0.9 c, -0.3 c, 1e-30). The pattern is 17 samples long
// and 17 is prime, so every channel of an interleaved frame sees every kind of sample.
static std::vector<float> FailClosedProbe(size_t frames, uint32_t ch, double c) {
    const float nan = std::numeric_limits<float>::quiet_NaN();
    const float inf = std::numeric_limits<float>::infinity();
    const float big = std::numeric_limits<float>::max();
    const float vals[17] = {7.0f, nan, inf, static_cast<float>(0.5 * c), -inf, -7.0f, 0.0f, static_cast<float>(1.1 * c),
                            big, static_cast<float>(-0.9 * c), -big, static_cast<float>(-1.5 * c), 1e-30f, -nan,
                            static_cast<float>(-0.3 * c), 7.0f, -7.0f};
    std::vector<float> v(frames * ch);
    for (size_t i = 0; i < v.size(); ++i) v[i] = vals[i % 17];
    return v;
}

struct GuardedRun {
    std::vector<float> in;       // what the processor was given
    std::vector<float> out;      // what it left in the output buffer (frames x channels samples)
    bool guardIntact = true;     // the 64 samples behind the buffer still hold their 7.0f marker
};

// Calls process(in, out, frames) once. inPlace: input and output are the same buffer; otherwise the output starts as
// all 7.0f. A guard of 64 samples of 7.0f follows the buffer, so a write that runs past the end is seen as well as
// one that falls short.
template <class Fn>
static GuardedRun RunGuarded(const std::vector<float>& probe, uint32_t frames, bool inPlace, Fn process) {
    GuardedRun r;
    r.in = probe;
    std::vector<float> a(probe);
    a.resize(probe.size() + 64, 7.0f);
    std::vector<float> b;
    if (!inPlace) b.assign(probe.size() + 64, 7.0f);
    std::vector<float>& o = inPlace ? a : b;
    process(a.data(), o.data(), frames);
    r.out.assign(o.begin(), o.begin() + static_cast<std::ptrdiff_t>(probe.size()));
    for (size_t i = probe.size(); i < o.size(); ++i) r.guardIntact = r.guardIntact && o[i] == 7.0f;
    return r;
}

static bool SameBits(const std::vector<float>& a, const std::vector<float>& b) {
    return a.size() == b.size() && (a.empty() || std::memcmp(a.data(), b.data(), a.size() * sizeof(float)) == 0);
}

// How many samples of `out` are not what a fail-closed processor must make of `in`: NaN and Inf become 0, a value of
// at most 0.95 c stays exactly as it is, and any other value becomes +-c (within tol, same sign). `over` counts the
// samples of that last kind, which are the ones a clamp has to change. `firstBad` is the index of the first wrong one.
static size_t FailClosedWrong(const std::vector<float>& in, const std::vector<float>& out, double c, double tol,
                              size_t& over, size_t& firstBad) {
    size_t wrong = 0;
    over = 0;
    firstBad = 0;
    for (size_t i = 0; i < in.size(); ++i) {
        const float x = in[i], y = out[i];
        bool bad = false;
        if (!std::isfinite(x)) {
            bad = (y != 0.0f);
        } else if (std::fabs(static_cast<double>(x)) <= 0.95 * c) {
            bad = !(y == x);
        } else {
            ++over;
            bad = !(std::fabs(std::fabs(static_cast<double>(y)) - c) <= tol && (y > 0.0f) == (x > 0.0f));
        }
        if (bad && wrong++ == 0) firstBad = i;
    }
    return wrong;
}

// One Process call on a Safety Cap that is not prepared: `frames` frames of bufCh channels of FailClosedProbe (built
// around the clamp level c, the linear ceiling in force), with a guard behind the buffer. Expects every sample
// sanitised and clamped (NaN and Inf as 0, the rest to +-c, in-range values untouched), nothing written past the end,
// ClipperHits() up by the number of samples the clamp changed, and no latency. `what` names the case.
static void ExpectCapFailsClosed(sp::SafetyCap& cap, uint32_t bufCh, uint32_t frames, bool inPlace, double c,
                                 const char* what) {
    const uint32_t hitsBefore = cap.ClipperHits();
    const GuardedRun r = RunGuarded(FailClosedProbe(frames, bufCh, c), frames, inPlace,
                                    [&cap](const float* pin, float* pout, uint32_t pn) {
                                        NoAlloc rt;
                                        cap.Process(pin, pout, pn);
                                    });
    size_t over = 0, firstBad = 0;
    const size_t wrong = FailClosedWrong(r.in, r.out, c, 1e-6, over, firstBad);
    CHECK(wrong == 0, "%s: %zu of %zu output samples are not sanitised and clamped to +-%.6g (first: sample %zu, in %g, out %g)",
          what, wrong, r.out.size(), c, firstBad, static_cast<double>(r.in[firstBad]), static_cast<double>(r.out[firstBad]));
    CHECK(AllFinite(r.out) && PeakAbs(r.out) <= c + 1e-6, "%s: output peak %.6g (all finite: %d), want <= %.6g", what,
          PeakAbs(r.out), AllFinite(r.out) ? 1 : 0, c + 1e-6);
    CHECK(cap.ClipperHits() - hitsBefore == over, "%s: ClipperHits rose by %u, want %zu (the samples the clamp changed)",
          what, cap.ClipperHits() - hitsBefore, over);
    CHECK(cap.LatencyFrames() == 0, "%s: LatencyFrames() is %u, want 0 (no delay while unprepared)", what,
          cap.LatencyFrames());
    CHECK(r.guardIntact, "%s: Process wrote past the end of the buffer", what);
}

// The same for a Leveler: every output sample must be finite and within +-1, in-range values untouched.
static void ExpectLevelerFailsClosed(sp::Leveler& lv, uint32_t bufCh, uint32_t frames, bool inPlace, const char* what) {
    const GuardedRun r = RunGuarded(FailClosedProbe(frames, bufCh, 1.0), frames, inPlace,
                                    [&lv](const float* pin, float* pout, uint32_t pn) {
                                        NoAlloc rt;
                                        lv.Process(pin, pout, pn);
                                    });
    size_t over = 0, firstBad = 0;
    const size_t wrong = FailClosedWrong(r.in, r.out, 1.0, 0.0, over, firstBad);
    CHECK(wrong == 0, "%s: %zu of %zu output samples are not sanitised and clamped to +-1 (first: sample %zu, in %g, out %g)",
          what, wrong, r.out.size(), firstBad, static_cast<double>(r.in[firstBad]), static_cast<double>(r.out[firstBad]));
    CHECK(AllFinite(r.out) && PeakAbs(r.out) <= 1.0, "%s: output peak %.6g (all finite: %d), want <= 1", what,
          PeakAbs(r.out), AllFinite(r.out) ? 1 : 0);
    CHECK(r.guardIntact, "%s: Process wrote past the end of the buffer", what);
}

// One Process call with a buffer whose channel count the processor cannot know (never prepared, 0 channels, or more
// than 65535): not one sample may be written. An output buffer of 7.0f stays 7.0f, an in-place buffer stays as it was.
// This is a caller error (see the top of SpDsp.h); the test pins that the processor then does not guess.
template <class Fn>
static void ExpectWritesNothing(Fn process, uint32_t bufCh, uint32_t frames, bool inPlace, const char* what) {
    const GuardedRun r = RunGuarded(FailClosedProbe(frames, bufCh, 1.0), frames, inPlace, process);
    const std::vector<float> want = inPlace ? r.in : std::vector<float>(r.in.size(), 7.0f);
    CHECK(SameBits(r.out, want) && r.guardIntact, "%s: Process wrote to a buffer whose channel count it cannot know", what);
}

static void T24_safetycap_formats() {
    const uint32_t channelCounts[] = {1, 2, 6, 8};
    const uint32_t rates[] = {44100, 48000, 96000};
    const float ceilingDb = -20.0f;
    const double limit = sp::DbToLinear(ceilingDb) + 1e-6;
    sp::SafetyCap reused;
    for (uint32_t ch : channelCounts) {
        for (uint32_t fs : rates) {
            sp::SafetyCap cap;
            CapInit(cap, fs, ch, ceilingDb);
            const size_t frames = Frames(0.5, static_cast<double>(fs));
            const std::vector<float> in = WhiteNoise(frames, ch, 0.9, 1000u + ch * 31u + fs);
            std::vector<float> out;
            CapRun(cap, in, ch, out);
            const double peak = PeakAbs(out);
            CHECK(peak <= limit, "%u ch at %u Hz: output peak %.6f exceeds the ceiling %.6f", ch, fs, peak, limit);
            CHECK(AllFinite(out), "%u ch at %u Hz: a non-finite output sample", ch, fs);
            CHECK(cap.ClipperHits() == 0, "%u ch at %u Hz: the final clamp changed %u samples, want 0 (the limiter must hold the ceiling by itself, on every channel)",
                  ch, fs, cap.ClipperHits());
            // Wide-band noise has inter-sample peaks well above its sample peaks, so the limiter rightly holds the
            // samples a few dB under the ceiling. The guard only has to prove the output is neither muted nor crushed.
            CHECK(PeakAbs(out, frames * ch / 2) >= 0.4 * sp::DbToLinear(ceilingDb),
                  "%u ch at %u Hz: the output is over-attenuated (peak %.6f)", ch, fs, PeakAbs(out, frames * ch / 2));
            if (ch == 2) {                                                // stereo link: one gain for both ears
                const size_t latency = cap.LatencyFrames();
                double worst = 0.0;
                size_t compared = 0;
                for (size_t n = latency; n < frames; ++n) {
                    const double a0 = in[(n - latency) * 2], a1 = in[(n - latency) * 2 + 1];
                    if (std::fabs(a0) >= 0.01 && std::fabs(a1) >= 0.01) {
                        worst = std::max(worst, std::fabs(out[n * 2] / a0 - out[n * 2 + 1] / a1));
                        ++compared;
                    }
                }
                CHECK(compared > frames / 2, "%u Hz: only %zu frames compared", fs, compared);
                CHECK(worst <= 1e-5, "%u Hz: the two channels' gains differ by %.3g, want <= 1e-5", fs, worst);
            }

            // A quiet signal (independent noise per channel, too small to ever reach the ceiling: a true peak is at
            // most about 2.6 times the largest sample) must come out as an exact copy delayed by LatencyFrames(), on
            // every channel. One object is re-prepared through all twelve formats.
            CapInit(reused, fs, ch, ceilingDb);
            const std::vector<float> qin = WhiteNoise(frames, ch, 0.03, 5000u + ch * 17u + fs);
            std::vector<float> qout;
            CapRun(reused, qin, ch, qout);
            const size_t lat = reused.LatencyFrames();
            const size_t wantLat = static_cast<size_t>(std::floor(0.005 * static_cast<double>(fs) + 0.5)) + 8;
            CHECK(lat == wantLat, "%u ch at %u Hz: latency %zu frames, want round(5 ms) + 8 = %zu", ch, fs, lat, wantLat);
            double worstQ = 0.0;
            for (size_t n = lat; n < frames; ++n)
                for (size_t c = 0; c < ch; ++c)
                    worstQ = std::max(worstQ, std::fabs(static_cast<double>(qout[n * ch + c]) - qin[(n - lat) * ch + c]));
            CHECK(worstQ <= 1e-6, "%u ch at %u Hz: a quiet signal is not a delayed copy (differs by %.3g)", ch, fs, worstQ);
            CHECK(reused.GainReductionDb() == 0.0f, "%u ch at %u Hz: a quiet signal shows %.4f dB of reduction", ch, fs,
                  reused.GainReductionDb());
        }
    }

    // Part B (not in the brief): the loudest channel decides, and every channel gets the same gain. Only the last
    // channel carries a loud 50 ms burst (1 kHz, 0.891); all the others carry a quiet 0.02 sine.
    const uint32_t linked[] = {2, 6, 8};
    for (uint32_t ch : linked) {
        sp::SafetyCap cap;
        CapInit(cap, 48000, ch, ceilingDb);
        std::vector<float> in(Frames(0.6, 48000.0) * ch, 0.0f);
        for (size_t n = 0; n < in.size() / ch; ++n) {
            const double phase = kTwoPi * 1000.0 * static_cast<double>(n) / 48000.0;
            for (uint32_t c = 0; c + 1 < ch; ++c) in[n * ch + c] = static_cast<float>(0.02 * std::sin(phase));
            in[n * ch + ch - 1] = (n >= 24000 && n < 26400) ? static_cast<float>(0.891 * std::sin(phase)) : 0.0f;
        }
        std::vector<float> out;
        CapRun(cap, in, ch, out);
        CHECK(PeakAbs(out) <= limit && cap.ClipperHits() == 0, "%u ch: loud burst in the last channel: peak %.6f, %u clamp hits",
              ch, PeakAbs(out), cap.ClipperHits());
        const size_t latency = cap.LatencyFrames();
        double worst = 0.0, lowestGain = 1.0;
        size_t compared = 0;
        for (size_t n = 24000 + latency + 300; n < 26400 + latency - 300; ++n) {     // inside the burst
            const double quietIn = in[(n - latency) * ch], loudIn = in[(n - latency) * ch + ch - 1];
            if (std::fabs(quietIn) < 0.01 || std::fabs(loudIn) < 0.2) continue;
            const double gQuiet = out[n * ch] / quietIn, gLoud = out[n * ch + ch - 1] / loudIn;
            worst = std::max(worst, std::fabs(gQuiet - gLoud));
            lowestGain = std::min(lowestGain, gLoud);
            ++compared;
        }
        CHECK(compared > 100, "%u ch: only %zu samples compared", ch, compared);
        CHECK(lowestGain < 0.2, "%u ch: the loud channel is turned down only to %.3f, want about 0.11", ch, lowestGain);
        CHECK(worst <= 1e-5, "%u ch: the quiet channels' gain differs from the loud channel's by %.3g, want <= 1e-5", ch, worst);
    }

    // Part C (not in the brief): the accepted range is 8 kHz to 768 kHz and 1 to 64 channels, and both ends of it
    // work. What Prepare does with a format outside that range is part D.
    struct Format { uint32_t fs, ch; };
    const Format edges[] = {{8000, 1}, {8000, 2}, {768000, 1}, {768000, 8}, {48000, 64}};
    for (const Format& f : edges) {
        sp::SafetyCap cap;
        CapInit(cap, f.fs, f.ch, ceilingDb);
        const size_t frames = Frames(0.3, static_cast<double>(f.fs));
        std::vector<float> o;
        CapRun(cap, WhiteNoise(frames, f.ch, 0.9, 77u + f.ch), f.ch, o);
        CHECK(PeakAbs(o) <= limit && cap.ClipperHits() == 0, "%u ch at %u Hz: loud noise peaks at %.6f with %u clamp hits",
              f.ch, f.fs, PeakAbs(o), cap.ClipperHits());
        CHECK(PeakAbs(o, frames * f.ch / 2) >= 0.4 * sp::DbToLinear(ceilingDb), "%u ch at %u Hz: the output is over-attenuated (peak %.6f)",
              f.ch, f.fs, PeakAbs(o, frames * f.ch / 2));
        sp::SafetyCap quiet;
        CapInit(quiet, f.fs, f.ch, ceilingDb);
        const std::vector<float> qin = WhiteNoise(frames, f.ch, 0.03, 91u + f.ch);
        std::vector<float> qout;
        CapRun(quiet, qin, f.ch, qout);
        const size_t lat = quiet.LatencyFrames();
        const size_t wantLat = static_cast<size_t>(std::floor(0.005 * static_cast<double>(f.fs) + 0.5)) + 8;
        double worstQ = 0.0;
        for (size_t n = lat; n < frames; ++n)
            for (size_t c = 0; c < f.ch; ++c)
                worstQ = std::max(worstQ, std::fabs(static_cast<double>(qout[n * f.ch + c]) - qin[(n - lat) * f.ch + c]));
        CHECK(lat == wantLat && worstQ <= 1e-6, "%u ch at %u Hz: latency %zu (want %zu), a quiet signal differs from its delayed copy by %.3g",
              f.ch, f.fs, lat, wantLat, worstQ);

        sp::Leveler lv;
        lv.Prepare(f.fs, f.ch, nullptr);
        std::vector<float> lo;
        LvRun(lv, Sine(static_cast<double>(f.fs), f.ch, 1000.0, 0.562, Frames(0.7, static_cast<double>(f.fs))), f.ch, lo);
        CHECK(lv.GainReductionDb() > 5.0f, "%u ch at %u Hz: the leveler reduces the loud sine by only %.3f dB", f.ch, f.fs,
              lv.GainReductionDb());
    }

    // Part D (fail closed; review I1, ruling 15): a format Prepare refuses leaves the object unprepared, and an
    // unprepared processor must not hand its input on to the headphones. Process then writes frames x (the channel
    // count that Prepare call was given) samples: NaN and Inf as 0 and everything else clamped, with no gain and no
    // delay, to +-the ceiling in force (Safety Cap) or +-1 (Leveler). When that channel count is 0 or past 65535 (a
    // WAVEFORMATEX count is 16 bits wide), or when Prepare was never called, nothing is known about the buffer and
    // Process writes nothing (a caller error). The telemetry shows no values from before the refusal. Every case runs
    // twice: into a separate output buffer pre-filled with 7.0f (about +17 dBFS), and in place.
    struct Refused { uint32_t fs, ch, bufCh; bool writes; uint32_t frames; };
    const Refused refused[] = {{0, 2, 2, true, 37},          {7999, 2, 2, true, 37},        {768001, 2, 2, true, 37},
                               {4000000000u, 2, 2, true, 37}, {48000, 65, 65, true, 37},     {48000, 65535, 65535, true, 2},
                               {48000, 0, 2, false, 37},      {48000, 65536, 2, false, 37},  {48000, 4000000000u, 2, false, 37}};
    const double capLinear = sp::DbToLinear(ceilingDb);
    for (const Refused& f : refused) {
        const std::vector<float> loud = Sine(48000.0, 2, 1000.0, 0.891, 4800);
        for (int mode = 0; mode < 2; ++mode) {
            const bool inPlace = mode == 1;
            char what[128];
            std::vector<float> scratch;

            sp::SafetyCap cap;
            CapInit(cap, 48000, 2, ceilingDb);
            CapRun(cap, loud, 2, scratch);                                       // a good format with loud sound in it ...
            CHECK(cap.GainReductionDb() > 6.0f, "SafetyCap: the loud sine is reduced by only %.2f dB before the refused Prepare",
                  cap.GainReductionDb());
            cap.Prepare(f.fs, f.ch);                                             // ... then a refused one on the same object
            std::snprintf(what, sizeof what, "SafetyCap, refused %u Hz / %u ch, %s", f.fs, f.ch, inPlace ? "in place" : "in != out");
            CHECK(cap.LatencyFrames() == 0 && cap.GainReductionDb() == 0.0f && cap.ClipperHits() == 0 &&
                      cap.InputPeakDb() == sp::kFloorDb && cap.OutputPeakDb() == sp::kFloorDb,
                  "%s: stale state (latency %u, reduction %.3f, hits %u, input peak %.3f, output peak %.3f)", what,
                  cap.LatencyFrames(), cap.GainReductionDb(), cap.ClipperHits(), cap.InputPeakDb(), cap.OutputPeakDb());
            if (f.writes) {
                ExpectCapFailsClosed(cap, f.bufCh, f.frames, inPlace, capLinear, what);
            } else {
                ExpectWritesNothing([&cap](const float* pin, float* pout, uint32_t pn) {
                                        NoAlloc rt;
                                        cap.Process(pin, pout, pn);
                                    },
                                    f.bufCh, f.frames, inPlace, what);
            }

            sp::Leveler lv;
            lv.Prepare(48000, 2, nullptr);
            LvRun(lv, Sine(48000.0, 2, 1000.0, 0.562, 48000), 2, scratch);       // loud enough to reduce
            CHECK(lv.GainReductionDb() > 5.0f, "Leveler: the loud sine is reduced by only %.2f dB before the refused Prepare",
                  lv.GainReductionDb());
            lv.Prepare(f.fs, f.ch, nullptr);
            std::snprintf(what, sizeof what, "Leveler, refused %u Hz / %u ch, %s", f.fs, f.ch, inPlace ? "in place" : "in != out");
            CHECK(lv.GainReductionDb() == 0.0f && lv.InputLoudnessDb() == sp::kFloorDb,
                  "%s: stale state (reduction %.3f, loudness %.3f)", what, lv.GainReductionDb(), lv.InputLoudnessDb());
            if (f.writes) {
                ExpectLevelerFailsClosed(lv, f.bufCh, f.frames, inPlace, what);
            } else {
                ExpectWritesNothing([&lv](const float* pin, float* pout, uint32_t pn) {
                                        NoAlloc rt;
                                        lv.Process(pin, pout, pn);
                                    },
                                    f.bufCh, f.frames, inPlace, what);
            }
        }

        sp::LoudnessMeter m;                                                     // the meter has no output to protect:
        m.Prepare(48000, 2, nullptr);                                            // it ignores Add and reads the floor
        MeterFeedAll(m, Sine(48000.0, 2, 1000.0, 0.1, 48000), 2);
        m.Prepare(f.fs, f.ch, nullptr);
        MeterFeedAll(m, loud, 2);
        CHECK(m.MomentaryLufs() == sp::kFloorDb, "LoudnessMeter, refused format %u Hz / %u ch: reads %.3f, want the floor",
              f.fs, f.ch, m.MomentaryLufs());
    }

    // Never prepared at all: the channel count is unknown, so Process writes nothing (a caller error). A refused
    // Prepare on an object that was never prepared does give the channel count: the Safety Cap then clamps at its
    // default ceiling, -8 dBFS, and the Leveler at +-1.
    for (int mode = 0; mode < 2; ++mode) {
        const bool inPlace = mode == 1;
        sp::SafetyCap unborn;
        sp::Leveler unbornLv;
        ExpectWritesNothing([&unborn](const float* pin, float* pout, uint32_t pn) {
                                NoAlloc rt;
                                unborn.Process(pin, pout, pn);
                            },
                            2, 37, inPlace, inPlace ? "SafetyCap never prepared, in place" : "SafetyCap never prepared, in != out");
        ExpectWritesNothing([&unbornLv](const float* pin, float* pout, uint32_t pn) {
                                NoAlloc rt;
                                unbornLv.Process(pin, pout, pn);
                            },
                            2, 37, inPlace, inPlace ? "Leveler never prepared, in place" : "Leveler never prepared, in != out");
        unborn.Prepare(7999, 2);
        unbornLv.Prepare(7999, 2, nullptr);
        ExpectCapFailsClosed(unborn, 2, 37, inPlace, sp::DbToLinear(-8.0f),
                             inPlace ? "SafetyCap never prepared, refused 7999 Hz, in place"
                                     : "SafetyCap never prepared, refused 7999 Hz, in != out");
        ExpectLevelerFailsClosed(unbornLv, 2, 37, inPlace,
                                 inPlace ? "Leveler never prepared, refused 7999 Hz, in place"
                                         : "Leveler never prepared, refused 7999 Hz, in != out");
    }

    // Part E (fail closed, out of memory): the same when Prepare runs out of memory half way. The allocation hook
    // fails the k-th allocation of Prepare(96000, 8) for k = 0, 1, 2 ... until Prepare no longer throws. Every failure
    // must leave the object unprepared with the channel count of that call (8), and the next Prepare must work.
    for (int mode = 0; mode < 2; ++mode) {
        const bool inPlace = mode == 1;
        char what[128];
        std::vector<float> scratch;

        size_t capFailures = 0;
        for (long k = 0; k < 32; ++k) {
            sp::SafetyCap cap;
            CapInit(cap, 48000, 2, ceilingDb);
            CapRun(cap, Sine(48000.0, 2, 1000.0, 0.891, 4800), 2, scratch);
            bool threw = false;
            g_failAllocIn = k;
            try {
                cap.Prepare(96000, 8);
            } catch (const std::bad_alloc&) {
                threw = true;
            }
            g_failAllocIn = -1;
            std::snprintf(what, sizeof what, "SafetyCap, allocation %ld of Prepare(96000, 8) fails, %s", k,
                          inPlace ? "in place" : "in != out");
            if (!threw) {                                                        // k is past the last allocation
                CHECK(cap.LatencyFrames() == 488, "%s: no failure, but the latency is %u, want 488", what, cap.LatencyFrames());
                break;
            }
            ++capFailures;
            CHECK(cap.LatencyFrames() == 0 && cap.GainReductionDb() == 0.0f && cap.ClipperHits() == 0 &&
                      cap.InputPeakDb() == sp::kFloorDb,
                  "%s: stale state (latency %u, reduction %.3f, hits %u, input peak %.3f)", what, cap.LatencyFrames(),
                  cap.GainReductionDb(), cap.ClipperHits(), cap.InputPeakDb());
            ExpectCapFailsClosed(cap, 8, 37, inPlace, capLinear, what);
            cap.Prepare(96000, 8);                                               // memory is back: normal processing again
            CHECK(cap.LatencyFrames() == 488 && cap.ClipperHits() == 0,
                  "%s: the next Prepare gives latency %u and %u clamp hits, want 488 and 0", what, cap.LatencyFrames(),
                  cap.ClipperHits());
        }
        CHECK(capFailures >= 4, "SafetyCap: only %zu of the first 32 allocations of Prepare could be made to fail, want at least 4",
              capFailures);

        size_t lvFailures = 0;
        for (long k = 0; k < 32; ++k) {
            sp::Leveler lv;
            lv.Prepare(48000, 2, nullptr);
            LvRun(lv, Sine(48000.0, 2, 1000.0, 0.562, 48000), 2, scratch);
            bool threw = false;
            g_failAllocIn = k;
            try {
                lv.Prepare(96000, 8, nullptr);
            } catch (const std::bad_alloc&) {
                threw = true;
            }
            g_failAllocIn = -1;
            std::snprintf(what, sizeof what, "Leveler, allocation %ld of Prepare(96000, 8) fails, %s", k,
                          inPlace ? "in place" : "in != out");
            std::vector<float> loud8;
            if (!threw) {                                                        // k is past the last allocation
                LvRun(lv, Sine(96000.0, 8, 1000.0, 0.562, Frames(0.7, 96000.0)), 8, loud8);
                CHECK(lv.GainReductionDb() > 5.0f, "%s: no failure, but a loud sine is reduced by only %.2f dB", what,
                      lv.GainReductionDb());
                break;
            }
            ++lvFailures;
            CHECK(lv.GainReductionDb() == 0.0f && lv.InputLoudnessDb() == sp::kFloorDb,
                  "%s: stale state (reduction %.3f, loudness %.3f)", what, lv.GainReductionDb(), lv.InputLoudnessDb());
            ExpectLevelerFailsClosed(lv, 8, 37, inPlace, what);
            lv.Prepare(96000, 8, nullptr);                                       // memory is back: normal processing again
            LvRun(lv, Sine(96000.0, 8, 1000.0, 0.562, Frames(0.7, 96000.0)), 8, loud8);
            CHECK(lv.GainReductionDb() > 5.0f, "%s: after the next Prepare a loud sine is reduced by only %.2f dB", what,
                  lv.GainReductionDb());
        }
        CHECK(lvFailures >= 4, "Leveler: only %zu of the first 32 allocations of Prepare could be made to fail, want at least 4",
              lvFailures);
    }

    // Part F (fail closed, the ceiling in force): an unprepared Safety Cap clamps at the ceiling SetParams has set,
    // and that ceiling moves as a prepared one's does: lower at once, higher by a linear ramp in dB (relaxRampMs,
    // 100 ms), and "disabled" means 0 dBFS, not a way round the clamp. No audio has gone through the cap before the
    // refusal, so its first fail-closed Process call is what ends the "first ceiling applies at once" period.
    {
        sp::SafetyCap cap;
        CapInit(cap, 48000, 2, ceilingDb);                                       // -20 dBFS ...
        cap.Prepare(7999, 2);                                                    // ... then a refused format
        const auto dc = [&cap](size_t dcFrames, float level) {
            std::vector<float> buf(dcFrames * 2, level);
            CapRunInPlace(cap, buf, 2);
            return buf;
        };
        const auto flatAt = [](const std::vector<float>& v, size_t fromFrame, double want) {
            for (size_t i = fromFrame * 2; i < v.size(); ++i)
                if (!(std::fabs(static_cast<double>(v[i]) - want) <= 1e-6)) return false;
            return true;
        };
        CHECK(flatAt(dc(480, 0.9f), 0, sp::DbToLinear(-20.0f)), "unprepared, ceiling -20 dBFS: DC 0.9 does not come out as %.6f",
              static_cast<double>(sp::DbToLinear(-20.0f)));

        sp::SafetyCapParams p;
        p.ceilingDb = -30.0f;
        CapSet(cap, p);
        CHECK(flatAt(dc(480, 0.9f), 0, sp::DbToLinear(-30.0f)),
              "unprepared: a lower ceiling (-30 dBFS) does not apply at once (want DC 0.9 to come out as %.6f)",
              static_cast<double>(sp::DbToLinear(-30.0f)));

        p.ceilingDb = -10.0f;
        CapSet(cap, p);
        const std::vector<float> ramp = dc(6000, 0.9f);                          // 4800 frames of ramp, then 1200 flat
        const double rampLo = sp::DbToLinear(-30.0f), rampMid = sp::DbToLinear(-20.0f), rampHi = sp::DbToLinear(-10.0f);
        CHECK(std::fabs(ramp[0] / rampLo - 1.0) <= 0.02, "unprepared: a higher ceiling (-10 dBFS) starts at %.6f, want about %.6f (a ramp, not a jump)",
              static_cast<double>(ramp[0]), rampLo);
        CHECK(std::fabs(ramp[2399 * 2] / rampMid - 1.0) <= 0.01, "unprepared: half way through the ramp the ceiling is %.6f, want about %.6f",
              static_cast<double>(ramp[2399 * 2]), rampMid);
        CHECK(flatAt(ramp, 4800, rampHi), "unprepared: the ceiling has not arrived at %.6f after the ramp", rampHi);
        bool rises = true, same = true;
        for (size_t n = 1; n < 6000; ++n) {
            rises = rises && ramp[n * 2] >= ramp[(n - 1) * 2];
            same = same && ramp[n * 2] == ramp[n * 2 + 1];
        }
        CHECK(rises && same && PeakAbs(ramp) <= rampHi + 1e-6,
              "unprepared: the ramp is not monotonic (%d), or the two channels differ (%d), or it is above %.6f (peak %.6f)",
              rises ? 1 : 0, same ? 1 : 0, rampHi, PeakAbs(ramp));

        p.enabled = false;                                                       // disabled: the ceiling ramps up to 0 dBFS
        CapSet(cap, p);
        const std::vector<float> relaxed = dc(6000, 7.0f);
        CHECK(AllFinite(relaxed) && PeakAbs(relaxed) <= 1.0 && flatAt(relaxed, 5000, 1.0),
              "unprepared and disabled: DC 7.0 must end up clamped at 1.0 (0 dBFS), peak %.6f", PeakAbs(relaxed));
    }

    // Part G (review M3): a Safety Cap that is limiting is prepared again for another format. Nothing of the old
    // format may come out of it: the delay line, filters and envelope are all rebuilt for 96 kHz / 8 channels. Loud
    // noise afterwards stays under the ceiling from the very first frame on, and quiet noise comes out as an exact copy
    // delayed by LatencyFrames(), which it could not if the old envelope were still pulling the gain down.
    for (int quietMode = 0; quietMode < 2; ++quietMode) {
        sp::SafetyCap cap;
        CapInit(cap, 48000, 2, ceilingDb);
        std::vector<float> scratch;
        CapRun(cap, WhiteNoise(Frames(0.5, 48000.0), 2, 0.9, 2024u), 2, scratch);
        CHECK(cap.GainReductionDb() > 6.0f, "before the second Prepare the cap reduces by only %.2f dB, want it limiting hard",
              cap.GainReductionDb());
        cap.Prepare(96000, 8);
        CHECK(cap.GainReductionDb() == 0.0f && cap.ClipperHits() == 0 && cap.InputPeakDb() == sp::kFloorDb &&
                  cap.OutputPeakDb() == sp::kFloorDb,
              "after the second Prepare: stale state (reduction %.3f, hits %u, input peak %.3f, output peak %.3f)",
              cap.GainReductionDb(), cap.ClipperHits(), cap.InputPeakDb(), cap.OutputPeakDb());
        const size_t latG = cap.LatencyFrames();
        CHECK(latG == 488, "96 kHz latency %zu frames, want round(5 ms) + 8 = 488", latG);
        const std::vector<float> in8 = WhiteNoise(Frames(0.5, 96000.0), 8, quietMode == 1 ? 0.03 : 0.9, 2025u + quietMode);
        std::vector<float> out8;
        CapRun(cap, in8, 8, out8);
        CHECK(PeakAbs(out8, 0, latG * 8) == 0.0, "%s noise: the first %zu frames after the second Prepare are not silent (peak %.6f)",
              quietMode == 1 ? "quiet" : "loud", latG, PeakAbs(out8, 0, latG * 8));
        CHECK(AllFinite(out8) && PeakAbs(out8) <= limit && cap.ClipperHits() == 0,
              "%s noise after the second Prepare: output peak %.6f (limit %.6f), %u clamp hits", quietMode == 1 ? "quiet" : "loud",
              PeakAbs(out8), limit, cap.ClipperHits());
        if (quietMode == 0) {
            CHECK(PeakAbs(out8, out8.size() / 2) >= 0.4 * sp::DbToLinear(ceilingDb),
                  "loud noise after the second Prepare is over-attenuated (peak %.6f)", PeakAbs(out8, out8.size() / 2));
        } else {
            double worstCopy = 0.0;
            for (size_t n = latG; n < in8.size() / 8; ++n)
                for (size_t c = 0; c < 8; ++c)
                    worstCopy = std::max(worstCopy, std::fabs(static_cast<double>(out8[n * 8 + c]) - in8[(n - latG) * 8 + c]));
            CHECK(worstCopy <= 1e-6 && cap.GainReductionDb() == 0.0f,
                  "quiet noise after the second Prepare is not a delayed copy (differs by %.3g, reduction %.3f dB)", worstCopy,
                  cap.GainReductionDb());
        }
    }
}

static void T25_safetycap_chunk_size_independent() {
    const std::vector<float> in = Programme(48000.0, 2, 3.0, 777u);
    const size_t chunks[] = {1, 7, 480, 4096};
    std::vector<float> reference;
    for (size_t chunk : chunks) {
        sp::SafetyCap cap;
        CapInit(cap, 48000, 2, -20.0f);
        std::vector<float> out;
        CapRun(cap, in, 2, out, chunk);
        CHECK(cap.ClipperHits() == 0, "chunk size %zu: the final clamp changed %u samples, want 0", chunk, cap.ClipperHits());
        if (reference.empty()) { reference = out; continue; }
        const double worst = MaxAbsDiff(reference, out);
        CHECK(worst <= 1e-6, "chunk size %zu differs from chunk size 1 by %.3g, want <= 1e-6", chunk, worst);
    }
    // In-place processing gives the same result as out-of-place.
    {
        sp::SafetyCap cap;
        CapInit(cap, 48000, 2, -20.0f);
        std::vector<float> buf = in;
        CapRunInPlace(cap, buf, 2);
        const double worst = MaxAbsDiff(reference, buf);
        CHECK(worst <= 1e-6, "in-place output differs from out-of-place by %.3g, want <= 1e-6", worst);
    }
    // Reset() returns the object to its just-prepared state.
    {
        sp::SafetyCap cap;
        CapInit(cap, 48000, 2, -20.0f);
        std::vector<float> first, second;
        CapRun(cap, in, 2, first);
        cap.Reset();
        CapRun(cap, in, 2, second);
        const double worst = MaxAbsDiff(first, second);
        CHECK(worst <= 1e-6, "after Reset() the same input gives a different output (by %.3g)", worst);
    }
    CHECK(PeakAbs(in) > 0.8 && PeakAbs(reference) <= sp::DbToLinear(-20.0f) + 1e-6,
          "the safety cap left the loud parts alone: the test proves nothing");
}

// ================================================================================================
// Tests 26-27: the pure functions
// ================================================================================================

static void T26_ceiling_rule() {
    CHECK(sp::CeilingDb(-20.0f, 12.0f) == -8.0f, "CeilingDb(-20, 12) = %g, want -8", sp::CeilingDb(-20.0f, 12.0f));
    CHECK(sp::CeilingDb(-5.0f, 12.0f) == -1.0f, "CeilingDb(-5, 12) = %g, want -1", sp::CeilingDb(-5.0f, 12.0f));
    CHECK(sp::CeilingDb(-60.0f, 0.0f) == -60.0f, "CeilingDb(-60, 0) = %g, want -60", sp::CeilingDb(-60.0f, 0.0f));
    // The dB helpers (their contract is in the header comments).
    CHECK(Near(sp::DbToLinear(-20.0f), 0.1, 1e-7), "DbToLinear(-20) = %.9f", sp::DbToLinear(-20.0f));
    CHECK(sp::DbToLinear(0.0f) == 1.0f, "DbToLinear(0) = %.9f", sp::DbToLinear(0.0f));
    CHECK(Near(sp::LinearToDb(0.1f), -20.0, 1e-4), "LinearToDb(0.1) = %.6f", sp::LinearToDb(0.1f));
    CHECK(sp::LinearToDb(1.0f) == 0.0f, "LinearToDb(1) = %.6f", sp::LinearToDb(1.0f));
    CHECK(sp::LinearToDb(1e-6f) == sp::kFloorDb, "LinearToDb(1e-6) = %.3f, want the floor", sp::LinearToDb(1e-6f));
    CHECK(sp::LinearToDb(0.0f) == sp::kFloorDb, "LinearToDb(0) = %.3f, want the floor", sp::LinearToDb(0.0f));
    CHECK(sp::LinearToDb(std::numeric_limits<float>::quiet_NaN()) == sp::kFloorDb, "LinearToDb(NaN) is not the floor");
    CHECK(sp::LinearToDb(std::numeric_limits<float>::infinity()) == sp::kFloorDb, "LinearToDb(Inf) is not the floor");
    // Just above the floor threshold the conversion is the plain 20*log10; negative values are below it.
    CHECK(Near(sp::LinearToDb(2e-6f), -113.979, 1e-2), "LinearToDb(2e-6) = %.4f, want -113.98", sp::LinearToDb(2e-6f));
    CHECK(Near(sp::LinearToDb(1e-4f), -80.0, 1e-3), "LinearToDb(1e-4) = %.4f, want -80", sp::LinearToDb(1e-4f));
    CHECK(sp::LinearToDb(-0.5f) == sp::kFloorDb, "LinearToDb(-0.5) = %.3f, want the floor", sp::LinearToDb(-0.5f));
    CHECK(sp::LinearToDb(-std::numeric_limits<float>::infinity()) == sp::kFloorDb, "LinearToDb(-Inf) is not the floor");
    CHECK(Near(sp::DbToLinear(-6.0206f), 0.5, 1e-5), "DbToLinear(-6.0206) = %.7f, want 0.5", sp::DbToLinear(-6.0206f));
    CHECK(Near(sp::DbToLinear(6.0206f), 2.0, 1e-4), "DbToLinear(+6.0206) = %.7f, want 2.0", sp::DbToLinear(6.0206f));
    // The ceiling rule with a NaN margin or cap falls back to the hard limit of -1 dB.
    CHECK(sp::CeilingDb(std::numeric_limits<float>::quiet_NaN(), 12.0f) == -1.0f, "CeilingDb(NaN, 12) is not -1");
    CHECK(sp::CeilingDb(-20.0f, std::numeric_limits<float>::quiet_NaN()) == -1.0f, "CeilingDb(-20, NaN) is not -1");
}

static void T27_channel_weights_from_mask() {
    float w[6] = {};
    sp::ChannelWeightsFromMask(0x3F, 6, w);                               // 5.1
    const float want[6] = {1.0f, 1.0f, 1.0f, 0.0f, 1.41f, 1.41f};
    for (int i = 0; i < 6; ++i) CHECK(w[i] == want[i], "5.1 channel %d: weight %g, want %g", i, w[i], want[i]);

    float s[2] = {7.0f, 7.0f};
    sp::ChannelWeightsFromMask(0, 2, s);                                  // no mask: every channel 1.0
    CHECK(s[0] == 1.0f && s[1] == 1.0f, "mask 0: weights (%g, %g), want (1, 1)", s[0], s[1]);

    float x[8] = {};
    sp::ChannelWeightsFromMask(0x63F, 8, x);                              // 5.1 plus side left and right
    const float wantX[8] = {1.0f, 1.0f, 1.0f, 0.0f, 1.41f, 1.41f, 1.41f, 1.41f};
    for (int i = 0; i < 8; ++i) CHECK(x[i] == wantX[i], "7.1 channel %d: weight %g, want %g", i, x[i], wantX[i]);

    float y[4] = {7.0f, 7.0f, 7.0f, 7.0f};
    sp::ChannelWeightsFromMask(0x3, 4, y);                                // more channels than mask bits
    CHECK(y[0] == 1.0f && y[1] == 1.0f && y[2] == 1.0f && y[3] == 1.0f, "extra channels are not weighted 1.0");
}

// ================================================================================================
// Runner
// ================================================================================================

struct TestCase {
    const char* name;
    void (*fn)();
};

#define TEST(fn) { #fn + 4, fn }     // strips the "Tnn_" prefix from the function name

static const TestCase kTests[] = {
    TEST(T01_kweighting_48k_matches_bs1770),
    TEST(T02_loudness_1k_sine_stereo_minus20),
    TEST(T03_loudness_mono_minus20_reads_minus23),
    TEST(T04_loudness_silence_is_floor),
    TEST(T05_loudness_other_rates_agree),
    TEST(T06_loudness_window_is_400ms),
    TEST(T07_leveler_quiet_input_untouched),
    TEST(T08_leveler_loud_input_settles_at_cap),
    TEST(T09_leveler_never_boosts),
    TEST(T10_leveler_release_returns_to_unity),
    TEST(T11_leveler_disabled_ramps_to_unity),
    TEST(T12_leveler_nonfinite_input_outputs_zero),
    TEST(T13_leveler_in_place_matches_out_of_place),
    TEST(T14_leveler_chunk_size_independent),
    TEST(T15_safetycap_burst_never_exceeds_ceiling),
    TEST(T16_safetycap_below_ceiling_is_delayed_copy),
    TEST(T17_safetycap_true_peak_12k),
    TEST(T18_safetycap_step_holds_ceiling),
    TEST(T19_safetycap_release),
    TEST(T20_safetycap_disabled_ramps_to_0dbfs),
    TEST(T21_safetycap_tightening_applies_at_once),
    TEST(T22_safetycap_never_louder_than_input),
    TEST(T23_safetycap_nonfinite_safe),
    TEST(T24_safetycap_formats),
    TEST(T25_safetycap_chunk_size_independent),
    TEST(T26_ceiling_rule),
    TEST(T27_channel_weights_from_mask),
};

// The brief lists exactly 27 tests; a missing or extra registration is a compile error, not a silent gap.
static_assert(sizeof(kTests) / sizeof(kTests[0]) == 27, "the brief pins exactly 27 tests");

int main() {
    const int total = static_cast<int>(sizeof(kTests) / sizeof(kTests[0]));
    int failedTests = 0;
    for (const TestCase& t : kTests) {
        g_failCount = 0;
        g_rtViolations = 0;
        t.fn();
        if (g_rtViolations != 0)
            Fail(0, "%lu heap allocation(s) or free(s) inside a real-time call (Process, Add or SetParams)",
                 g_rtViolations);
        if (g_failCount == 0) {
            std::printf("PASS %s\n", t.name);
        } else {
            ++failedTests;
            const int shown = std::min(g_failCount, kMaxMessages);
            for (int i = 0; i < shown; ++i) std::printf("FAIL %s: %s\n", t.name, g_messages[i]);
            if (g_failCount > shown) std::printf("FAIL %s: ... and %d more failed check(s)\n", t.name, g_failCount - shown);
        }
    }
    if (failedTests == 0) std::printf("ALL %d PASSED\n", total);
    else std::printf("FAILED: %d of %d tests\n", failedTests, total);
    std::fflush(stdout);
    return failedTests == 0 ? 0 : 1;
}
