// SpApoBase.h - COM and APO plumbing shared by the Leveler and the Safety Cap.
//
// SpApoBase sits on the SDK's CBaseAudioProcessingObject and adds:
//   - COM without ATL, with aggregation: a non-delegating inner IUnknown, an outer pointer (the caller's outer
//     unknown, or the object's own inner unknown) and delegating public QueryInterface/AddRef/Release;
//   - Initialize, the two format-negotiation calls, LockForProcess/UnlockForProcess, GetLatency, Reset;
//   - IAudioSystemEffects2 (an empty effects list);
//   - APOProcess, the real-time path. It never allocates, frees, locks, waits, logs, touches files or calls the OS.
//     The only exceptions are _mm_getcsr/_mm_setcsr (flush-to-zero and denormals-are-zero around the work).
//
// The two effects supply the DSP through the On* hooks. What leaves APOProcess is always finite and never beyond
// limit_ (the effect's ceiling): the last step is an integer-compare clamp that does not trust the DSP.
#pragma once

#include "SpRuntime.h"  // first: it defines NOMINMAX before <windows.h>

#include <objbase.h>
#include <baseaudioprocessingobject.h>
#include <audioengineextensionapo.h>
#include <audiomediatype.h>
#include <xmmintrin.h>

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <new>

namespace sp {
namespace apo {

// What the plug-in accepts: float32, 1 to 32 channels, 8000 to 384000 Hz.
constexpr uint32_t kMaxChannels = 32;
constexpr uint32_t kMinRate = 8000;
constexpr uint32_t kMaxRate = 384000;

// Input beyond this is garbage, not music (+60 dBFS). It is cut before the DSP so that one wild sample cannot
// drive the loudness meter or the limiter state into a long, deep gain reduction.
constexpr float kSanitiseLimit = 1000.0f;

// The registration properties of the two classes (static storage: the base class keeps a pointer to them).
const APO_REG_PROPERTIES* LevelerRegProps();
const APO_REG_PROPERTIES* SafetyCapRegProps();

// ---- helpers on the audio path (header-only so that they inline) --------------------------------------------

inline uint32_t FloatBits(float f) {
    uint32_t b;
    std::memcpy(&b, &f, sizeof b);
    return b;
}

inline float BitsFloat(uint32_t b) {
    float f;
    std::memcpy(&f, &b, sizeof f);
    return f;
}

// Walks n samples. NaN and Inf become 0. A finite sample beyond +-limit is cut to +-limit and counted in *hits.
// Returns the largest |sample| left. Integer compares only: they cannot be changed by the floating-point mode, and
// for non-negative floats the bit patterns sort like the values. The sign of limit is ignored; a limit that is NaN
// or Inf means 0 (silence): a broken limit must never switch the clamp off.
inline float ClampBlock(float* p, size_t n, float limit, uint32_t* hits) {
    uint32_t limitBits = FloatBits(limit) & 0x7FFFFFFFu;
    if (limitBits >= 0x7F800000u) limitBits = 0;
    uint32_t peak = 0;
    uint32_t hit = 0;
    for (size_t i = 0; i < n; ++i) {
        const uint32_t b = FloatBits(p[i]);
        uint32_t a = b & 0x7FFFFFFFu;
        if (a > limitBits) {
            if (a >= 0x7F800000u) {  // Inf or NaN
                p[i] = 0.0f;
                a = 0;
            } else {
                p[i] = BitsFloat((b & 0x80000000u) | limitBits);
                a = limitBits;
                ++hit;
            }
        }
        if (a > peak) peak = a;
    }
    if (hits) *hits += hit;
    return BitsFloat(peak);
}

// Copies n samples from src to dst (the same buffer is allowed) with the same rule, at kSanitiseLimit.
inline void SanitiseCopy(const float* src, float* dst, size_t n) {
    const uint32_t limitBits = FloatBits(kSanitiseLimit);
    for (size_t i = 0; i < n; ++i) {
        const uint32_t b = FloatBits(src[i]);
        const uint32_t a = b & 0x7FFFFFFFu;
        if (a > limitBits) {
            dst[i] = (a >= 0x7F800000u) ? 0.0f : BitsFloat((b & 0x80000000u) | limitBits);
        } else {
            dst[i] = src[i];
        }
    }
}

// Flush-to-zero and denormals-are-zero for the scope; the caller's MXCSR comes back at the end.
class FtzDazScope {
public:
    FtzDazScope() : saved_(_mm_getcsr()) { _mm_setcsr(saved_ | 0x8040u); }
    ~FtzDazScope() { _mm_setcsr(saved_); }
    FtzDazScope(const FtzDazScope&) = delete;
    FtzDazScope& operator=(const FtzDazScope&) = delete;

private:
    unsigned int saved_;
};

// The largest float that is not above 10^(db/20). The DLL's own final clamp must never sit above the exact ceiling.
float CeilingLinearFloor(float db);

// Maps an AudioProcessingMode GUID to an SpMode (GUID_NULL: unknown; any other GUID that is not listed: other).
uint32_t ModeFromGuid(const GUID& mode);

// ---- the base class --------------------------------------------------------------------------------------------

class SpApoBase : public CBaseAudioProcessingObject, public IAudioSystemEffects2 {
public:
    // The object's own, non-delegating IUnknown: what the class factory hands out first (it holds one reference).
    IUnknown* InnerUnknown() { return &inner_; }

    // ---- IUnknown (delegating: they forward to the outer unknown; one override serves all four IUnknown bases) ----
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppvObject) override;
    ULONG STDMETHODCALLTYPE AddRef() override;
    ULONG STDMETHODCALLTYPE Release() override;

    // ---- IAudioProcessingObject ----
    STDMETHOD(Initialize)(UINT32 cbDataSize, BYTE* pbyData) override;
    STDMETHOD(IsInputFormatSupported)(IAudioMediaType* pOutputFormat, IAudioMediaType* pRequestedInputFormat,
                                      IAudioMediaType** ppSupportedInputFormat) override;
    STDMETHOD(IsOutputFormatSupported)(IAudioMediaType* pInputFormat, IAudioMediaType* pRequestedOutputFormat,
                                       IAudioMediaType** ppSupportedOutputFormat) override;
    STDMETHOD(Reset)() override;
    STDMETHOD(GetLatency)(HNSTIME* pTime) override;

    // ---- IAudioProcessingObjectConfiguration ----
    STDMETHOD(LockForProcess)(UINT32 u32NumInputConnections, APO_CONNECTION_DESCRIPTOR** ppInputConnections,
                              UINT32 u32NumOutputConnections, APO_CONNECTION_DESCRIPTOR** ppOutputConnections) override;
    STDMETHOD(UnlockForProcess)() override;

    // ---- IAudioProcessingObjectRT ----
    STDMETHOD_(void, APOProcess)(UINT32 u32NumInputConnections, APO_CONNECTION_PROPERTY** ppInputConnections,
                                 UINT32 u32NumOutputConnections, APO_CONNECTION_PROPERTY** ppOutputConnections) override;

    // ---- IAudioSystemEffects2 ----
    HRESULT STDMETHODCALLTYPE GetEffectsList(LPGUID* ppEffectsIds, UINT* pcEffects, HANDLE Event) override;

protected:
    SpApoBase(const APO_REG_PROPERTIES* props, IUnknown* outer);
    ~SpApoBase() override;

    // ---- what a concrete effect supplies ----
    virtual const char* Name() const = 0;   // for the log
    virtual uint32_t SlotKind() const = 0;  // SpSlotKind
    // Control thread, inside LockForProcess; not called in safe mode (safe_). May allocate. May throw: the lock then
    // fails with E_OUTOFMEMORY and everything is rolled back.
    virtual void OnPrepare(uint32_t rate, uint32_t channels, uint32_t channelMask) = 0;
    // Audio path (and once at lock, before processing starts). The settings changed. Must set limit_.
    virtual void OnSettings(const Settings& s) = 0;
    // Audio path. Clear the DSP state.
    virtual void OnReset() = 0;
    // Audio path. buf holds frames * channels_ finite samples (or zeros). Run the DSP on them in place. Nothing else.
    virtual void OnProcess(float* buf, uint32_t frames) = 0;
    // Audio path, after the final clamp. Update the slot's atomics (slot_ is not null here).
    virtual void OnTelemetry(uint32_t frames, uint32_t clampHits, float outPeakLin) = 0;
    // Any thread. Frames of delay at the locked rate (0 when none, and in safe mode).
    virtual uint32_t OnLatencyFrames() const = 0;

    // The locked format (valid between LockForProcess and UnlockForProcess).
    uint32_t channels_ = 0;
    uint32_t rate_ = 0;
    uint32_t mask_ = 0;
    uint32_t maxFrames_ = 0;
    SlotRt*  slot_ = nullptr;   // null when all 32 slots were taken
    bool     safe_ = false;     // clamp-only mode, decided once per lock
    float    limit_ = 1.0f;     // the final clamp, linear (Leveler 1.0; Safety Cap its ceiling, 1.0 when disabled)

private:
    // Non-delegating IUnknown. It is a member, so its lifetime is the object's lifetime.
    struct Inner final : IUnknown {
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override { return owner->NonDelegatingQueryInterface(riid, ppv); }
        ULONG STDMETHODCALLTYPE AddRef() override { return owner->NonDelegatingAddRef(); }
        ULONG STDMETHODCALLTYPE Release() override { return owner->NonDelegatingRelease(); }
        SpApoBase* owner = nullptr;
    };

    HRESULT NonDelegatingQueryInterface(REFIID riid, void** ppv);
    ULONG NonDelegatingAddRef();
    ULONG NonDelegatingRelease();

    HRESULT CheckFormat(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** ppSupported, const char* what);
    // The part of LockForProcess that follows the base class's lock: registers the effect, prepares the DSP. On any
    // failure it undoes its own work (the caller undoes the base class's lock).
    HRESULT StartLocked(const UNCOMPRESSEDAUDIOFORMAT& fmt, uint32_t maxFrames);
    void SyncSettings();  // audio path

    Inner inner_;
    IUnknown* outer_ = nullptr;  // never null after construction: the caller's outer unknown, or &inner_
    LONG refs_ = 1;              // references to the inner unknown (the creation reference included)

    uint32_t mode_ = 0;  // SpMode, from Initialize
    bool discoveryOnly_ = false;
    bool registered_ = false;             // Runtime::EffectLocked was called and not yet balanced (m_CritSec)
    std::atomic<bool> ready_{false};      // the DSP is prepared: APOProcess may work
    std::atomic<bool> resetPending_{false};
    uint32_t appliedSeq_ = 1;             // settings publication the DSP follows now; odd = none yet (audio path only)
};

// Creates a T (a SpApoBase), returns the interface asked for and drops the creation reference.
template <class T>
HRESULT CreateApo(IUnknown* outer, REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    T* obj = new (std::nothrow) T(outer);
    if (!obj) return E_OUTOFMEMORY;
    IUnknown* inner = obj->InnerUnknown();
    const HRESULT hr = inner->QueryInterface(riid, ppv);
    inner->Release();
    return hr;
}

// Defined in SpLevelerApo.cpp and SpSafetyCapApo.cpp; used by the class factory.
HRESULT CreateLevelerApo(IUnknown* outer, REFIID riid, void** ppv);
HRESULT CreateSafetyCapApo(IUnknown* outer, REFIID riid, void** ppv);

}  // namespace apo
}  // namespace sp
