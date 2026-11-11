# Sound Protection Plug-in (Way 1, A to Z) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build, test and install the in-engine plug-in from Ariel's sketch: a per-app loudness leveler and a safety cap on the total, both inside Windows' sound engine on the Headphones, before the volume slider. The tray app becomes its remote control.

**Architecture:** One C++ DLL (`SoundProtection.Apo.dll`) holds two APO classes built on the Windows SDK's `CBaseAudioProcessingObject`:
- the **Leveler**, appended to the Headphones' per-stream list `,13`;
- the **Safety Cap**, appended to the end-of-line list `,15`.

Their DSP lives in a header-only, OS-free library with its own native tests. A PowerShell installer does every system change: one elevated run, a journal and a one-step undo. The C# tray app writes the cap to a 32-byte config file, reads a memory-mapped telemetry file, checks health, and reports what was turned down.

**Tech Stack:** C++17 (MSVC 14.44, Windows SDK 10.0.26100.0, no WDK, no ATL); Windows PowerShell 5.1; C# .NET 10 WinForms with NAudio 3.3 and xUnit (existing solution).

**Spec:** `docs/02/WIN11-BUILD-PLAN.md` (plan 3, Way 1, approved by Ariel 2026-11-10: "i like the plan, do the 'my thing' with full plugin a-z now, no mid tests"). Engineering details come from `PIPELINE-LIMITER-PLAN.md` §4.2–4.6 and §7. **Shared contract:** `native/apo/SpShared.h`, which holds the CLSIDs, file names, byte layouts, golden bytes and defaults. Read it first.

## Global Constraints

- **Target PC.** Windows 11 25H2, build 26200.9711, x64. The Headphones endpoint is "Headphones" (Realtek(R) Audio). Its composite lists `,13` and `,15` exist, every mode list is Default only, and the volume is in hardware.
- **Toolchain.** `call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"`, then `cl`.
  - Flags: `/std:c++17 /EHsc /W4 /O2 /fp:precise /MT /DUNICODE /D_UNICODE`.
  - Plug-in link: `/LD ... /link /MANIFEST:NO /NODEFAULTLIB:atls.lib audiobaseprocessingobject.lib audiomediatypecrt.lib audioeng.lib ole32.lib advapi32.lib`.
  - Silence only the SDK's C4815 (`/wd4815`). Your own code builds with 0 warnings.
- **Paths contain spaces** (`C:\Users\Ariel\source\MS PlayGround\Sound Protection`). Quote every path in scripts.
- **Real-time rule.** `APOProcess` and everything it calls never allocates, frees, locks, waits, logs, touches files, or calls the OS. The one exception is `_mm_getcsr`/`_mm_setcsr`, used to save, set and restore flush-to-zero and denormals-are-zero.
  - Settings reach that path only through `std::atomic` values.
- **Ear safety.** Gains are always ≤ 1. Every output sample is finite: NaN/Inf become 0.
  - The Leveler's output is clamped to ±1.0.
  - The Safety Cap's output is clamped to ±ceiling.
  - Nothing ever makes sound louder than its input.
- **Defaults.** Cap −20.0 dB, margin 12.0 dB, enabled.
  - Ceiling = min(cap + margin, −1.0) dBFS.
  - Disabled = the Leveler ramps to unity and the Safety Cap ceiling ramps to 0 dBFS. Both stay in line, never bypassed.
- **Names and locations.**
  - Product name: "Sound Protection".
  - DLL `C:\Program Files\SoundProtection\SoundProtection.Apo.dll`.
  - Data `C:\ProgramData\SoundProtection\`.
  - Built DLL `native\out\apo\SoundProtection.Apo.dll`.
- **Scripts** must run under Windows PowerShell 5.1: no `??`, no ternary, no `-Parallel`.
- **C#.** Keep the existing solution `SoundProtection.sln`. `dotnet test SoundProtection.sln` must stay green (105 tests today), and `dotnet build` must report 0 warnings.
- **No git.** This folder is not a repository: no commits. Progress goes in `docs/superpowers/plans/2026-11-10-sound-protection-plugin.ledger.md`, written by the controller.
- **Agents never touch the live system.**
  - No writes under HKLM. No service stop or start. No `regsvr32`. No `icacls` on real folders.
  - No files outside the project folder and `%TEMP%`.
  - No sound playback.
  - Never stop or replace the running tray app (`dist\SoundProtection.exe`, PID 17848), and never publish into `dist\`.
  - Only the controller (Task 5) deploys.

## Review Focus

1. **A garbage, NaN or uninitialised buffer reaching the headphones as a full-scale burst.** Every output must be finite and within the ceiling, including when the input is flagged `BUFFER_SILENT` with junk in it. Pinned by ApoHost checks H6 and H7 (Task 2) and DSP tests 12 and 23 (Task 1).
2. **A format change between two LockForProcess calls**, such as 44.1 kHz mono, 96 kHz with 8 channels, or a channel mask with an LFE channel. It must re-prepare without leaking or exceeding the ceiling. Pinned by ApoHost check H5 and DSP tests 5 and 24.
3. **The installer run twice, run as repair, or interrupted.** The journal must keep the first original values, and uninstall must restore them byte for byte, deleting values that did not exist. Pinned by rehearsal tests R2–R5 (Task 3).
4. **The tray writing config.bin while audiodg reads it.** A torn or corrupt file must never be applied, and the tray must retry on a sharing violation. Pinned by ApoHost check H9 (Task 2) and C# test `Write_RetriesWhileLocked` (Task 4).
5. **An audiodg crash loop.** After 3 unclean process starts the plug-in runs clamp-only (safe mode) and says so in telemetry. More than 32 streams must not crash it. Pinned by ApoHost checks H10 and H11 (Task 2) and the health test `SafeMode_IsDown` (Task 4).

## Execution: waves and file ownership

- **Wave 1 (parallel):** Tasks 1, 2, 3 and 4, each by its own agent.
  - Task 2 builds against its own stub of the DSP (`native\apo\SpDspStub.h`, selected by `/DSP_DSP_STUB`) until Task 1 is done.
- **Wave 2:** Task 5, by the controller: integration build, all tests, final review, deploy and live check.

| Owner | Files (create or modify only these) |
|---|---|
| Controller | `native\apo\SpShared.h` (read-only for agents), this plan, the ledger, `docs\03\` |
| Task 1 | `native\dsp\**`, `native\build-dsp.cmd`; build output `native\out\dsp\` |
| Task 2 | `native\apo\**` except `SpShared.h`; `native\apohost\**`; `native\build-apo.cmd`; build output `native\out\apo\` |
| Task 3 | `installer\**` |
| Task 4 | `src\**`, `tests\**`; publish output `dist-staging\` |

### The DSP contract (Task 1 implements it, Task 2 consumes it)

`native\dsp\SpDsp.h` exposes exactly this public API, in namespace `sp`. The private members are Task 1's choice. Task 2's stub `native\apo\SpDspStub.h` declares the same public API, with pass-through bodies.

```cpp
#pragma once
#include <cstdint>
namespace sp {
constexpr float kFloorDb = -120.0f;
float DbToLinear(float db);                        // 10^(db/20)
float LinearToDb(float linear);                    // 20*log10; kFloorDb for <= 1e-6, NaN or Inf
float CeilingDb(float capDb, float marginDb);      // min(capDb + marginDb, -1.0f)
struct Biquad { double b0, b1, b2, a1, a2; };     // a0 = 1; y = b0x + b1x1 + b2x2 - a1y1 - a2y2
void KWeighting(double sampleRate, Biquad& shelf, Biquad& highPass);   // ITU-R BS.1770-5, any rate
void ChannelWeightsFromMask(uint32_t channelMask, uint32_t channels, float* weightsOut);
class LoudnessMeter {
public:
    void Prepare(uint32_t sampleRate, uint32_t channels, const float* channelWeights); // may allocate; weights null = all 1.0
    void Reset();
    void Add(const float* interleaved, uint32_t frames);   // real-time safe; non-finite counts as 0
    float MomentaryLufs() const;                            // kFloorDb until the first 10 ms block, or when silent
};
struct LevelerParams { float capDb = -20.0f; bool enabled = true; float attackMs = 100.0f;
                       float releaseMs = 1000.0f; float maxReductionDb = 40.0f; };
class Leveler {
public:
    void Prepare(uint32_t sampleRate, uint32_t channels, const float* channelWeights);
    void Reset();
    void SetParams(const LevelerParams& p);                         // real-time safe
    void Process(const float* in, float* out, uint32_t frames);    // real-time safe; in == out allowed
    float InputLoudnessDb() const;
    float GainReductionDb() const;                                  // >= 0
};
struct SafetyCapParams { float ceilingDb = -8.0f; bool enabled = true; float lookaheadMs = 5.0f;
                         float holdMs = 20.0f; float releaseFastMs = 80.0f; float releaseSlowMs = 400.0f;
                         float slowAfterMs = 200.0f; float relaxRampMs = 100.0f; };
class SafetyCap {
public:
    void Prepare(uint32_t sampleRate, uint32_t channels);
    void Reset();
    void SetParams(const SafetyCapParams& p);                       // real-time safe
    void Process(const float* in, float* out, uint32_t frames);    // real-time safe; in == out allowed
    uint32_t LatencyFrames() const;   // lookahead + true-peak filter delay
    float InputPeakDb() const;        // largest input true-peak estimate in the last Process call
    float OutputPeakDb() const;       // largest |output| in the last Process call
    float GainReductionDb() const;    // current, >= 0
    uint32_t ClipperHits() const;     // cumulative samples changed by the final clamp
};
}
```

---

### Task 1: DSP library and its native tests

**Files:**
- Create: `native\dsp\SpDsp.h` (the API above; implementation inline or in further headers under `native\dsp\`)
- Create: `native\dsp\tests\DspTests.cpp` (self-contained runner: `CHECK(cond, msg)` macros, prints `PASS name` / `FAIL name: msg`, exits non-zero on any failure)
- Create: `native\build-dsp.cmd`. It calls vcvars64, compiles the tests to `native\out\dsp\DspTests.exe` with `/I "%~dp0dsp"` and `/WX`, then runs it. Its exit code is the tests' exit code.

**Interfaces:**
- Consumes: nothing.
- Produces: the DSP contract above.

**Algorithms. The tests pin the behaviour; these steps pin what the tests can't see:**

- **K-weighting.** Use the libebur128 formulas for any rate fs.
  - Stage 1 shelf: f0 = 1681.974450955533, G = 3.999843853973347 dB, Q = 0.7071752369554196.
    - K = tan(π·f0/fs), Vh = 10^(G/20), Vb = Vh^0.4996667741545416, a0 = 1 + K/Q + K².
    - b0 = (Vh + Vb·K/Q + K²)/a0, b1 = 2(K² − Vh)/a0, b2 = (Vh − Vb·K/Q + K²)/a0, a1 = 2(K² − 1)/a0, a2 = (1 − K/Q + K²)/a0.
  - Stage 2 high-pass: f0 = 38.13547087602444, Q = 0.5003270373238773, K = tan(π·f0/fs).
    - a1 = 2(K² − 1)/(1 + K/Q + K²), a2 = (1 − K/Q + K²)/(1 + K/Q + K²), b = (1, −2, 1).
  - Filter state in double.
- **Momentary loudness.**
  - Sum the K-weighted squares per channel over 10 ms blocks (round(fs/100) frames), and keep a ring of 40 blocks (400 ms).
  - L = −0.691 + 10·log10(Σ_c w_c · mean_c), where mean_c is the channel's sum of squares divided by the frames in the filled blocks.
  - Before the ring is full, divide by 400 ms worth of frames. So the reading ramps up as the window fills, as a true 400 ms window would.
  - Return kFloorDb when the sum is 0.
- **ChannelWeightsFromMask.** Walk the set bits of the mask in ascending order, one per channel:
  - 0x8 (LFE) → 0.0;
  - 0x10, 0x20, 0x200, 0x400 (back and side) → 1.41;
  - any other bit → 1.0;
  - channels beyond the set bits → 1.0;
  - mask 0 → 1.0 for every channel.
- **Leveler, per frame:**
  1. Feed the input to the meter, which updates L after each completed block.
  2. target = enabled ? clamp(L − cap, 0, maxReductionDb) : 0.
  3. Smooth the gain reduction GR:
     - Enabled, target > GR: GR += (target − GR)(1 − e^(−1/(attack·fs))).
     - Enabled, otherwise: the same with release.
     - Disabled: GR = max(0, GR − maxReductionDb/(0.1·fs)), a linear fall of 40 dB per 100 ms.
     - Recompute the coefficients only when the parameters change.
  4. g = 10^(−GR/20).
  5. out = finite(in) ? clamp(in·g, −1, 1) : 0.
- **Safety Cap, per frame, the same gain on all channels:**
  1. Treat non-finite input as 0, and push the frame into a delay line of LatencyFrames.
  2. True-peak estimate of frame k = n − G:
     - The largest of |x[k]| and the 4× interpolated values at k + 1/4, k + 2/4 and k + 3/4, over all channels.
     - Interpolation uses 3 phases × 16 taps of Kaiser-windowed sinc (β = 8), each phase normalised to a sum of 1. Group delay G = 8.
  3. Required gain r = (p > C) ? C/p : 1, with C = DbToLinear(current ceiling).
  4. held = minimum of r over the last D + H + 1 frames, with D = round(lookaheadMs·fs/1000) and H = round(holdMs·fs/1000). Use a monotonic deque in a preallocated ring.
  5. Envelope:
     - held < env: env = held, at once.
     - Otherwise env += (held − env)(1 − e^(−1/(τ·fs))). τ = releaseFastMs, or releaseSlowMs once env has stayed below 0.999 for more than slowAfterMs.
  6. g = moving average of env over the last D + 1 frames, kept as a running sum in a ring.
  7. y = (frame delayed by D + G) · g.
  8. Final clamp: |y| ≤ C. Count every sample this changes in ClipperHits.
  9. LatencyFrames = D + G.
- **Ceiling changes:**
  - A lower ceiling applies at once; the final clamp enforces it from the next sample.
  - A higher ceiling, or disabled (target 0 dBFS), ramps linearly in dB over relaxRampMs.

- [ ] **Step 1: Write the failing tests in `DspTests.cpp`.** Every test is named and asserts exactly this. Unless stated, signals are at 48 kHz, stereo, with identical channels.
  1. `kweighting_48k_matches_bs1770`:
     - shelf b0 1.53512485958697, b1 −2.69169618940638, b2 1.19839281085285, a1 −1.69065929318241, a2 0.73248077421585;
     - high-pass b 1, −2, 1, a1 −1.99004745483398, a2 0.99007225036621;
     - each within 1e-5.
  2. `loudness_1k_sine_stereo_minus20`: after 1 s of a 1 kHz sine with amplitude 0.1, MomentaryLufs = −20.0 ± 0.2.
  3. `loudness_mono_minus20_reads_minus23`: the same sine, 1 channel: −23.0 ± 0.2.
  4. `loudness_silence_is_floor`: 1 s of zeros gives ≤ −100.
  5. `loudness_other_rates_agree`: test 2 at 44100 and at 96000: −20.0 ± 0.3.
  6. `loudness_window_is_400ms`: zeros then the test-2 sine. At 200 ms into the sine: −23.0 ± 0.5. At 450 ms: −20.0 ± 0.3.
  7. `leveler_quiet_input_untouched`: cap −20, sine amplitude 0.0316 (≈ −30 LUFS) for 2 s: every output sample equals its input within 1e-7.
  8. `leveler_loud_input_settles_at_cap`: cap −20, sine amplitude 0.562 (≈ −5 LUFS) for 3 s.
     - A separate meter on the output reads −20.0 ± 1.0 at 3 s.
     - GainReductionDb = 15 ± 1.
  9. `leveler_never_boosts`: 5 s of noise whose level changes every 250 ms between −60 and 0 dBFS (fixed-seed LCG): |out| ≤ |in| + 1e-7 for every sample.
  10. `leveler_release_returns_to_unity`: 2 s at amplitude 0.562, then 3 s at 0.01: GainReductionDb < 0.5 at the end.
  11. `leveler_disabled_ramps_to_unity`: settled as in test 8, then set enabled = false. After 150 ms, GainReductionDb == 0 and out == in within 1e-7.
  12. `leveler_nonfinite_input_outputs_zero`:
      - NaN, +Inf and −Inf at frames 1000, 1001 and 1002 of a sine: those outputs are 0;
      - every output is finite;
      - frames after 2000 are again equal to the input (quiet sine, amplitude 0.01) within 1e-7.
  13. `leveler_in_place_matches_out_of_place`: identical outputs within 1e-7.
  14. `leveler_chunk_size_independent`: chunk sizes 1, 7, 480 and 4096 give identical outputs within 1e-6.
  15. `safetycap_burst_never_exceeds_ceiling`: ceiling −20; a 1 kHz burst at amplitude 0.891 for 50 ms after 1 s of silence. Every |output| ≤ DbToLinear(−20) + 1e-6, from the burst's first sample on.
  16. `safetycap_below_ceiling_is_delayed_copy`: ceiling −20, sine amplitude 0.0316: out[n] == in[n − LatencyFrames()] within 1e-6 for n ≥ latency.
  17. `safetycap_true_peak_12k`: ceiling −6; a 12 kHz sine with amplitude 0.891 and phase π/4 (sample peak −4.0 dBFS, true peak −1.0 dBFS).
      - The output's true peak, measured in the test by 16× Kaiser-sinc oversampling with 64 taps per phase, is ≤ −6 + 0.7 dB once the limiter has settled (after 50 ms).
      - InputPeakDb reads −1.0 ± 0.3.
  18. `safetycap_step_holds_ceiling`: ceiling −10; 1 s of amplitude 0.0316, then 1 s of 0.708: every |output| ≤ DbToLinear(−10) + 1e-6.
  19. `safetycap_release`: after test 15's burst, a sine at amplitude 0.0316. Within 600 ms of the burst ending, GainReductionDb < 0.1 and the output is a delayed copy again (as test 16).
  20. `safetycap_disabled_ramps_to_0dbfs`: ceiling −20, enabled = false, sine amplitude 0.5. After 150 ms the output peak is ≥ 0.49.
  21. `safetycap_tightening_applies_at_once`: sine amplitude 0.25 with ceiling −10, then SetParams with ceiling −20 between two Process calls. Every output sample after the call has |y| ≤ DbToLinear(−20) + 1e-6.
  22. `safetycap_never_louder_than_input`: test 9's noise: |out[n]| ≤ |in[n − LatencyFrames()]| + 1e-6.
  23. `safetycap_nonfinite_safe`: as test 12. Every output is finite, and the outputs at the delayed positions of the bad samples are 0.
  24. `safetycap_formats`:
      - channels 1, 2, 6 and 8 at 44100, 48000 and 96000, with loud noise at amplitude 0.9: every |output| ≤ ceiling + 1e-6;
      - in 2-channel frames where both inputs are ≥ 0.01, out/in is equal across channels within 1e-5 (stereo link).
  25. `safetycap_chunk_size_independent`: as test 14.
  26. `ceiling_rule`: CeilingDb(−20, 12) == −8, CeilingDb(−5, 12) == −1, CeilingDb(−60, 0) == −60.
  27. `channel_weights_from_mask`: mask 0x3F (5.1) gives {1, 1, 1, 0, 1.41, 1.41}; mask 0 with 2 channels gives {1, 1}.

- [ ] **Step 2: Run `native\build-dsp.cmd`.** Expected: a compile failure, or FAIL lines, because the code isn't written yet.
- [ ] **Step 3: Implement `native\dsp\SpDsp.h`** per the algorithms above.
- [ ] **Step 4: Run `native\build-dsp.cmd`.** Expected: 27 PASS lines, `ALL 27 PASSED`, exit code 0, no compiler warnings.
- [ ] **Step 5: Report.** List the test names with their results, and note any deviation from this task with the reason.

---

### Task 2: The plug-in DLL (two APO classes) and its test host

**Files:**
- Create in `native\apo\`:
  - `SpApoBase.h/.cpp`: shared COM and APO plumbing.
  - `SpLevelerApo.cpp`.
  - `SpSafetyCapApo.cpp`.
  - `SpRuntime.h/.cpp`: process-wide settings, worker thread, config, telemetry, guard and log.
  - `SpDll.cpp`: exports and class factory.
  - `SoundProtection.Apo.def`: `DllGetClassObject`, `DllCanUnloadNow`, `DllRegisterServer`, `DllUnregisterServer`, all PRIVATE.
  - `SpDspStub.h`: the DSP contract with pass-through bodies.
- Create: `native\apohost\ApoHost.cpp`.
- Create: `native\build-apo.cmd [stub]`. It builds the DLL to `native\out\apo\SoundProtection.Apo.dll` (with `/DSP_DSP_STUB` when the argument is `stub`), builds `native\out\apo\ApoHost.exe`, then runs `ApoHost.exe "<dll>"`. Its exit code is the host's.
- Read first:
  - `native\apo\SpShared.h`.
  - `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\baseaudioprocessingobject.h`, especially `CRegAPOProperties`.
  - `audioenginebaseapo.h`, `audioengineextensionapo.h` and `audiomediatype.h` in the same folder.
  - `PIPELINE-LIMITER-PLAN.md` §4.2 item 1 and §4.5.

**Interfaces:**
- Consumes: the DSP contract (`#include "SpDsp.h"` from `native\dsp`, or `"SpDspStub.h"` under `SP_DSP_STUB`), and `SpShared.h`.
- Produces: `SoundProtection.Apo.dll`, with registration done by `DllRegisterServer`. The installer runs it through `regsvr32`. It writes HKLM `Classes\CLSID\{clsid}` (InprocServer32 = this DLL's full path, ThreadingModel = Both) and calls `RegisterAPO` for each class. `DllUnregisterServer` reverses both and ignores "not found".

**Behaviour:**

- **COM without ATL. Aggregation must work.** Each object has a non-delegating IUnknown and an outer pointer. The outer pointer is the caller's outer unknown, or its own non-delegating unknown.
  - The public `QueryInterface`/`AddRef`/`Release` delegate to the outer pointer.
  - `NonDelegatingQueryInterface` serves `IUnknown` (the non-delegating one), `IAudioProcessingObject`, `IAudioProcessingObjectRT`, `IAudioProcessingObjectConfiguration`, `IAudioSystemEffects` and `IAudioSystemEffects2`.
  - The class factory returns `CLASS_E_NOAGGREGATION` when it gets an outer unknown and riid ≠ IID_IUnknown.
  - The DLL counts live objects and server locks, and `DllCanUnloadNow` returns S_OK only when both are 0.
- **Registration properties.** `CRegAPOProperties<1>` with:
  - the CLSIDs from `SpShared.h`;
  - friendly names `SP_LEVELER_FRIENDLY_NAME` and `SP_SAFETYCAP_FRIENDLY_NAME`;
  - copyright `L"Sound Protection (personal build)"`, version 1.0;
  - interface `__uuidof(IAudioProcessingObject)`;
  - flags `APO_FLAG_INPLACE | APO_FLAG_SAMPLESPERFRAME_MUST_MATCH | APO_FLAG_FRAMESPERSECOND_MUST_MATCH | APO_FLAG_BITSPERSAMPLE_MUST_MATCH` (0xF);
  - 1 input and 1 output;
  - max instances 0xFFFFFFFF.
- **`Initialize(cb, data)`.**
  - Requires `data` ≠ null and cb ≥ sizeof(APOInitBaseStruct). Otherwise return E_POINTER or E_INVALIDARG.
  - When cb equals sizeof(APOInitSystemEffects2), or sizeof(APOInitSystemEffects3) from `audioengineextensionapo.h`, read `AudioProcessingMode` and `InitializeForDiscoveryOnly`.
  - Map the mode to `SpMode` (Default, Raw, Communications, Speech, Media, Movie, Notification; any other value → Other; no mode → Unknown).
  - Accept null property stores.
  - Set m_bIsInitialized and return S_OK. Don't call the base Initialize.
- **Formats.** Override `IsInputFormatSupported` and `IsOutputFormatSupported` with one helper.
  - Accepted: float32 (subtype KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, container 4, valid bits 32), 1–32 channels, 8000–384000 Hz. When the opposite format is given, the same channels and rate as it.
  - Accepted → set *ppSupported to the requested type (AddRef) and return S_OK.
  - Otherwise → build float32 with the same channels and rate (`CreateAudioMediaTypeFromUncompressedAudioFormat`), return it with S_FALSE, and log it.
- **`LockForProcess`.**
  1. Call the base `LockForProcess`.
  2. Read the format: `GetUncompressedAudioFormat` gives channels, rate and dwChannelMask.
  3. Weights from `ChannelWeightsFromMask`.
  4. Prepare the DSP (allocation is allowed here).
  5. Claim a telemetry slot, which may fail when all 32 are taken: run without one.
  6. Start the runtime if this is the first locked effect in the process.
  - `UnlockForProcess` releases the slot. When the last effect in the process unlocks, it resets the guard to 0.
  - `GetLatency` reports LatencyFrames/fs in 100-ns units for the Safety Cap, 0 for the Leveler, and 0 before the lock.
- **`APOProcess`.**
  1. Save MXCSR and set FTZ|DAZ.
  2. If the input flags are BUFFER_SILENT, process zeros: zero the output buffer and use it as the source, because a silent buffer's contents are undefined.
  3. Read the runtime atomics: cap, margin, enabled, safe mode. Call SetParams only when they changed.
  4. Process: Leveler, or Safety Cap with ceiling = CeilingDb(cap, margin).
     - Safe mode, Leveler: copy with the finite check and a ±1 clamp.
     - Safe mode, Safety Cap: a hard clamp at the ceiling with the finite check, and no delay.
  5. Run a final finite check over the output: non-finite → 0.
  6. Update the slot's RT atomics: frames += n; inputDb; gainReductionDb; maxGainReductionDb by CAS-max; outputPeakDb by CAS-max; clipperHits.
  7. Set the output's ValidFrameCount = n and flags = BUFFER_VALID, then restore MXCSR.
- **Runtime (SpRuntime), one per process.**
  - **Data dir.** The env var `SP_DATA_DIR_ENV` if set and not empty; else `%ProgramData%\SoundProtection` (GetEnvironmentVariableW("ProgramData"), falling back to `C:\ProgramData`).
  - **Worker thread**, started at the first lock and joined when the last object is destroyed (never inside DllMain). Every 50 ms it:
    1. Checks `config.bin` every 4th tick, by last-write time and size.
       - Read it with FILE_SHARE_READ|WRITE|DELETE.
       - Apply it only if size == 32 and magic, version and checksum match. Clamp cap and margin.
       - Missing or invalid → keep the last good values (defaults at start), and clear kFlagConfigValid.
    2. Writes the telemetry header and every active slot:
       - copies the RT atomics;
       - exchanges maxGainReductionDb and outputPeakDb back to 0 dB/floor;
       - stamps lastProcessFileTime when framesProcessed changed;
       - stamps the heartbeat and its FILETIME.
    3. After 10 s of healthy processing (frames advancing, no safe mode), resets the guard to 0.
    4. Flushes queued log lines to `apo.log`: UTF-8, roll to `.old` past 256 KB. Only non-RT code queues lines, under a mutex.
  - **Telemetry file.** `<data>\telemetry\apo.bin`: CreateFileW (OPEN_ALWAYS, all share modes), size kTelemetryBytes, unnamed CreateFileMappingW and MapViewOfFile. Any failure → telemetry off, logged once, no crash.
  - **Guard.** The first lock in the process reads `<data>\telemetry\guard.bin`, increments uncleanStarts and writes it back. If the value was already ≥ kGuardSafeModeAt, the process runs in safe mode and sets kFlagSafeMode.
  - **Slots.** Claim and release them under the runtime mutex. `instanceId` comes from a process counter starting at 1.

- [ ] **Step 1: Write `ApoHost.cpp` with these checks.** Each prints `PASS Hn` or `FAIL Hn: why`, and the exit code is non-zero if any fail.
  - **Set-up:**
    - Create `%TEMP%\SoundProtectionHost-<pid>\telemetry` and set `SOUNDPROTECTION_DATA_DIR` to its parent before LoadLibrary.
    - Write config golden 1 (`SpShared.h`) as `config.bin`.
    - Create every object through `DllGetClassObject` → `IClassFactory::CreateInstance`, with a test outer object that forwards QueryInterface for everything except IUnknown to the inner non-delegating unknown.
  - **H1 Aggregation:**
    - both classes are created with an outer object;
    - QI through the outer object works for IAudioProcessingObject, IAudioProcessingObjectRT, IAudioProcessingObjectConfiguration and IAudioSystemEffects2;
    - releasing everything brings `DllCanUnloadNow` to S_OK;
    - non-aggregated creation also works;
    - aggregated creation asking for IAudioProcessingObject returns CLASS_E_NOAGGREGATION.
  - **H2 Registration properties:** GetRegistrationProperties gives Flags 0xF, u32MaxInstances 0xFFFFFFFF, and the right CLSIDs.
  - **H3 Initialize:**
    - APOInitSystemEffects2, null stores, mode DEFAULT → S_OK;
    - cb = 0 → error;
    - data null → error.
  - **H4 Formats:**
    - float32 48 kHz stereo → S_OK;
    - int16 48 kHz stereo → S_FALSE with a float32 48 kHz stereo suggestion.
  - **H5 Lock and process across formats.** For each of (48000, 2, mask 3), (44100, 1, mask 4) and (96000, 8, mask 0x63F), with re-lock in between:
    - 3 s of 480-frame periods of 1 kHz at amplitude 0.708, in place;
    - every Safety Cap output ≤ ceiling −8 dBFS + 1e-6;
    - after 2 s, the Leveler's output loudness is −20 ± 1.5;
    - then UnlockForProcess.
  - **H6 Non-finite and junk:**
    - NaN, Inf and 1e30 in the input → all outputs finite and ≤ ceiling (Safety Cap) or ≤ 1 (Leveler);
    - BUFFER_SILENT with a buffer pre-filled with 1e30 → output all zeros or near zero (≤ 1e-6) and flags BUFFER_VALID.
  - **H7 Separate in/out buffers:** the same input processed out of place equals the in-place output within 1e-6.
  - **H8 Telemetry:** within 500 ms, `telemetry\apo.bin` has the magic, a heartbeat that increases between two reads 200 ms apart, a Leveler slot and a Safety Cap slot with frames > 0, mode = Default, flags with kFlagConfigValid|kFlagEnabled, and capDb −20.
  - **H9 Config:**
    - write golden 2 (cap −35.5, disabled): within 600 ms telemetry shows configSequence 42, capDb −35.5 and the enabled flag clear; a loud tone is no longer reduced by the Leveler after 200 ms more;
    - then write a config whose checksum is wrong (sequence 43): configSequence stays 42 and kFlagConfigValid is clear.
  - **H10 Guard:** write `guard.bin` with uncleanStarts = 3, unload, reload the DLL, create and lock.
    - kFlagSafeMode is set within 500 ms.
    - The Leveler output equals its input (loud tone, clamped to ±1).
    - The Safety Cap output is ≤ the ceiling.
    - After the last unlock, `guard.bin` reads 0.
  - **H11 Many streams:** 40 Leveler objects locked and processing at once: no crash, exactly 32 slots active in telemetry, all outputs finite.
  - **H12 Latency:** GetLatency of the locked Safety Cap at 48 kHz equals (240 + 8)/48000 s in 100-ns units, ±1.
  - **Clean-up:** release everything, unload, delete the temp folder.
- [ ] **Step 2: Run `native\build-apo.cmd stub`.** Expected: the host fails because the classes aren't written yet.
- [ ] **Step 3: Implement the DLL** as specified.
- [ ] **Step 4: Run `native\build-apo.cmd stub`.** Expected: H1–H4 and H6–H12 pass. H5's loudness part and H9's "no longer reduced" part can only pass with the real DSP: under the stub, print them as SKIP (stub).
- [ ] **Step 5: If `native\out\dsp\DspTests.exe` exists and passes, run `native\build-apo.cmd` with the real DSP.** Expected: H1–H12 all pass. If Task 1 isn't done yet, say so in the report.
- [ ] **Step 6: Report** the checks and their results, the exports (`dumpbin /exports`), and confirmation that `dumpbin /dependents` shows no `vcruntime`/`msvcp` DLLs.

---

### Task 3: Installer, undo, repair and status (PowerShell), with a rehearsal test

**Files:**
- Create: `installer\SoundProtection.Install.ps1`
- Create: `installer\Install.Tests.ps1`: plain PowerShell asserts, exit code 0 or 1, no Pester.

**Interfaces:**
- Consumes:
  - `native\out\apo\SoundProtection.Apo.dll` (default `-DllSource`: `"$PSScriptRoot\..\native\out\apo\SoundProtection.Apo.dll"`);
  - the CLSIDs and file names in `native\apo\SpShared.h`;
  - golden 1 bytes for the default config.
- Produces:
  - `C:\Program Files\SoundProtection\{SoundProtection.Apo.dll, SoundProtection.Install.ps1}`;
  - `C:\ProgramData\SoundProtection\{config.bin, telemetry\, backup\<stamp>\, install-journal.json, install-log.txt}`;
  - the desktop shortcut `Sound Protection - Undo.lnk`;
  - exit codes 0 (done and verified), 3 (installed but not running yet: restart needed), 1 (failed and rolled back).
  - The tray app (Task 4) calls `-Action Repair` and `-Action Uninstall` on the installed copy.

**Parameters:**
- `-Action Install|Uninstall|Repair|Status` (required).
- `-Endpoints` (default `@('Headphones')`).
- `-RegistryBase` (default `'HKLM:\SOFTWARE'`).
- `-DataDir` (default `"$env:ProgramData\SoundProtection"`).
- `-InstallDir` (default `"$env:ProgramFiles\SoundProtection"`).
- `-DllSource`.
- `-Rehearsal` (skips the elevation check, services, files, `regsvr32`, ACLs, shortcut and verify; touches only the registry under `-RegistryBase` and files under `-DataDir`).
- `-WhatIf` (prints every change and makes none).

**Self-elevation:** for real Install, Repair or Uninstall when not elevated, relaunch `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <self> <same args>` with `-Verb RunAs -Wait`, print the tail of `install-log.txt`, and exit with the child's exit code.

**Keys** (relative to `-RegistryBase`):
- Render endpoints: `Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{id}`. An endpoint matches when `Properties` value `{a45c254e-df1c-4efd-8020-67d146a850e0},2` equals one of `-Endpoints` and `FxProperties` has `{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13` and `,15`.
- Leveler: append `{026BF7A9-ED1A-4C62-ADC6-142802421CBA}` to the end of `,13` (REG_MULTI_SZ) if no entry already equals it (case-insensitive).
- Safety Cap: append `{148D0A3C-621B-40F5-B7FD-13E6CB834BCB}` to the end of `,15` the same way.
- Exclusive mode off: `Properties` value `{b3f8fa53-0004-438e-9003-51a46e139bfc},3` = DWORD 0. This property is only reported to be the right one, so a failure here is a warning, not an error.
- Test key: `Microsoft\Windows\CurrentVersion\Audio` value `DisableProtectedAudioDG` = DWORD 1.

**Install order:**
1. **Back up.** `reg export` the matched endpoint keys and the Audio key into `backup\<yyyyMMdd-HHmmss>\` (skipped in rehearsal).
2. **Journal.** Write `install-journal.json` before any change. For every value the run will touch, record key, name, existed, kind and data.
   - If a journal already exists, keep its records. Add only values it doesn't have.
   - This is how a repeated install or a repair keeps the true originals.
3. **Stop the audio services.** Record the running services that depend on AudioEndpointBuilder, then `Stop-Service AudioEndpointBuilder -Force`.
4. **Files.** Copy the DLL and the script itself into `-InstallDir`.
5. **Register.** `& "$env:SystemRoot\System32\regsvr32.exe" /s "<installed dll>"`. Check exit code 0 and that both `HKLM:\SOFTWARE\Classes\CLSID\{…}\InprocServer32` defaults equal the installed path.
6. **Data folder and ACLs.** Create `-DataDir` and `telemetry`, then:
   - `icacls "<data>" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)M" "*S-1-5-19:(OI)(CI)RX" "*S-1-15-2-1:(OI)(CI)RX" "*S-1-15-2-2:(OI)(CI)RX"`
   - `icacls "<data>\telemetry" /grant "*S-1-5-19:(OI)(CI)M" "*S-1-15-2-1:(OI)(CI)M" "*S-1-15-2-2:(OI)(CI)M"`
   - Write `config.bin` = golden 1 only if it is missing.
7. **Test key**, the list appends and exclusive mode.
8. **Start the services.** Start AudioEndpointBuilder, then Audiosrv, then the recorded dependents. Wait up to 20 s for Running.
9. **Shortcut.** Create `Sound Protection - Undo.lnk` on the desktop: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<InstallDir>\SoundProtection.Install.ps1" -Action Uninstall`.
10. **Verify.**
    - Write a 2 s, 48 kHz, 16-bit stereo, 1 kHz WAV at −40 dBFS (amplitude 328, 20 ms fades) to `%TEMP%` and play it with `System.Media.SoundPlayer.Play()`.
    - For up to 6 s, read `telemetry\apo.bin` (FileShare ReadWrite). Pass needs the magic, an increasing heartbeat, a Safety Cap slot and a Leveler slot each with frames > 0.
    - Pass → `VERIFY: PASS` and exit 0. Fail → `VERIFY: NOT RUNNING (<what was missing>). A restart of the PC may be needed for the test key.` and exit 3.
- Any error in steps 4–8 → roll back from the journal (as Uninstall), log it, exit 1.

**Uninstall:**
1. Stop the services.
2. Restore every journal record exactly: write the original kind and data, or delete the value if it didn't exist.
3. Run `regsvr32 /u /s`, then remove the remaining `Classes\CLSID\{ours}` and `Classes\AudioEngine\AudioProcessingObjects\{ours}` keys.
4. Delete `-InstallDir` and the shortcut.
5. Start the services.
6. Rename the journal to `install-journal.done.json`. Keep `backup\` and the log.
- **No journal:** remove our two CLSIDs from `,13` and `,15` on every endpoint, unregister, and leave `DisableProtectedAudioDG` as it is, with a warning.

**Repair:** steps 3, 5, 7 and 8 again, with the journal-preserving rule, then Verify. It finds endpoints by name, because the GUID can change after a driver reinstall.

**Status (read-only, no elevation):** print JSON with:
- `installed` (files and COM keys present);
- per endpoint: `name`, `id`, `levelerListed`, `safetyCapListed`, `effectsDisabled` (`{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5` = 1 in `FxProperties` or `Properties`), `exclusiveAllowed`;
- `testKey`;
- `heartbeatAgeMs` (null if there is no telemetry).

- [ ] **Step 1: Write `Install.Tests.ps1`.** It builds a rehearsal copy:
  - Read the real Headphones endpoint (read-only), and copy its `FxProperties` and `Properties` values exactly (name, kind, data) into `HKCU:\Software\SoundProtectionRehearsal\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{same id}\…`.
  - Create an empty `…\CurrentVersion\Audio` key.
  - Run with `-Rehearsal -RegistryBase 'HKCU:\Software\SoundProtectionRehearsal' -DataDir "$env:TEMP\SpRehearsal-<pid>"`.

  Tests:
  - **R1 Install:** `,13` ends with the Leveler CLSID, `,15` ends with the Safety Cap CLSID, `DisableProtectedAudioDG` = 1, `,3` = 0, the journal exists, and `config.bin` equals golden 1.
  - **R2 Idempotent:** a second Install gives one copy of each CLSID, and the journal still records `DisableProtectedAudioDG` as not existing before.
  - **R3 Uninstall restores byte-exact:** every value name, kind and data under the rehearsal endpoint and Audio keys equals the snapshot taken before R1. `DisableProtectedAudioDG` and `,3` are absent again.
  - **R4 Repair:** after Install, remove our CLSID from `,15` by hand; Repair puts it back at the end.
  - **R5 Interrupted:** Install with a fault injected after the list appends (switch `-FailAfter Lists`, rehearsal only) exits 1, and the keys equal the snapshot.
  - **R6 WhatIf on the real system:** take a read-only snapshot of the real Headphones keys and the Audio key, run `-Action Install -WhatIf` without elevation, take the snapshot again. Assert it is unchanged and the output names all three changes.
  - **R7 Status:** `-Action Status` returns parseable JSON with the Headphones endpoint.
  - **Clean-up:** delete the rehearsal key and the temp folder, even on failure.
- [ ] **Step 2: Run** `powershell -NoProfile -ExecutionPolicy Bypass -File installer\Install.Tests.ps1`. Expected: failures, because the script isn't written yet.
- [ ] **Step 3: Implement `SoundProtection.Install.ps1`.**
- [ ] **Step 4: Run the tests.** Expected: `R1`–`R7 PASS`, exit 0, and nothing written under HKLM. The R6 snapshot proves the last part.
- [ ] **Step 5: Report** the results and the exact command lines the controller will use.

---

### Task 4: Tray app as the plug-in's remote control (C#)

**Files:**
- Create in `src\SoundProtection.Core\`:
  - `ProtectionConfig.cs`
  - `ApoTelemetry.cs`
  - `ProtectionHealth.cs`
  - `ProtectionEngine.cs`, which includes `TurnDownDetector`
- Modify in `src\SoundProtection.Core\`:
  - `Settings.cs`
  - `Messages.cs`
  - `PanelState.cs`, only if needed
  - `TrayText.cs`, adding `IconKind.Down`
- Create in `src\SoundProtection.App\`:
  - `ProtectionPaths.cs`
  - `ProtectionRegistry.cs`
- Modify in `src\SoundProtection.App\`: `TrayApp.cs`, `PanelForm.cs`, `Sampler.cs`, `IconFactory.cs`, `DemoPng.cs`, `MeterBar.cs`
- Tests in `tests\SoundProtection.Tests\`:
  - `ProtectionConfigTests.cs`
  - `ApoTelemetryTests.cs`
  - `ProtectionHealthTests.cs`
  - `ProtectionEngineTests.cs`
  - additions to `SettingsTests.cs` and to a messages test file

**Interfaces:**
- Consumes:
  - the byte layouts, golden bytes and CLSIDs in `native\apo\SpShared.h`;
  - the installer's path `C:\Program Files\SoundProtection\SoundProtection.Install.ps1` with `-Action Repair|Uninstall`;
  - the existing types `AppLevel`, `AudioSnapshot`, `Db`, `Settings`, `SettingsStore`, `OverEvent`, `Messages`, `NotificationThrottle`.
- Produces:
  - **Config:**
    - `public sealed record ProtectionConfig(uint Sequence, bool Enabled, float CapDb, float MarginDb)`;
    - `public static class ProtectionConfigFile { const int Size = 32; static uint Fnv1a(ReadOnlySpan<byte>); static byte[] ToBytes(ProtectionConfig); static ProtectionConfig? FromBytes(ReadOnlySpan<byte>); static ProtectionConfig? TryRead(string path); static void Write(string path, ProtectionConfig c); static float CeilingDb(float capDb, float marginDb); }`.
    - `ToBytes` clamps the cap to [−60, 0] and the margin to [0, 30].
    - `Write` writes `path + ".tmp"`, then `File.Move(tmp, path, overwrite: true)`. On IOException it retries up to 10 times, 50 ms apart, then throws.
  - **Telemetry:**
    - `public enum SlotKind : uint { Free, Leveler, SafetyCap }` and `public enum ApoMode : uint { Unknown, Default, Raw, Communications, Speech, Media, Movie, Notification, Other }`;
    - `public sealed record TelemetryHeader(uint WriterPid, uint ConfigSequence, ulong Heartbeat, DateTime HeartbeatUtc, uint Flags, float CapDb, float CeilingDb)` with `ConfigValid`, `SafeMode` and `Enabled` bool properties;
    - `public sealed record TelemetrySlot(SlotKind Kind, uint InstanceId, uint SampleRate, uint Channels, ApoMode Mode, uint ClipperHits, ulong FramesProcessed, DateTime LastProcessUtc, float InputDb, float GainReductionDb, float MaxGainReductionDb, float OutputPeakDb)`;
    - `public sealed record TelemetrySnapshot(TelemetryHeader Header, IReadOnlyList<TelemetrySlot> Slots)`, holding non-free slots only;
    - `public static class ApoTelemetry { const int Size = 2112; static TelemetrySnapshot? Parse(ReadOnlySpan<byte>); static TelemetrySnapshot? TryRead(string path); }`.
    - Parse returns null when the buffer is short or the magic or version is wrong. FILETIME is read with `DateTime.FromFileTimeUtc`; 0 maps to `DateTime.MinValue`.
  - **Health:**
    - `public enum ProtectionStatus { NotInstalled, Down, Off, Idle, Protected }`;
    - `public sealed record EndpointFacts(string Name, bool LevelerListed, bool SafetyCapListed, bool EffectsDisabled, bool ExclusiveAllowed)`;
    - `public sealed record RegistryFacts(bool TestKeyOn, IReadOnlyList<EndpointFacts> Endpoints)`;
    - `public sealed record HealthResult(ProtectionStatus Status, string Text, IReadOnlyList<string> Warnings)`;
    - `public static class ProtectionHealth { static HealthResult Evaluate(RegistryFacts reg, TelemetrySnapshot? tel, DateTime nowUtc, bool configEnabled, double capDb, bool audioActive); }`.
    - Rules, first match wins, with exact texts:
      1. No endpoint has both CLSIDs listed → NotInstalled, "Protection is not installed".
      2. A listed endpoint has EffectsDisabled → Down, "Windows switched off sound effects on {Name}".
      3. !TestKeyOn → Down, "The test key is off, so Windows will not load the plug-in".
      4. !configEnabled → Off, "Protection is off".
      5. tel != null && tel.Header.SafeMode → Down, "The plug-in is in safe mode after repeated crashes".
      6. audioActive && (tel == null || nowUtc − HeartbeatUtc > 2 s || no SafetyCap slot with nowUtc − LastProcessUtc ≤ 2 s) → Down, "The plug-in is not running".
      7. audioActive → Protected, "Protected: cap {Db.Format(capDb)}".
      8. Otherwise → Idle, "Protected (nothing playing)".
    - Warnings: "Apps can take exclusive control of {Name}" for each listed endpoint with ExclusiveAllowed.
  - **Engine:**
    - `public sealed record AppTurnDown(string Name, double TurnDownDb)`;
    - `public sealed record ProtectionState(ProtectionStatus Status, string StatusText, double LoudestInputDb, double TurnDownDb, double SafetyCapDb, IReadOnlyList<AppTurnDown> Apps)`;
    - `public sealed record TurnDownEvent(DateTimeOffset Start, TimeSpan Duration, double MaxTurnDownDb, double CapDb, string? App)`;
    - `public sealed class ProtectionEngine { event Action<TurnDownEvent>? TurnedDown; ProtectionState Process(TelemetrySnapshot? tel, HealthResult health, IReadOnlyList<AppLevel> apps, double capDb, DateTimeOffset now); }`.
    - **Fresh slots:** a slot counts only when LastProcessUtc is within 1 s of `now`.
    - **LoudestInputDb:** the maximum InputDb over fresh Leveler slots, or Db.Floor.
    - **TurnDownDb:** the maximum GainReductionDb over fresh Leveler slots.
    - **SafetyCapDb:** the maximum GainReductionDb over fresh Safety Cap slots.
    - **Matching:**
      - Take fresh Leveler slots with InputDb > −70, sorted by InputDb descending, and apps with PeakDbfs > −90, sorted by PeakDbfs descending. Pair them in order.
      - Each pair gives an AppTurnDown(app.Name, slot.GainReductionDb).
      - Extra slots become "Other sound".
    - **Detector:** a TurnDownEvent opens when TurnDownDb ≥ 1.0. Readings < 500 ms apart merge, and an episode closes at 5 s. App = the matched app with the largest turn-down, at the moment of the maximum.
  - **Settings:**
    - `ProtectionEnabled` (bool, default true);
    - `int? SettingsVersion`, with `Settings.CurrentVersion = 2`;
    - the default `CeilingDb` becomes −20.0, and its doc comment says "the cap: loudness before the volume slider".
    - `SettingsStore.Load`: no file → `new Settings { SettingsVersion = 2 }`. A loaded file with `(SettingsVersion ?? 0) < 2` → CeilingDb = −20.0 and SettingsVersion = 2.
  - **Messages:**
    - `Messages.TurnedDown(TurnDownEvent e)` → Title `$"Turned down by {Db.Format(e.MaxTurnDownDb)}"`, Body `$"{app} went over your cap ({Db.Format(e.CapDb)}) for {duration}. Sound Protection turned it down."`;
    - `Messages.Line(TurnDownEvent e)` → `"HH:mm:ss  {app}  turned down {Db.Format(max)} ({duration})"`;
    - app and duration as in the existing `Over` helpers.

**App behaviour:**
- **`ProtectionPaths`:** DataDir = `%ProgramData%\SoundProtection`, ConfigPath, TelemetryPath, and InstallerPath = `%ProgramFiles%\SoundProtection\SoundProtection.Install.ps1`.
- **`ProtectionRegistry.Read()`** is read-only. It enumerates the render endpoints, reads the name property, whether both CLSIDs are listed (case-insensitive), EffectsDisabled (`{1da5d803-…},5` = 1 in `FxProperties` or `Properties`), ExclusiveAllowed (`{b3f8fa53-…},3` absent or ≠ 0) and the test key. It returns RegistryFacts listing only endpoints that have at least one of our CLSIDs, or are named Headphones.
- **Timing and data flow:**
  - Read registry facts every 2 s and telemetry every 100 ms, off the UI thread.
  - Run the existing sampler unchanged for the ear-level meter.
  - audioActive = the latest snapshot has Apps.Count > 0 or PeakDbfs > −90.
- **Config writes.**
  - The trigger: start-up, and every change of CeilingDb or ProtectionEnabled.
  - The action: if DataDir exists, write `config.bin` with sequence = the existing file's sequence + 1, Enabled, CapDb = CeilingDb and MarginDb = 12. Errors are logged with Logging.ErrorOnce.
- **Tray menu.**
  - Directly under "Open panel": a checkable "Protection" item that toggles ProtectionEnabled.
  - Before Exit: "Repair protection…" and "Remove protection (undo)…". Each starts `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<InstallerPath>" -Action Repair|Uninstall`, and is disabled when InstallerPath is missing.
- **Icon and tooltip.**
  - Status Down or NotInstalled → the new `IconKind.Down`, a red icon from IconFactory.
  - The tooltip is "Sound Protection: {StatusText}", cut to 127 characters.
- **Panel.** The amber `_limiter` label shows StatusText: green for Protected/Idle, amber for Off, red for Down/NotInstalled. Each warning goes on its own line.
  - **Status Protected, Idle or Off:**
    - the big number = LoudestInputDb;
    - caption = "loudest app now, before your volume slider" or, when TurnDownDb ≥ 1, "{App} turned down by {Db.Format(TurnDownDb)}";
    - the meter shows LoudestInputDb with the cap marker, and Over = TurnDownDb ≥ 1;
    - "Playing now" lists the AppTurnDown entries;
    - "Use that peak" is hidden;
    - the slider tooltip reads "Drag to set the cap: loudness before your volume slider. Louder apps are turned down to it."
  - **Status NotInstalled or Down:** the current ear-level display and over-alerts, unchanged.
- **Alerts.** TurnDown events go to the panel's event list and to balloons, which respect AlertsEnabled, ShowNotifications and the 10 s per-app throttle. Over-events are raised only in NotInstalled or Down.
- **`DemoPng`:** `--demo-png <path>` renders the protection-mode panel, and `--demo-png-legacy <path>` the old one.

- [ ] **Step 1: Write the failing tests:**
  - **`ProtectionConfigTests`:**
    - `ToBytes_Golden1` and `ToBytes_Golden2`: the exact bytes from `SpShared.h`;
    - `FromBytes_RoundTrip`;
    - `FromBytes_BadChecksum_ReturnsNull`;
    - `FromBytes_WrongMagic_ReturnsNull`;
    - `FromBytes_Short_ReturnsNull`;
    - `ToBytes_Clamps`: −80 → −60, 5 → 0, margin 40 → 30, −1 → 0;
    - `Fnv1a_KnownVectors`: empty → 0x811C9DC5, "a" → 0xE40C292C;
    - `CeilingDb_Rule`: −20/12 → −8, −5/12 → −1, −60/0 → −60;
    - `Write_ThenTryRead_RoundTrip` (temp dir);
    - `Write_RetriesWhileLocked`: hold the file open with FileShare.Read for 200 ms on another thread; Write succeeds after it is released.
  - **`ApoTelemetryTests`:**
    - a helper writes a 2112-byte buffer at the `SpShared.h` offsets;
    - `Parse_ReadsHeaderAndSlots` (every field);
    - `Parse_SkipsFreeSlots`;
    - `Parse_WrongMagic_ReturnsNull`;
    - `Parse_Short_ReturnsNull`;
    - `Parse_ZeroFileTime_IsMinValue`.
  - **`ProtectionHealthTests`:** one test per rule 1–8, asserting the exact Status and Text, plus `ExclusiveAllowed_AddsWarning` and `SafeMode_IsDown`.
  - **`ProtectionEngineTests`:**
    - `Match_PairsLoudestSlotWithLoudestApp`;
    - `StaleSlots_AreIgnored`;
    - `TurnDown_EventAfterMergeGap`: 3 dB for 1 s, then 0, gives one event 500 ms later with Max 3 and App "Zoom";
    - `TurnDown_BelowThreshold_NoEvent` (0.8 dB);
    - `TurnDown_SplitsAtFiveSeconds`.
  - **`SettingsTests` additions:**
    - `OldFile_WithoutVersion_MigratesCapToMinus20`;
    - `CurrentFile_KeepsCap` (version 2, cap −33 stays −33);
    - `NewSettings_HaveVersion2AndCapMinus20AndProtectionOn`.
  - **Messages:** `TurnedDown_TitleBodyAndLine`, with exact strings for Zoom, max 6.24, cap −20 and 3.4 s:
    - Title "Turned down by 6.2 dB";
    - Body "Zoom went over your cap (-20.0 dB) for 3.4 s. Sound Protection turned it down.";
    - Line "HH:mm:ss  Zoom  turned down 6.2 dB (3.4 s)", with the time formatted as in the existing Line.
- [ ] **Step 2: Run `dotnet test SoundProtection.sln`.** Expected: compile errors, because the types don't exist yet.
- [ ] **Step 3: Implement Core**, then App, as specified.
- [ ] **Step 4: Run `dotnet test SoundProtection.sln`.** Expected: all pass (105 + new). `dotnet build SoundProtection.sln` reports 0 warnings.
- [ ] **Step 5: Render** `dotnet run --project src\SoundProtection.App -- --demo-png "%TEMP%\sp-panel.png"` and `--demo-png-legacy "%TEMP%\sp-panel-legacy.png"`. View both: nothing clipped, and the status line is readable.
- [ ] **Step 6: Publish** `dotnet publish src\SoundProtection.App\SoundProtection.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist-staging`. Expected: `dist-staging\SoundProtection.exe`. Do NOT touch `dist\` or the running process.
- [ ] **Step 7: Report** the test counts, the warnings count, the two PNG paths and the publish output.

---

### Task 5: Integration, final review, deploy and live check (controller)

- [ ] **Step 1: Integration build.**
  - `native\build-dsp.cmd` gives 27/27.
  - `native\build-apo.cmd` (real DSP) gives H1–H12 PASS.
  - `installer\Install.Tests.ps1` gives R1–R7 PASS.
  - `dotnet test SoundProtection.sln` is all green, and the build has 0 warnings.
- [ ] **Step 2: Final review.** A fresh reviewer, on the most capable model, reads `native\`, `installer\` and the C# changes against this plan and the Review Focus. Fix Critical and Important findings test-first. Ledger the minors.
- [ ] **Step 3: Deploy, which changes the PC** (approved by Ariel: "full plugin a-z now").
  1. Stop the running tray app.
  2. Copy `dist-staging\*` over `dist\`.
  3. Run `powershell -NoProfile -ExecutionPolicy Bypass -File installer\SoundProtection.Install.ps1 -Action Install`. Ariel clicks one UAC prompt, and audio pauses while the services restart.
  4. Exit code 0 → go on. Exit code 3 → ask Ariel to restart the PC; after the restart, run `-Action Status` and Repair if needed. Exit code 1 → stop, read the log, report.
- [ ] **Step 4: Live check (silent).**
  - `-Action Status` shows both CLSIDs listed, the test key on and effects on.
  - The telemetry heartbeat is fresh while `dist\SoundProtection.exe --tone -40 1500` plays: a Leveler slot with mode Default, and a Safety Cap slot.
  - Set Start with Windows (`HKCU\...\Run\SoundProtection` = `"<dist exe>"`).
  - Start the tray app. Its tooltip says Protected or "Protected (nothing playing)".
- [ ] **Step 5: Record.** Write `docs\03\INSTALL-REPORT.md` (what changed, how to undo, test results), update the ledger and the memory, and reply to Ariel in short.
