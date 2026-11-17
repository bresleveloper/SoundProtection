// SpSafetyCapApo.cpp - the end-of-line Safety Cap: a look-ahead true-peak limiter that nothing can get past.
//
// The DSP is sp::SafetyCap (native\dsp\SpDsp.h), with the V2 sp::BandGuard in front of it. This file only connects
// them to the plumbing in SpApoBase:
//   - the ceiling is min(cap + margin, -1 dBFS), from the runtime's settings (config.bin),
//   - disabled means the ceiling ramps up to 0 dBFS inside the DSP; the effect stays in line,
//   - the bass and treble caps (bands.bin, through the same settings) go to the band guard, which turns a band down by
//     what it is over its cap; it runs ahead of the limiter, so the limiter still has the last word on the peaks,
//     and with protection switched off both cuts are released (the guard's own enabled switch),
//   - the guard adds no delay, so the latency is the limiter's alone (look-ahead plus the true-peak filter) and is
//     reported to the engine through GetLatency; the cuts it makes go out in the telemetry slot,
//   - in safe mode (crash-loop guard tripped) no DSP runs and no delay is added: a hard clamp at the ceiling, with no
//     band guard either, and the cut telemetry reads 0,
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
        band_.Prepare(rate, channels);  // cannot throw; first, so that the throw below leaves nothing half done
        cap_.Prepare(rate, channels);   // may throw std::bad_alloc: the lock then fails
    }

    void OnSettings(const Settings& s) override {
        const float ceilingDb = CeilingDb(s.capDb, s.marginDb);  // min(cap + margin, -1), NaN gives -1
        limit_ = s.Enabled() ? CeilingLinearFloor(ceilingDb) : 1.0f;
        if (safe_) return;
        BandGuardParams b;
        b.enabled = s.Enabled();
        b.bassOn = (s.bandFlags & sp::kBandBass) != 0;
        b.bassCapDb = s.bassCapDb;
        b.trebleOn = (s.bandFlags & sp::kBandTreble) != 0;
        b.trebleCapDb = s.trebleCapDb;
        band_.SetParams(b);
        SafetyCapParams p;
        p.ceilingDb = ceilingDb;
        p.enabled = s.Enabled();
        cap_.SetParams(p);
    }

    void OnReset() override {
        if (safe_) return;
        band_.Reset();
        cap_.Reset();
    }

    void OnProcess(float* buf, uint32_t frames) override {
        if (safe_) return;
        band_.Process(buf, buf, frames);  // no delay: the limiter's look-ahead below is still the only latency
        cap_.Process(buf, buf, frames);
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
        slot_->bassCutDb.store(safe_ ? 0.0f : band_.BassCutDb(), std::memory_order_relaxed);
        slot_->trebleCutDb.store(safe_ ? 0.0f : band_.TrebleCutDb(), std::memory_order_relaxed);
    }

    uint32_t OnLatencyFrames() const override { return safe_ ? 0u : cap_.LatencyFrames(); }  // the band guard adds none

private:
    BandGuard band_;  // ahead of cap_ in OnProcess
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
