// SpLevelerApo.cpp - the per-stream Leveler: turns a loud stream down to the loudness cap, before the volume slider.
//
// The DSP is sp::Leveler (native\dsp\SpDsp.h). This file only connects it to the plumbing in SpApoBase:
//   - the cap and the on/off switch come from the runtime's settings (config.bin),
//   - disabled means the gain ramps back to unity inside the DSP; the effect stays in line,
//   - in safe mode (crash-loop guard tripped) no DSP runs: the base class's final clamp at +-1.0 is all that is left,
//   - the output is finite and within +-1.0 whatever the DSP does (limit_ = 1.0).
#include "SpApoBase.h"
#include "SpDspSelect.h"

namespace sp {
namespace apo {

namespace {

class LevelerApo final : public SpApoBase {
public:
    explicit LevelerApo(IUnknown* outer) : SpApoBase(LevelerRegProps(), outer) { limit_ = 1.0f; }

protected:
    const char* Name() const override { return "Leveler"; }
    uint32_t SlotKind() const override { return sp::kSlotLeveler; }

    void OnPrepare(uint32_t rate, uint32_t channels, uint32_t channelMask) override {
        float weights[kMaxChannels];  // one per channel; the LFE channel counts 0, the surrounds 1.41
        ChannelWeightsFromMask(channelMask, channels, weights);
        leveler_.Prepare(rate, channels, weights);  // may throw std::bad_alloc: the lock then fails
    }

    void OnSettings(const Settings& s) override {
        limit_ = 1.0f;
        if (safe_) return;
        LevelerParams p;
        p.capDb = s.capDb;
        p.enabled = s.Enabled();
        leveler_.SetParams(p);
    }

    void OnReset() override {
        if (!safe_) leveler_.Reset();
    }

    void OnProcess(float* buf, uint32_t frames) override {
        if (!safe_) leveler_.Process(buf, buf, frames);
    }

    void OnTelemetry(uint32_t frames, uint32_t /*clampHits*/, float outPeakLin) override {
        const float gr = safe_ ? 0.0f : leveler_.GainReductionDb();
        slot_->frames.fetch_add(frames, std::memory_order_relaxed);
        slot_->inputDb.store(safe_ ? kTelemetryFloorDb : leveler_.InputLoudnessDb(), std::memory_order_relaxed);
        slot_->gainReductionDb.store(gr, std::memory_order_relaxed);
        AtomicMax(slot_->maxGainReductionDb, gr);
        AtomicMax(slot_->outputPeakLin, outPeakLin);
    }

    uint32_t OnLatencyFrames() const override { return 0; }  // sample-by-sample gain: no delay

private:
    Leveler leveler_;
};

}  // namespace

const APO_REG_PROPERTIES* LevelerRegProps() {
    // Static storage: the base class keeps the pointer for the life of every object.
    static const CRegAPOProperties<1> props(sp::kClsidLeveler, SP_LEVELER_FRIENDLY_NAME, L"Sound Protection (personal build)", 1, 0,
                                            __uuidof(IAudioProcessingObject), static_cast<APO_FLAG>(0xF), 1, 1, 1, 1, 0xFFFFFFFFu);
    return props;
}

HRESULT CreateLevelerApo(IUnknown* outer, REFIID riid, void** ppv) { return CreateApo<LevelerApo>(outer, riid, ppv); }

}  // namespace apo
}  // namespace sp
