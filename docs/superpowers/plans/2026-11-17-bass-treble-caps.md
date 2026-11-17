# Bass and Treble Caps (V2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add two cut-only caps, bass and treble, inside the Safety Cap plug-in, with two tray sliders. With the caps off, V1 behaviour must not change by a single bit.

**Architecture:**
- **DSP.** A new header-only class `sp::BandGuard` (in `native\dsp\SpDsp.h`) runs first in the Safety Cap APO's `OnProcess`, ahead of the limiter.
- **Settings.** They come from a new 32-byte `bands.bin` in the data folder. The plug-in's runtime watches it next to `config.bin` and publishes it through the existing settings sequence lock.
- **Tray.** It writes `bands.bin` from two new sliders and shows the live cuts from two new telemetry floats.

**Tech Stack:**
- C++17: MSVC 14.44, Windows SDK 26100, `/W4 /WX /fp:precise`, no framework.
- C#: .NET 10 WinForms, xunit.

**Spec:** `docs/superpowers/specs/2026-11-17-bass-treble-caps-design.md`. The contract it defines is already in `native\apo\SpShared.h`.

## Global Constraints

- **No git commits, and no staging by workers.** Leave every change in the working tree; the controller stages after each review.
- **Hands off the live system.**
  - Never touch `dist\`, where the V1 tray is running, or `C:\Program Files\SoundProtection`, `C:\ProgramData\SoundProtection`, the registry or the audio services.
  - Never register the DLL, play sound through the speakers, or run the installer.
  - Tests use temp folders only (`SOUNDPROTECTION_DATA_DIR`, `%TEMP%`).
- **The contract.** `native\apo\SpShared.h` belongs to the controller: read it, never edit it. `bands.bin`'s layout, flags, clamps and golden bytes, and the slot floats `bassCutDb` (offset 56) and `trebleCutDb` (offset 60), are exactly as written there. The telemetry header version stays 1.
- **DSP rules** (the top comment of `SpDsp.h`):
  - Process and SetParams never allocate, lock, wait or call the OS.
  - Every output sample is finite.
  - One thread at a time per object.
  - `/fp:precise`, no fast-math.
  - `in == out` is allowed.
  - Formats: 8 kHz to 768 kHz, 1 to 64 channels.
- **BandGuard constants (verbatim):**

  | | Centre | Q | Detector | Attack | Release |
  |---|---|---|---|---|---|
  | Bass | 160 Hz | 0.6 | 25 ms | 20 ms | 300 ms |
  | Treble | 3500 Hz | 0.6 | 5 ms | 5 ms | 150 ms |

  - The maximum cut is 12 dB.
  - The snap to exactly 0 happens at ≤ 0.01 dB.
  - The detector power is clamped at 1.0e4.
  - A cap goes from −60 to 0 dB, relative to a full-scale sine.
- **Exact copy.** A band that is off, or under its cap, gives an output bit for bit equal to the input.
- **Builds.**
  - `native\build-dsp.cmd` and `native\build-apo.cmd [stub]` compile with `/W4 /WX` and zero warnings.
  - `dotnet build SoundProtection.sln` reports 0 warnings.
- **Existing tests stay green:** DSP T01–T27, host H1–H17, and the whole C# suite.
- **UI copy, verbatim:**
  - The words "Bass", "Treble", "Off" and "cutting N.N dB".
  - Bass tooltip: "Bass cap: when the deep, boomy part of the sound goes over this level, only that part is turned down. All the way right = off."
  - Treble tooltip: "Treble cap: when the sharp, high part of the sound goes over this level, only that part is turned down. All the way right = off."

## Review Focus

1. **A cut that moves must not click.** The cut changes as a voice gets louder or softer, and its steps must stay small and smooth. T29 checks the cut's step size frame by frame during onset; T35 checks that the release has no step.
2. **Windows can change the output format while the caps are on** (48 kHz to 44.1 kHz). The re-lock must prepare again at the new rate, and the cap must keep working. Covered by H18 Part C.
3. **`bands.bin` can be torn mid-replace, garbage, short or deleted.** The plug-in keeps the last good values and never crashes. Covered by H19, plus the C# writer's temp-then-move.
4. **A V1 settings file has no band fields.** It must load with both caps Off. Covered by `SettingsTests.Load_V1File_BandsOff`.
5. **Ariel switches protection off in the tray.** Both cuts release to 0, and the output becomes an exact copy. Covered by T35 Part B (DSP) and H18 Part D (through the DLL).

## File Structure

| File | Change | Task |
|---|---|---|
| `native\apo\SpShared.h` | the contract: `bands.bin`, band constants, slot cut floats | controller, **done** |
| `native\dsp\SpDsp.h` | add `BandGuardParams` and `BandGuard` (new section after SafetyCap); add BandGuard to the top comment's list | 1 |
| `native\dsp\tests\DspTests.cpp` | T28–T40; header comment and `static_assert` 27 → 40 | 1 |
| `native\apo\SpRuntime.h`, `SpRuntime.cpp` | `bands.bin` watcher; Settings fields; slot atomics; telemetry write | 2 |
| `native\apo\SpSafetyCapApo.cpp` | run BandGuard ahead of the cap; telemetry | 2 |
| `native\apo\SpDspStub.h` | a pass-through BandGuard with the same API | 2 |
| `native\apohost\ApoHost.cpp` | H18–H21 and a `bands.bin` writer helper | 2 |
| `src\SoundProtection.Core\BandsConfig.cs` (new) | `BandsConfig`, `BandsConfigFile`, `BandsConfigWriter` | 3 |
| `src\SoundProtection.Core\BandText.cs` (new) | UI text helpers and tooltips | 3 |
| `src\SoundProtection.Core\ProtectionConfig.cs` | move the replace-with-retry write into `SharedFile.WriteReplacing` | 3 |
| `src\SoundProtection.Core\ApoTelemetry.cs` | parse the two cut floats | 3 |
| `src\SoundProtection.Core\ProtectionEngine.cs` | `BassCutDb` and `TrebleCutDb` in `ProtectionState` | 3 |
| `src\SoundProtection.Core\Settings.cs` | `BassCapDb` and `TrebleCapDb` | 3 |
| `src\SoundProtection.App\ProtectionPaths.cs`, `Sampler.cs`, `TrayApp.cs`, `PanelForm.cs`, `DemoPng.cs` | path, sync, wiring, two panel rows, demo | 3 |
| `tests\SoundProtection.Tests\BandsConfigTests.cs` (new), `BandTextTests.cs` (new), `SettingsTests.cs`, `ApoTelemetryTests.cs`, `ProtectionEngineTests.cs` | tests | 3 |

Release work (the controller, after the final review): `dotnet publish` to `dist-staging\`, and Ariel's notes in `docs\04\`.

---

### Task 1: BandGuard, the bass and treble caps DSP

**Files:**
- Modify: `native\dsp\SpDsp.h`. Add a new section after `SafetyCap` named "BandGuard: bass and treble caps (V2)", and add one line about BandGuard to the file's top comment.
- Test: `native\dsp\tests\DspTests.cpp`. Add T28–T40, register them in `kTests`, and change the header comment and the `static_assert` from 27 to 40.

**Interfaces:**
- Consumes: `detail::` helpers already in `SpDsp.h` (`IsFinite`, `Sanitize`, `FormatOk`, `FailClosedChannels`, `OnePoleCoef`, `kPi`, `kMaxChannels`).
- Produces, exactly:

```cpp
namespace sp {
struct BandGuardParams {
    bool  enabled     = true;   // the global switch (config.bin "on"); false releases both cuts to 0
    bool  bassOn      = false;
    float bassCapDb   = 0.0f;   // dB re a full-scale sine; clamped to [-60, 0]; NaN keeps the old value
    bool  trebleOn    = false;
    float trebleCapDb = 0.0f;   // the same
};
class BandGuard {
public:
    static constexpr double kBassHz = 160.0, kTrebleHz = 3500.0, kQ = 0.6, kMaxCutDb = 12.0;
    static constexpr double kBassDetectMs = 25.0, kBassAttackMs = 20.0, kBassReleaseMs = 300.0;
    static constexpr double kTrebleDetectMs = 5.0, kTrebleAttackMs = 5.0, kTrebleReleaseMs = 150.0;
    static constexpr double kSnapDb = 0.01, kMaxDetectPower = 1.0e4;
    static constexpr float  kMinCapDb = -60.0f, kMaxCapDb = 0.0f;
    void Prepare(std::uint32_t sampleRate, std::uint32_t channels) noexcept;   // never allocates; keeps the params
    void Reset() noexcept;                    // clears filter, detector and cut state; keeps the params
    void SetParams(const BandGuardParams& p) noexcept;   // unchanged values do nothing
    void Process(const float* in, float* out, std::uint32_t frames) noexcept;  // interleaved; in == out allowed
    float BassCutDb() const noexcept;         // current cut in dB, >= 0
    float TrebleCutDb() const noexcept;
    std::uint32_t LatencyFrames() const noexcept { return 0; }
};
}  // namespace sp
```

**Behaviour that the signatures do not fix:**
- **Preparing.**
  - All state lives in fixed member arrays sized for 64 channels: two SVF states per band per channel, plus per-channel scratch. Prepare cannot throw or allocate.
  - Prepare first records `detail::FailClosedChannels(channels)`. Then, if `!detail::FormatOk(rate, channels)`, the object is unprepared.
- **Unprepared Process.** Each sample is copied with NaN/Inf → 0, over the recorded channel count (0 channels: write nothing). No cut is applied.
- **SetParams.**
  - Caps are clamped to [−60, 0], and a NaN keeps the old value.
  - A band is *active* when `enabled && on`.
  - It precomputes `thrMs = 0.5 * 10^(capDb / 10)` per band.
- **Algorithm.** It runs once per frame, bass band first, then treble on the bass band's output:

```text
coefficients (Prepare), Simper trapezoidal SVF, unity-peak band-pass:
  g = tan(pi * f0 / fs);  k = 1 / kQ;  a1 = 1 / (1 + g * (g + k));  a2 = g * a1;  a3 = g * a2
  aDet/aAtt/aRel = detail::OnePoleCoef(ms, fs) for the band's detector, attack and release times

per frame, for each band in order (v0[ch] = this band's input, double; the bass band's input is the sanitised frame):
  for each channel:  v3 = v0 - ic2;  v1 = a1*ic1 + a2*v3;  v2 = ic2 + a2*ic1 + a3*v3
                     ic1 = 2*v1 - ic1;  ic2 = 2*v2 - ic2;  flush |ic1|, |ic2| < 1e-30 to 0
                     bp[ch] = k * v1
  p   = min(max over channels of bp[ch]^2, kMaxDetectPower)
  ms += aDet * (p - ms)
  target = (active && ms > thrMs) ? min(10*log10(ms / thrMs), kMaxCutDb) : 0
  gr += (target - gr) * (target > gr ? aAtt : aRel);   if (target == 0 && gr <= kSnapDb) gr = 0
  c   = (gr > 0) ? 1 - 10^(-gr / 20) : 0              // this frame already uses the new c
  for each channel:  out_band[ch] = v0[ch] - c * bp[ch]

output: if both bands' c == 0, write the sanitised input sample itself (bit-exact);
        else write the treble band's output, clamped to +-FLT_MAX, as float
```

- No `log10` when a band is under its cap. No `pow` or `exp` when `gr == 0`.
- Reading every input sample of the frame before writing any output keeps `in == out` safe.

**Tests.** All run at 48 kHz stereo unless the test says otherwise.
- Level means peak amplitude in dB re 1.0, `20*log10(max|y|)`, over the stated window.
- "Settled" means: measure over the last 200 ms of a 1 s run.
- Every Process and SetParams call goes inside the existing NoAlloc scope.

- [ ] **Step 1: Write the failing tests T28–T40 in `DspTests.cpp`**
  - `T28_bandguard_idle_is_bit_exact`. The signal is speech-like: harmonics 1–20 of 220 Hz at amplitude 1/n, scaled to a peak of 0.3, times a 4 Hz envelope, with a 3500 Hz component at 0.05 in its second half, 2 s.
    - Part A: both bands off. The output equals the input (memcmp).
    - Part B: both bands on at −3 dB. The output equals the input.
    - Part C: Part B run in place. The output equals the input.
  - `T29_bandguard_treble_centre_tone_capped`. A 3500 Hz sine at amplitude 0.1 (−20 dB); treble on at −26; bass off.
    - Settled level −26.0 ± 0.5 dB.
    - The level over 45–50 ms after onset is ≤ −25.0 dB.
    - Running 1 frame per call for the first 50 ms, consecutive `TrebleCutDb()` readings differ by ≤ 0.1 dB.
    - `BassCutDb() == 0`.
  - `T30_bandguard_bass_centre_tone_capped`. A 160 Hz sine at 0.1; bass on at −26; treble off.
    - Settled level −26.0 ± 0.5 dB.
    - The level over 140–150 ms is ≤ −25.0 dB.
    - `TrebleCutDb() == 0`.
  - `T31_bandguard_tone_between_bands_untouched`. A 1000 Hz sine at 0.1; both bands on at −24. Over the whole 1 s run the output equals the input bit for bit, and both cuts read 0.
  - `T32_bandguard_never_boosts`. Both bands on at −60, so the 12 dB maximum cut is in force.
    - For each of 40, 80, 160, 320, 640, 1000, 2000, 3500, 5000, 8000, 12000 and 16000 Hz at amplitude 0.5: the settled peak is ≤ 0.5 × 1.0001.
    - With 1 s of white noise (uniform ±0.5, fixed seed): the output energy over the last 0.5 s is ≤ the input energy over the same window.
  - `T33_bandguard_max_cut_is_12_db`. A 3500 Hz sine at 0.5; treble on at −60.
    - Settled level −18.0 ± 0.1 dB, which is −6 − 12.
    - `TrebleCutDb()` is 12.0 ± 0.01.
  - `T34_bandguard_release_returns_to_exact_copy`. Treble on at −26.
    - 500 ms of 3500 Hz at 0.1, then 3 s of 1000 Hz at 0.01.
    - `TrebleCutDb() == 0.0f` exactly by 1.5 s after the switch.
    - From that frame on, the output equals the input bit for bit.
  - `T35_bandguard_switching_off_releases_smoothly`. Use 3500 Hz at 0.1 with treble on at −26 until the cut settles near 6 dB, keeping the tone running throughout.
    - Part A: SetParams `trebleOn = false`.
      - 1 ms later the cut is ≥ 5.9 dB.
      - Consecutive per-frame readings differ by ≤ 0.1 dB.
      - The cut reads exactly 0 within 1.5 s, and from then on the output equals the input.
    - Part B: the same with `enabled = false` instead.
  - `T36_bandguard_nonfinite_and_huge_input_safe`.
    - Bands off: NaN, +Inf and −Inf samples come out as exactly 0, and the other samples are unchanged.
    - Both bands on at −30: 100 ms of samples alternating ±FLT_MAX with some NaN, then 5 s of 1000 Hz at 0.01.
      - Every output sample is finite.
      - Both cuts read 0 by the end.
      - The last 200 ms equals the input bit for bit.
  - `T37_bandguard_in_place_and_chunk_size_independent`. The T28 signal, both bands on at −30, so cuts are active.
    - Run it as one call, in chunks of 1, in chunks of 7, and in chunks of 480.
    - Run the chunks-of-480 case in place as well.
    - All five outputs are bit-identical.
  - `T38_bandguard_formats`.
    - At 8000, 44100, 96000, 192000 and 768000 Hz (stereo), the T29 tone is capped to −26.0 ± 0.5 dB settled.
    - At 48000 Hz with 1, 6 and 64 channels (the tone in every channel), the same holds.
    - `LatencyFrames() == 0`.
  - `T39_bandguard_unprepared_passes_sanitised`.
    - `Prepare(7999, 2)` and `Prepare(48000, 65)`: each copies its channel count of samples with NaN/Inf → 0 and everything else unchanged. Cuts read 0.
    - `Prepare(48000, 0)`: writes nothing (the output buffer keeps its sentinel).
    - A later valid `Prepare` processes normally (the T29 check passes).
    - Prepare itself makes zero heap allocations (counting new).
  - `T40_bandguard_channels_linked`.
    - Left: 3500 Hz at 0.1. Right: 3500 Hz at 0.01. Treble on at −26.
    - Settled left −26.0 ± 0.5 dB, and right −46.0 ± 0.5 dB (the same 6 dB cut).
  - Register T28–T40 in `kTests`. Change the header comment ("For each of the 27 tests …", "ALL 27 PASSED", "of 27 tests") to 40, and change the `static_assert` to `== 40` with the message "the plan pins exactly 40 tests".

- [ ] **Step 2: Run `native\build-dsp.cmd`.** Expected: `build-dsp: COMPILE FAILED`, because `BandGuard` is undefined.
- [ ] **Step 3: Implement `BandGuardParams` and `BandGuard` in `SpDsp.h`** as specified above. Name the private members freely.
- [ ] **Step 4: Run `native\build-dsp.cmd`.** Expected: 40 PASS lines, `ALL 40 PASSED`, exit code 0, and no compiler warnings.
- [ ] **Step 5: Do not commit.** Report the changed files.

---

### Task 2: Plug-in wiring for the caps

**Files:**
- Modify: `native\apo\SpRuntime.h`, `native\apo\SpRuntime.cpp`, `native\apo\SpSafetyCapApo.cpp` (and its top comment), and `native\apo\SpDspStub.h`.
- Test: `native\apohost\ApoHost.cpp`. Add H18–H21, the `bands.bin` writer helper, and the `Run(...)` lines, and extend the header comment.

**Interfaces:**
- **Consumes:**
  - Task 1's `BandGuard` and `BandGuardParams`, exactly as in its Interfaces block.
  - `SpShared.h`: `SpBands`, `SP_BANDS_FILE`, `kBandsMagic`, `kBandsVersion`, `kBandBass`, `kBandTreble`, `kMinBandCapDb`, `kMaxBandCapDb`, and `SpTelemetrySlot::bassCutDb` / `trebleCutDb`.
- **Produces:**
  - `apo::Settings` gains `uint32_t bandFlags = 0; float bassCapDb = 0.0f; float trebleCapDb = 0.0f;`.
  - `SlotRt` gains `std::atomic<float> bassCutDb{0.0f}; std::atomic<float> trebleCutDb{0.0f};`.
  - Two log lines, verbatim:
    - `bands applied: sequence %u, bass %s %.1f dB, treble %s %.1f dB` (each `%s` is `on` or `off`);
    - `bands.bin is missing or invalid; keeping the last good values` (once per bad episode, as the `config.bin` line is).

**Runtime (`SpRuntime.*`)**
- **Path.** `bandsPath_` is resolved with the other paths, as `<data>\bands.bin`.
- **Reading.** `ReadBandsFile(path, SpBands&)` mirrors `ReadConfigFile`: exactly 32 bytes, the magic, version 1, and FNV-1a over bytes 0..27.
- **`CheckBands()`.** It mirrors `CheckConfig()`, with its own change signature (write time, size, and the "recent write" re-read) and its own in-use fields.
  - It applies the file as: `flags & (kBandBass | kBandTreble)`, and each cap clamped to [−60, 0] with NaN → 0.
  - It publishes only when a value differs, and logs the "applied" line.
  - Call it right after each existing `CheckConfig()` call: the first-lock one and the worker one.
- **Publishing.**
  - `PublishSettingsLocked` also stores `setBandFlags_`, `setBass_` and `setTreble_`, which are new atomics.
  - `ReadSettings` copies them inside the same sequence-lock read.
- **Telemetry.**
  - Where a claimed slot's atomics are reset, also reset `bassCutDb` and `trebleCutDb` to 0.
  - `WriteTelemetry` copies both into `t.bassCutDb` and `t.trebleCutDb`. They stay 0 for Leveler slots.

**Safety Cap (`SpSafetyCapApo.cpp`)**

The member is `BandGuard band_;`.

| Hook | What it does |
|---|---|
| `OnPrepare` | `band_.Prepare(rate, channels)`, then `cap_.Prepare` as today |
| `OnSettings` (not safe) | `band_.SetParams({s.Enabled(), (s.bandFlags & kBandBass) != 0, s.bassCapDb, (s.bandFlags & kBandTreble) != 0, s.trebleCapDb})` |
| `OnReset` (not safe) | `band_.Reset()` |
| `OnProcess` (not safe) | `band_.Process(buf, buf, frames)`, then `cap_.Process(buf, buf, frames)` |
| `OnTelemetry` | store `safe_ ? 0 : band_.BassCutDb()` and `TrebleCutDb()` into the slot |
| `OnLatencyFrames` | unchanged |

**Stub (`SpDspStub.h`).** The same `BandGuardParams` and `BandGuard` public API with pass-through bodies:
- Process copies `in` to `out`, and `in == out` is allowed;
- cuts read 0;
- `LatencyFrames()` is 0.

**Host checks.** The runtime is shared by every check in one host run, so each check first writes the `bands.bin` it needs (the next sequence number) and waits up to 2 s for the "bands applied: sequence N" log line. Find the Safety Cap slot in the telemetry file the way H8 does, and reach safe mode the way H10 does, without leaving the shared runtime in safe mode for later checks.
- [ ] **Step 1: Write H18–H21 in `ApoHost.cpp`.** H18 has four parts and needs the real DSP; under the stub it prints SKIP for its level and telemetry asserts, the way H5 does.
  - `H18 Bass and treble caps through the DLL`.
    - Part A: `config.bin` on (cap −20, margin 12) and `bands.bin` treble on at −30, bass off. A Safety Cap object at 48 kHz stereo. Process 2 s of a 3500 Hz sine at amplitude 0.05623 (−25 dB) in 480-frame buffers.
      - The output peak over the last 0.5 s is −30.0 ± 0.7 dB.
      - The slot's `trebleCutDb` is 5.0 ± 0.7, and `bassCutDb` is 0.
    - Part B: then 2 s of 1000 Hz at −25 dB. Over the last 0.5 s the output equals the input delayed by the reported latency, bit for bit.
    - Part C: unlock, re-lock at 44100 Hz stereo, and repeat Part A's level check.
    - Part D: write `config.bin` with `enabled = 0`, leaving `bands.bin` as it is, and process the 3500 Hz tone for 2 s.
      - `trebleCutDb` reads 0.
      - The last 0.5 s equals the delayed input bit for bit.
  - `H19 A missing or bad bands.bin leaves the last good caps`. Start from a valid `bands.bin` with both caps off, applied. Then, in turn:
    1. the same bytes with treble on at −30 but a flipped checksum byte;
    2. a 31-byte file;
    3. deleting the file.
    - After each, process the 3500 Hz tone at −25 dB for 1 s. The last 0.5 s equals the delayed input bit for bit.
    - The log has the "missing or invalid" line.
  - `H20 The caps add no latency`. `GetLatency` with `bands.bin` treble on at −30 equals `GetLatency` with both off. Runs in both builds.
  - `H21 Safe mode bypasses the caps`. In safe mode, with `bands.bin` treble on at −30 and the 3500 Hz tone at −25 dB:
    - the output peak is −25.0 ± 0.1 dB;
    - `trebleCutDb` is 0.
- [ ] **Step 2: Run `native\build-apo.cmd stub`.** Expected: a compile failure, because `bandFlags` and the stub's `BandGuard` are not there yet.
- [ ] **Step 3: Implement the runtime, Safety Cap and stub changes above.**
- [ ] **Step 4: Run `native\build-apo.cmd stub`.** Expected: H1–H21 print PASS, H18's real-DSP asserts print SKIP (stub), and the exit code is 0.
- [ ] **Step 5: Run `native\build-apo.cmd`.** Expected: H1–H21 PASS, exit 0, no warnings. Then run `native\build-dsp.cmd`. Expected: `ALL 40 PASSED`.
- [ ] **Step 6: Do not commit.** Report the changed files and the host's full PASS/SKIP list.

---

### Task 3: Tray sliders, `bands.bin` writer and live cuts

**Files:**
- Create:
  - `src\SoundProtection.Core\BandsConfig.cs`
  - `src\SoundProtection.Core\BandText.cs`
  - `tests\SoundProtection.Tests\BandsConfigTests.cs`
  - `tests\SoundProtection.Tests\BandTextTests.cs`
- Modify:
  - in `src\SoundProtection.Core\`: `ProtectionConfig.cs`, `ApoTelemetry.cs`, `ProtectionEngine.cs` and `Settings.cs`;
  - in `src\SoundProtection.App\`: `ProtectionPaths.cs`, `Sampler.cs`, `TrayApp.cs`, `PanelForm.cs` and `DemoPng.cs`;
  - in `tests\SoundProtection.Tests\`: `SettingsTests.cs`, `ApoTelemetryTests.cs` and `ProtectionEngineTests.cs`.

**Interfaces:**
- **Consumes:** the `bands.bin` layout and goldens in `SpShared.h`; slot offsets 56 and 60; the existing `ProtectionConfigFile.Fnv1a`, `ProtectionConfigFile.NextSequence`, `SharedFile.ReadUpTo` and `Db.Format`.
- **Produces, exactly:**

```csharp
public sealed record BandsConfig(uint Sequence, bool BassOn, float BassCapDb, bool TrebleOn, float TrebleCapDb);
public static class BandsConfigFile {
    public const int Size = 32; public const float MinCapDb = -60f, MaxCapDb = 0f;
    public static byte[] ToBytes(BandsConfig config);
    public static BandsConfig? FromBytes(ReadOnlySpan<byte> bytes);   // exact size, magic, version 1, checksum; caps clamped, NaN -> 0f; flags bits 0/1
    public static BandsConfig? TryRead(string path);                  // SharedFile.ReadUpTo(path, Size + 1)
    public static void Write(string path, BandsConfig config);        // SharedFile.WriteReplacing
    public static BandsConfig FromSliders(uint sequence, double bassCapDb, double trebleCapDb); // on = cap < 0; clamp; NaN -> 0
}
public sealed class BandsConfigWriter {
    public BandsConfigWriter(string path);
    public bool LastAttemptFailed { get; }
    public bool Sync(double bassCapDb, double trebleCapDb);   // writes only when flags or caps differ from the file; true = written
}
public static class BandText {
    public const string BassTip = "...";    // verbatim from Global Constraints
    public const string TrebleTip = "...";  // verbatim from Global Constraints
    public static string Value(double capDb);   // >= 0 or NaN -> "Off"; else Db.Format(capDb), e.g. "-32.0 dB"
    public static string Cut(double cutDb);     // not finite or < 0.05 -> ""; else "cutting " + cut.ToString("0.0", InvariantCulture) + " dB"
}
```

- `internal static class SharedFile` gains `void WriteReplacing(string path, byte[] bytes)`. It is the temp-write, move and retry body moved out of `ProtectionConfigFile.Write`, with the same behaviour, and `ProtectionConfigFile.Write` now calls it.
- `TelemetrySlot` gains the trailing parameters `float BassCutDb = 0f, float TrebleCutDb = 0f`, which `Parse` reads from `slot[56..]` and `slot[60..]`.
- `ProtectionState` gains the trailing parameters `double BassCutDb = 0.0, double TrebleCutDb = 0.0`.
  - `ProtectionEngine.Process` sets them to the largest finite, non-negative values among **fresh** Safety Cap slots.
  - Anything else reads as 0.
- `Settings` gains:
  - `public const double MinBandCapDb = -60.0, MaxBandCapDb = 0.0, DefaultBandCapDb = 0.0;`
  - `public double BassCapDb { get; set; } = 0.0; public double TrebleCapDb { get; set; } = 0.0;`
  - `SettingsStore.Load` clamps both to [−60, 0], with NaN → 0. `CurrentVersion` stays 2.
- `ProtectionPaths.BandsPath` is `Path.Combine(DataDir, "bands.bin")`.
- `PanelForm` gains:
  - `public event Action<double>? BassChanged;` and `TrebleChanged`, raised only when the user moves a slider;
  - `public void SetBands(double bassCapDb, double trebleCapDb)`, which raises no events.

**App wiring**
- **`Sampler`.**
  - `ProtectionLoop` creates `new BandsConfigWriter(ProtectionPaths.BandsPath)`.
  - `StartConfigSyncIfNeeded` writes `config.bin` and then `bands.bin` in the same work item. Each sits in its own try/catch; the bands one logs with `Logging.ErrorOnce("bands", "Could not write the bands config", ex)`.
  - `settingsNotSaved` is `writer.LastAttemptFailed || bandsWriter.LastAttemptFailed`.
- **`TrayApp`.**
  - `_panel.BassChanged += db => Mutate(s => s.BassCapDb = db)`, and the same for `TrebleChanged`.
  - Call `_panel.SetBands(...)` wherever `SetCeiling` is called from settings.
  - `Mutate` also requests a config write when `BassCapDb` or `TrebleCapDb` changed.
- **`PanelForm`.**
  - After the "Cap (dB)" row come two rows, each with:
    - a muted label, "Bass" or "Treble";
    - a `CeilingSlider` (range −60 to 0, step 0.5, Dock Fill);
    - a value label (`BandText.Value`);
    - a muted cut label (`BandText.Cut`).
  - Tooltips: `BandText.BassTip` and `BandText.TrebleTip`.
  - `ShowState` fills the cut labels from `protection.State.BassCutDb` and `TrebleCutDb`, but only when the panel shows the before-the-slider (plug-in) display. Otherwise they are blank.
  - Make the form tall enough that nothing is clipped.
- **`DemoPng`.** The demo shows Bass Off and Treble at −32.0 dB with "cutting 4.0 dB".

- [ ] **Step 1: Write the failing tests.**
  - **`BandsConfigTests`:**
    - `ToBytes_GoldenB1`: `ToBytes(new(1, true, -30f, true, -35f))` equals the B1 bytes.
    - `ToBytes_GoldenB2`: `ToBytes(new(42, false, 0f, true, -27.5f))` equals the B2 bytes.
    - `FromBytes_GoldenB1_RoundTrips`.
    - `FromBytes_Rejects` (theory) returns null for 31 bytes, 33 bytes, a changed magic, version 2, and a flipped checksum byte.
    - `FromBytes_ClampsCaps`: a bass of −80 and a treble of +5, with a valid checksum, read as −60 and 0.
    - `FromBytes_NaNCapReadsZero`.
    - `FromSliders_OnWhenBelowZero`: `FromSliders(3, -30.0, 0.0)` equals `new(3, true, -30f, false, 0f)`.
    - `Writer_WritesWhenMissing_ThenSkipsSame` (temp dir):
      - `Sync(-30, 0)` returns true, and the file decodes to seq 1, bass on −30, treble off;
      - a second `Sync(-30, 0)` returns false, and the file bytes are unchanged;
      - `Sync(-30, -35)` returns true, with seq 2.
    - `Writer_RapidChanges_EndWithLast`: 20 `Sync` calls with treble from −20 to −39 leave treble −39 on disk.
    - `Writer_BackToOff`: after an on value, `Sync(0, 0)` writes flags 0.
  - **`BandTextTests`:**

    | Call | Result |
    |---|---|
    | `Value(0.0)` | `"Off"` |
    | `Value(double.NaN)` | `"Off"` |
    | `Value(-32.0)` | `"-32.0 dB"` |
    | `Cut(0.0)` | `""` |
    | `Cut(0.04)` | `""` |
    | `Cut(4.04)` | `"cutting 4.0 dB"` |
    | `Cut(double.NaN)` | `""` |

  - **`SettingsTests`:**
    - `Defaults_BandsOff`: both are 0.
    - `Load_V1File_BandsOff`: JSON without band fields loads as 0 and 0.
    - `Load_ClampsBands`: −100 loads as −60, and 3 as 0.
    - `Save_Load_RoundTripsBands`: −30 and −35 survive.
  - **`ApoTelemetryTests`:** `Parse_ReadsBandCuts`. A Safety Cap slot with 3.5f at offset 56 and 4.25f at 60 parses to `BassCutDb` 3.5 and `TrebleCutDb` 4.25.
  - **`ProtectionEngineTests`:**
    - `Process_TakesLargestFreshSafetyCapCuts`: fresh treble cuts of 3.0 and 5.0 give 5.0; a bass cut of 1.0 gives 1.0.
    - `Process_IgnoresStaleAndNonFiniteCuts`: a stale slot at 9.0 is ignored; NaN reads as 0; −2 reads as 0.
- [ ] **Step 2: Run `dotnet test SoundProtection.sln`.** Expected: compile errors, because the types don't exist yet.
- [ ] **Step 3: Implement the Core changes**: BandsConfig, BandText, SharedFile.WriteReplacing, telemetry, engine and settings.
- [ ] **Step 4: Implement the App changes**: paths, Sampler, TrayApp, PanelForm and DemoPng.
- [ ] **Step 5: Run `dotnet test SoundProtection.sln` and `dotnet build SoundProtection.sln`.** Expected: every test passes (the previous count plus the new ones), and the build reports 0 warnings.
- [ ] **Step 6: Render the demo.** Run `dotnet run --project src\SoundProtection.App -- --demo-png "<path the controller gives>"`. Expected: a PNG with Bass Off and Treble −32.0 dB "cutting 4.0 dB", with nothing clipped.
- [ ] **Step 7: Do not commit.** Report the changed files and the test counts.
