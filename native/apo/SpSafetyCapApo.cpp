// SpSafetyCapApo.cpp - the end-of-line Safety Cap: a look-ahead true-peak limiter that nothing can get past.
//
// The DSP is sp::SafetyCap (native\dsp\SpDsp.h). This file only connects it to the plumbing in SpApoBase:
//   - the ceiling is min(cap + margin, -1 dBFS), from the runtime's settings (config.bin),
//   - disabled means the ceiling ramps up to 0 dBFS inside the DSP; the effect stays in line,
//   - the latency (look-ahead plus the true-peak filter) is reported to the engine through GetLatency,
//   - in safe mode (crash-loop guard tripped) no DSP runs and no delay is added: a hard clamp at the ceiling,
//   - the DLL's own final clamp (limit_) sits at the ceiling too, so even a wrong DSP cannot exceed it.
#include "SpApoBase.h"
#include "SpDspSelect.h"

namespace sp {
namespace apo {

namespace {

class SafetyCapApo final : public SpApoBase {
public:
    explicit SafetyCapApo(IUnknown* outer) : SpApoBase(SafetyCapRegProps(), outer) {}

protected:
    const char* Name() const override { return "SafetyCap"; }
    uint32_t SlotKind() const override { return sp::kSlotSafetyCap; }

    void OnPrepare(uint32_t rate, uint32_t channels, uint32_t /*channelMask*/) override {
        cap_.Prepare(rate, channels);  // may throw std::bad_alloc: the lock then fails
    }

    void OnSettings(const Settings& s) override {
        const float ceilingDb = CeilingDb(s.capDb, s.marginDb);  // min(cap + margin, -1), NaN gives -1
        limit_ = s.Enabled() ? CeilingLinearFloor(ceilingDb) : 1.0f;
        if (safe_) return;
        SafetyCapParams p;
        p.ceilingDb = ceilingDb;
        p.enabled = s.Enabled();
        cap_.SetParams(p);
    }

    void OnReset() override {
        if (!safe_) cap_.Reset();
    }

    void OnProcess(float* buf, uint32_t frames) override {
        if (!safe_) cap_.Process(buf, buf, frames);
    }

    void OnTelemetry(uint32_t frames, uint32_t clampHits, float outPeakLin) override {
        dllHits_ += clampHits;
        const float gr = safe_ ? 0.0f : cap_.GainReductionDb();
        slot_->frames.fetch_add(frames, std::memory_order_relaxed);
        AtomicMax(slot_->inputDb, safe_ ? LinearToDb(outPeakLin) : cap_.InputPeakDb());  // the worker reads and resets it each tick
        slot_->gainReductionDb.store(gr, std::memory_order_relaxed);
        AtomicMax(slot_->maxGainReductionDb, gr);
        AtomicMax(slot_->outputPeakLin, outPeakLin);
        slot_->clipperHits.store((safe_ ? 0u : cap_.ClipperHits()) + dllHits_, std::memory_order_relaxed);
    }

    uint32_t OnLatencyFrames() const override { return safe_ ? 0u : cap_.LatencyFrames(); }

private:
    SafetyCap cap_;
    uint32_t dllHits_ = 0;  // samples the DLL's own final clamp had to change (cumulative; should stay 0)
};

}  // namespace

const APO_REG_PROPERTIES* SafetyCapRegProps() {
    // Static storage: the base class keeps the pointer for the life of every object.
    static const CRegAPOProperties<1> props(sp::kClsidSafetyCap, SP_SAFETYCAP_FRIENDLY_NAME, L"Sound Protection (personal build)", 1, 0,
                                            __uuidof(IAudioProcessingObject), static_cast<APO_FLAG>(0xF), 1, 1, 1, 1, 0xFFFFFFFFu);
    return props;
}

HRESULT CreateSafetyCapApo(IUnknown* outer, REFIID riid, void** ppv) { return CreateApo<SafetyCapApo>(outer, riid, ppv); }

}  // namespace apo
}  // namespace sp
