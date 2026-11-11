// SpApoBase.cpp - see SpApoBase.h.
#include "SpApoBase.h"

#include <audioenginebaseapo.h>
#include <ks.h>
#include <ksmedia.h>

#include <cmath>

namespace sp {
namespace apo {

namespace {

constexpr LONG kDestroying = 0x40000000;  // refs_ while the object is being destroyed

constexpr float kMinRateF = static_cast<float>(kMinRate);
constexpr float kMaxRateF = static_cast<float>(kMaxRate);

// Holds m_CritSec for a scope. Control calls only; the audio path never takes it.
class CritSecScope {
public:
    explicit CritSecScope(CRITICAL_SECTION& cs) : cs_(cs) { EnterCriticalSection(&cs_); }
    ~CritSecScope() { LeaveCriticalSection(&cs_); }
    CritSecScope(const CritSecScope&) = delete;
    CritSecScope& operator=(const CritSecScope&) = delete;

private:
    CRITICAL_SECTION& cs_;
};

uint32_t PopCount(uint32_t v) {
    uint32_t n = 0;
    while (v != 0) {
        v &= v - 1;
        ++n;
    }
    return n;
}

// The one format the DSP is built for: float32 in a 4-byte container, 1 to 32 channels, 8000 to 384000 Hz.
bool IsSupportedFloat32(const UNCOMPRESSEDAUDIOFORMAT& f) {
    return IsEqualGUID(f.guidFormatType, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT) != FALSE && f.dwBytesPerSampleContainer == 4 &&
           f.dwValidBitsPerSample == 32 && f.dwSamplesPerFrame >= 1 && f.dwSamplesPerFrame <= kMaxChannels &&
           f.fFramesPerSecond >= kMinRateF && f.fFramesPerSecond <= kMaxRateF;  // false for NaN
}

// Reads and checks the two connections of a lock: both float32, same channel count and rate, a usable frame count.
HRESULT ReadConnections(APO_CONNECTION_DESCRIPTOR** pIn, APO_CONNECTION_DESCRIPTOR** pOut, UNCOMPRESSEDAUDIOFORMAT& fmt,
                        uint32_t& maxFrames) {
    if (!pIn || !pOut || !pIn[0] || !pOut[0]) return E_POINTER;
    const APO_CONNECTION_DESCRIPTOR& in = *pIn[0];
    const APO_CONNECTION_DESCRIPTOR& out = *pOut[0];
    if (!in.pFormat || !out.pFormat) return E_POINTER;
    UNCOMPRESSEDAUDIOFORMAT fi = {};
    UNCOMPRESSEDAUDIOFORMAT fo = {};
    HRESULT hr = in.pFormat->GetUncompressedAudioFormat(&fi);
    if (FAILED(hr)) return hr;
    hr = out.pFormat->GetUncompressedAudioFormat(&fo);
    if (FAILED(hr)) return hr;
    if (!IsSupportedFloat32(fi) || !IsSupportedFloat32(fo)) return APOERR_INVALID_CONNECTION_FORMAT;
    if (fi.dwSamplesPerFrame != fo.dwSamplesPerFrame || fi.fFramesPerSecond != fo.fFramesPerSecond) return APOERR_INVALID_CONNECTION_FORMAT;
    const uint32_t frames = in.u32MaxFrameCount < out.u32MaxFrameCount ? in.u32MaxFrameCount : out.u32MaxFrameCount;
    if (frames == 0) return APOERR_INVALID_OUTPUT_MAXFRAMECOUNT;
    fmt = fi;
    maxFrames = frames;
    return S_OK;
}

}  // namespace

float CeilingLinearFloor(float db) {
    const double exact = std::pow(10.0, static_cast<double>(db) / 20.0);
    float f = static_cast<float>(exact);
    if (static_cast<double>(f) > exact) f = std::nextafter(f, 0.0f);
    return f;
}

uint32_t ModeFromGuid(const GUID& mode) {
    if (IsEqualGUID(mode, GUID_NULL)) return kModeUnknown;
    if (IsEqualGUID(mode, AUDIO_SIGNALPROCESSINGMODE_DEFAULT)) return kModeDefault;
    if (IsEqualGUID(mode, AUDIO_SIGNALPROCESSINGMODE_RAW)) return kModeRaw;
    if (IsEqualGUID(mode, AUDIO_SIGNALPROCESSINGMODE_COMMUNICATIONS)) return kModeCommunications;
    if (IsEqualGUID(mode, AUDIO_SIGNALPROCESSINGMODE_SPEECH)) return kModeSpeech;
    if (IsEqualGUID(mode, AUDIO_SIGNALPROCESSINGMODE_MEDIA)) return kModeMedia;
    if (IsEqualGUID(mode, AUDIO_SIGNALPROCESSINGMODE_MOVIE)) return kModeMovie;
    if (IsEqualGUID(mode, AUDIO_SIGNALPROCESSINGMODE_NOTIFICATION)) return kModeNotification;
    return kModeOther;
}

// =====================================================================================================
// Construction, COM
// =====================================================================================================

SpApoBase::SpApoBase(const APO_REG_PROPERTIES* props, IUnknown* outer) : CBaseAudioProcessingObject(props) {
    inner_.owner = this;
    outer_ = outer ? outer : static_cast<IUnknown*>(&inner_);  // not aggregated: the object is its own outer unknown
    GetRuntime().ApoCreated();
}

SpApoBase::~SpApoBase() {
    ready_.store(false, std::memory_order_release);
    if (registered_) {  // destroyed while still locked: give the slot and the guard count back
        GetRuntime().EffectUnlocked(slot_);
        registered_ = false;
        slot_ = nullptr;
    }
}

HRESULT SpApoBase::NonDelegatingQueryInterface(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (IsEqualIID(riid, __uuidof(IUnknown))) {
        // The identity of the object. Counted on the inner unknown, not on the outer one.
        *ppv = static_cast<IUnknown*>(&inner_);
        NonDelegatingAddRef();
        return S_OK;
    }
    void* itf = nullptr;
    if (IsEqualIID(riid, __uuidof(IAudioProcessingObject))) {
        itf = static_cast<IAudioProcessingObject*>(this);
    } else if (IsEqualIID(riid, __uuidof(IAudioProcessingObjectRT))) {
        itf = static_cast<IAudioProcessingObjectRT*>(this);
    } else if (IsEqualIID(riid, __uuidof(IAudioProcessingObjectConfiguration))) {
        itf = static_cast<IAudioProcessingObjectConfiguration*>(this);
    } else if (IsEqualIID(riid, __uuidof(IAudioSystemEffects))) {
        itf = static_cast<IAudioSystemEffects*>(static_cast<IAudioSystemEffects2*>(this));
    } else if (IsEqualIID(riid, __uuidof(IAudioSystemEffects2))) {
        itf = static_cast<IAudioSystemEffects2*>(this);
    } else {
        return E_NOINTERFACE;
    }
    outer_->AddRef();  // every other interface counts on the outer unknown
    *ppv = itf;
    return S_OK;
}

ULONG SpApoBase::NonDelegatingAddRef() { return static_cast<ULONG>(InterlockedIncrement(&refs_)); }

ULONG SpApoBase::NonDelegatingRelease() {
    const LONG r = InterlockedDecrement(&refs_);
    if (r != 0) return static_cast<ULONG>(r);
    refs_ = kDestroying;  // a stray AddRef/Release during destruction must not destroy the object a second time
    delete this;
    GetRuntime().ApoDestroyed();  // after this call nothing here touches the DLL's data
    return 0;
}

HRESULT SpApoBase::QueryInterface(REFIID riid, void** ppvObject) { return outer_->QueryInterface(riid, ppvObject); }
ULONG SpApoBase::AddRef() { return outer_->AddRef(); }
ULONG SpApoBase::Release() { return outer_->Release(); }

HRESULT SpApoBase::GetEffectsList(LPGUID* ppEffectsIds, UINT* pcEffects, HANDLE /*Event*/) {
    if (!ppEffectsIds || !pcEffects) return E_POINTER;
    *ppEffectsIds = nullptr;  // the list never changes and is empty: this is a dynamics processor, not a named effect
    *pcEffects = 0;
    return S_OK;
}

// =====================================================================================================
// Initialize and formats
// =====================================================================================================

HRESULT SpApoBase::Initialize(UINT32 cbDataSize, BYTE* pbyData) {
    if (!pbyData) return E_POINTER;
    if (cbDataSize < sizeof(APOInitBaseStruct)) return E_INVALIDARG;
    CritSecScope cs(m_CritSec);
    if (m_bIsLocked) return APOERR_APO_LOCKED;

    uint32_t mode = kModeUnknown;
    bool discovery = false;
    static_assert(sizeof(APOInitSystemEffects2) != sizeof(APOInitSystemEffects3), "the two layouts are told apart by their size");
    if (cbDataSize == sizeof(APOInitSystemEffects2)) {
        APOInitSystemEffects2 d;  // copied: the caller's buffer need not be aligned for the pointers inside
        std::memcpy(&d, pbyData, sizeof(d));
        mode = ModeFromGuid(d.AudioProcessingMode);
        discovery = (d.InitializeForDiscoveryOnly != FALSE);
    } else if (cbDataSize == sizeof(APOInitSystemEffects3)) {
        APOInitSystemEffects3 d;
        std::memcpy(&d, pbyData, sizeof(d));
        mode = ModeFromGuid(d.AudioProcessingMode);
        discovery = (d.InitializeForDiscoveryOnly != FALSE);
    }
    mode_ = mode;
    discoveryOnly_ = discovery;
    m_bIsInitialized = true;
    return S_OK;
}

HRESULT SpApoBase::CheckFormat(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** ppSupported, const char* what) {
    if (!ppSupported) return E_POINTER;
    *ppSupported = nullptr;
    if (!requested) return E_POINTER;

    UNCOMPRESSEDAUDIOFORMAT req = {};
    HRESULT hr = requested->GetUncompressedAudioFormat(&req);
    if (FAILED(hr)) return hr;
    UNCOMPRESSEDAUDIOFORMAT opp = {};
    const bool haveOpp = (opposite != nullptr) && SUCCEEDED(opposite->GetUncompressedAudioFormat(&opp));

    // Fine when it is float32 in range, and (when the other side is known) has its channel count and rate.
    const bool ok = IsSupportedFloat32(req) &&
                    (!haveOpp || (req.dwSamplesPerFrame == opp.dwSamplesPerFrame && req.fFramesPerSecond == opp.fFramesPerSecond));
    if (ok) {
        requested->AddRef();
        *ppSupported = requested;
        return S_OK;
    }

    // Otherwise suggest float32 with the other side's channels and rate (or the requested ones), pulled into range.
    const UNCOMPRESSEDAUDIOFORMAT& src = haveOpp ? opp : req;
    uint32_t ch = src.dwSamplesPerFrame;
    if (ch < 1) ch = 1;
    if (ch > kMaxChannels) ch = kMaxChannels;
    float rate = src.fFramesPerSecond;
    if (!(rate >= kMinRateF)) rate = kMinRateF;  // also catches NaN
    if (rate > kMaxRateF) rate = kMaxRateF;

    UNCOMPRESSEDAUDIOFORMAT sug = {};
    sug.guidFormatType = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
    sug.dwSamplesPerFrame = ch;
    sug.dwBytesPerSampleContainer = 4;
    sug.dwValidBitsPerSample = 32;
    sug.fFramesPerSecond = rate;
    sug.dwChannelMask = (PopCount(src.dwChannelMask) == ch) ? src.dwChannelMask : 0;

    IAudioMediaType* suggestion = nullptr;
    hr = CreateAudioMediaTypeFromUncompressedAudioFormat(&sug, &suggestion);
    if (FAILED(hr)) return hr;
    if (!suggestion) return E_FAIL;

    Runtime& rt = GetRuntime();
    if (rt.TakeFormatLogBudget()) {
        rt.Log("%s: %s refused %u ch, %.0f Hz, %u valid bits in %u bytes (subtype %08lX); suggested float32, %u ch, %.0f Hz", Name(), what,
               req.dwSamplesPerFrame, static_cast<double>(req.fFramesPerSecond), req.dwValidBitsPerSample, req.dwBytesPerSampleContainer,
               req.guidFormatType.Data1, ch, static_cast<double>(rate));
    }
    *ppSupported = suggestion;
    return S_FALSE;
}

HRESULT SpApoBase::IsInputFormatSupported(IAudioMediaType* pOutputFormat, IAudioMediaType* pRequestedInputFormat,
                                          IAudioMediaType** ppSupportedInputFormat) {
    return CheckFormat(pOutputFormat, pRequestedInputFormat, ppSupportedInputFormat, "IsInputFormatSupported");
}

HRESULT SpApoBase::IsOutputFormatSupported(IAudioMediaType* pInputFormat, IAudioMediaType* pRequestedOutputFormat,
                                           IAudioMediaType** ppSupportedOutputFormat) {
    return CheckFormat(pInputFormat, pRequestedOutputFormat, ppSupportedOutputFormat, "IsOutputFormatSupported");
}

// =====================================================================================================
// Lock and unlock
// =====================================================================================================

HRESULT SpApoBase::LockForProcess(UINT32 u32NumInputConnections, APO_CONNECTION_DESCRIPTOR** ppInputConnections,
                                  UINT32 u32NumOutputConnections, APO_CONNECTION_DESCRIPTOR** ppOutputConnections) {
    CritSecScope cs(m_CritSec);
    if (!m_bIsInitialized) return APOERR_NOT_INITIALIZED;
    if (m_bIsLocked) return APOERR_APO_LOCKED;

    // The base class checks the shape of the connections (counts, signatures, buffer types) and remembers the format.
    HRESULT hr = CBaseAudioProcessingObject::LockForProcess(u32NumInputConnections, ppInputConnections, u32NumOutputConnections,
                                                            ppOutputConnections);
    if (FAILED(hr)) return hr;

    // From here on the base class holds the lock: every failure must undo it.
    UNCOMPRESSEDAUDIOFORMAT fmt = {};
    uint32_t maxFrames = 0;
    hr = ReadConnections(ppInputConnections, ppOutputConnections, fmt, maxFrames);
    if (SUCCEEDED(hr)) hr = StartLocked(fmt, maxFrames);
    if (FAILED(hr)) CBaseAudioProcessingObject::UnlockForProcess();
    return hr;
}

HRESULT SpApoBase::StartLocked(const UNCOMPRESSEDAUDIOFORMAT& fmt, uint32_t maxFrames) {
    Runtime& rt = GetRuntime();
    const uint32_t rate = static_cast<uint32_t>(fmt.fFramesPerSecond + 0.5f);
    const uint32_t channels = fmt.dwSamplesPerFrame;
    const uint32_t mask = fmt.dwChannelMask;

    // Registers the effect: starts the worker on the first one, reads config.bin, counts the start in the guard.
    // This is also where safe mode can be decided, so the settings are read only afterwards.
    slot_ = rt.EffectLocked(SlotKind(), rate, channels, mode_);
    registered_ = true;

    Settings s;
    bool haveSettings = false;
    for (int attempt = 0; attempt < 200 && !haveSettings; ++attempt) {
        haveSettings = rt.ReadSettings(s);
        if (!haveSettings) Sleep(0);  // a publication is in progress: it takes microseconds
    }
    if (!haveSettings) {  // cannot happen in practice; the bounded fallback is the clamp-only mode
        s = Settings();
        s.flags |= kSettingsSafe;
    }

    channels_ = channels;
    rate_ = rate;
    mask_ = mask;
    maxFrames_ = maxFrames;
    safe_ = s.Safe();
    try {
        if (!safe_) OnPrepare(rate, channels, mask);
        OnSettings(s);
    } catch (...) {
        rt.EffectUnlocked(slot_);
        registered_ = false;
        slot_ = nullptr;
        channels_ = 0;
        rate_ = 0;
        mask_ = 0;
        maxFrames_ = 0;
        rt.Log("%s: lock failed: the DSP could not be prepared (out of memory)", Name());
        return E_OUTOFMEMORY;
    }
    appliedSeq_ = s.seq;
    resetPending_.store(false, std::memory_order_relaxed);
    ready_.store(true, std::memory_order_release);  // publishes everything above to the audio thread

    rt.Log("%s locked: %u Hz, %u ch, mask 0x%X, mode %u, up to %u frames%s%s%s", Name(), rate, channels, mask, mode_, maxFrames,
           safe_ ? ", SAFE MODE (clamp only)" : "", s.Enabled() ? "" : ", disabled", slot_ ? "" : ", no telemetry slot");
    return S_OK;
}

HRESULT SpApoBase::UnlockForProcess() {
    CritSecScope cs(m_CritSec);
    ready_.store(false, std::memory_order_release);  // first: a late APOProcess gets silence, not a half-torn-down DSP
    if (registered_) {
        Runtime& rt = GetRuntime();
        rt.EffectUnlocked(slot_);
        registered_ = false;
        slot_ = nullptr;
        rt.Log("%s unlocked", Name());
    }
    channels_ = 0;
    rate_ = 0;
    mask_ = 0;
    maxFrames_ = 0;
    return CBaseAudioProcessingObject::UnlockForProcess();
}

HRESULT SpApoBase::Reset() {
    // The DSP belongs to the audio thread: it clears its state at the start of its next period.
    resetPending_.store(true, std::memory_order_release);
    return S_OK;
}

HRESULT SpApoBase::GetLatency(HNSTIME* pTime) {
    if (!pTime) return E_POINTER;
    *pTime = 0;
    CritSecScope cs(m_CritSec);
    if (!m_bIsLocked || rate_ == 0 || !ready_.load(std::memory_order_acquire)) return S_OK;
    const uint32_t frames = OnLatencyFrames();
    *pTime = static_cast<HNSTIME>(static_cast<double>(frames) * 10000000.0 / static_cast<double>(rate_) + 0.5);
    return S_OK;
}

// =====================================================================================================
// The audio path
// =====================================================================================================

void SpApoBase::SyncSettings() {
    Runtime& rt = GetRuntime();
    const uint32_t seq = rt.SettingsSeq();
    if (seq == appliedSeq_ || (seq & 1u) != 0) return;
    Settings s;
    if (!rt.ReadSettings(s)) return;  // a publication was in progress on every try: keep the old values
    appliedSeq_ = s.seq;
    OnSettings(s);
}

void SpApoBase::APOProcess(UINT32 u32NumInputConnections, APO_CONNECTION_PROPERTY** ppInputConnections, UINT32 u32NumOutputConnections,
                           APO_CONNECTION_PROPERTY** ppOutputConnections) {
    if (u32NumOutputConnections < 1 || !ppOutputConnections || !ppOutputConnections[0]) return;  // nowhere to write
    APO_CONNECTION_PROPERTY* out = ppOutputConnections[0];
    const APO_CONNECTION_PROPERTY* in = (u32NumInputConnections >= 1 && ppInputConnections) ? ppInputConnections[0] : nullptr;

    if (!ready_.load(std::memory_order_acquire) || !in || in->pBuffer == 0 || out->pBuffer == 0) {
        // Not locked, or not usable: hand back silence by flag. The buffer is not touched.
        out->u32ValidFrameCount = in ? in->u32ValidFrameCount : 0;
        out->u32BufferFlags = BUFFER_SILENT;
        return;
    }

    const FtzDazScope ftz;

    uint32_t frames = in->u32ValidFrameCount;
    if (frames > maxFrames_) frames = maxFrames_;
    const size_t n = static_cast<size_t>(frames) * channels_;
    const float* src = reinterpret_cast<const float*>(in->pBuffer);
    float* dst = reinterpret_cast<float*>(out->pBuffer);

    // A buffer flagged silent or invalid has undefined contents: its input is zeros, and the DSP processes those zeros
    // like any other input. What comes out is whatever the DSP still holds of the sound before (the Safety Cap's
    // look-ahead tail), then zeros: nothing here cuts the tail off. Anything else: finite, within +-1000.
    const bool silent = (in->u32BufferFlags != BUFFER_VALID);
    if (silent) {
        std::memset(dst, 0, n * sizeof(float));
    } else {
        SanitiseCopy(src, dst, n);
    }

    if (resetPending_.load(std::memory_order_relaxed) && resetPending_.exchange(false, std::memory_order_acq_rel)) OnReset();
    SyncSettings();

    OnProcess(dst, frames);

    // The last word. Whatever the DSP did, nothing leaves this function that is not finite or is beyond limit_.
    uint32_t hits = 0;
    const float peak = ClampBlock(dst, n, limit_, &hits);
    if (slot_) OnTelemetry(frames, hits, peak);

    // Always BUFFER_VALID (step 7 of the brief): it covers the tail of a silent-flagged period, which has non-zero
    // samples, and a silent period without a tail is valid zeros.
    out->u32ValidFrameCount = frames;
    out->u32BufferFlags = BUFFER_VALID;
}

}  // namespace apo
}  // namespace sp
