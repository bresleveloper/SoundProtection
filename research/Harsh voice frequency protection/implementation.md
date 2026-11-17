# Building frequency-band voice protection inside the Sound Protection APO plug-in

*Reading guide.* Research notes for the report writer, current as of 2026-11-16 (November 2026). Our own code and docs are cited by path and line; paths are relative to `C:\Users\Ariel\source\MS PlayGround\Sound Protection\`. Web facts carry an inline link. Marks used below:

- **DERIVED**: my own mathematics from the cited formulas. The steps are shown so they can be checked.
- **MODEL-CHECK**: a number from a throwaway Python model of the proposed algorithm, run from standard input (nothing written to disk; no project test, build or host was run; the project is untouched). The model is not our code. Its setup is written down once, in Key Question 1 (Inferences).
- **DESIGN**: my proposal.
- **UNCERTAIN**: not verified, or not found.

No effort or time estimates are given. Cross-references, one line each: the *causes* researcher says why these voices sound hard; the *methods and products* researcher compares protection approaches; the *per-person tuning* researcher chooses band centres, ceilings and time constants. This file only says what the code must make adjustable and what the maths allows.

---

## Key Question 1: Which method fits our code and rules best: (a) cut-only dynamic EQ, (b) crossover split with a per-band limiter, (c) static EQ, (d) FFT/STFT suppression or de-essing?

### Takeaway
Build (a) as a cascade of fixed-pole subtractive band cuts, `y = x - c * bandpass(x)`, with `c` set per band by a band-pass detector and a gain computer. It is the only method that keeps our existing rules (bit-exact copy when idle, zero latency, no allocation, response never above 1), and the same code also gives (c) static EQ (constant `c`) and de-essing (a high band with fast ballistics). It is a soft, centre-weighted cap, not a hard per-band ceiling: (b) and (d) can get closer to a hard ceiling but each gives up an existing rule, and the exposure guarantee stays with the Leveler and the Safety Cap whichever method is used.

### Cited Findings
- Our DSP library already states the rules any new stage must keep: every gain is at most 1; every output sample is finite; `Process`, `Add` and `SetParams` never allocate, free, lock, wait or call the OS (only `Prepare` allocates); one thread per object; `/fp:precise`; in == out is allowed; an unprepared object fails closed; `Prepare` accepts 8 kHz to 768 kHz and 1 to 64 channels — `native\dsp\SpDsp.h` lines 6-25.
- Exact transparency is an explicit project value: a recovered limiter is exactly unity "and the output is again an exact copy of the input, delayed by LatencyFrames" — `native\dsp\SpDsp.h` lines 33-35; tested bit-exactly in `native\dsp\tests\DspTests.cpp`: T10 Part C (comment at lines 574-575, "a bit-exact copy of the input") and the comments at lines 1349 and 1663. The existing bit-comparison helper is `SameBits` (lines 1231-1233, a `memcmp` of two float vectors).
- The chain that is installed: each app's stream goes through the Leveler, the streams are mixed, the Safety Cap is last (limit = cap + 12 dB, never above -1 dB), then the Windows volume slider; Realtek's own effects are before ours — `docs\03\INSTALL-REPORT.md` lines 14-26 and 47.
- Split-band de-essing and dynamic EQ: a de-esser either acts on the whole signal (wideband) or only on the sibilant band (split-band); its side-chain is band-pass filtered; the dynamic-EQ form lowers a parametric EQ band's gain as sibilance rises; sibilance sits roughly at 3-6 kHz for male and 6-8 kHz for female voices; rise and fall times under 10 ms are suggested — [Wikipedia: De-essing](https://en.wikipedia.org/wiki/De-essing) (the page carries sourcing warnings; the numbers are rough).
- Linkwitz-Riley LR4 is two cascaded 2nd-order Butterworth filters; the low and high outputs are in phase with each other and out of phase with the input; both are -6 dB at the crossover, with 180 degrees of phase shift there; the sum has a flat magnitude — [Rane Note 160](https://www.ranecommercial.com/legacy/note160.html); same definition and the all-pass-like sum in [Wikipedia: Linkwitz-Riley filter](https://en.wikipedia.org/wiki/Linkwitz%E2%80%93Riley_filter).
- A 3-band LR4 split needs allpass compensation: Faust defines `crossover3LR4(cf1, cf2, x) = crossover2LR4(cf1, x) : svf.ap(cf2, 1.0 / sqrt(2.0)) , crossover2LR4(cf2)`, so the low band gets a 2nd-order allpass (Q = 1/sqrt 2) at the upper crossover — [Faust `filters.lib`](https://github.com/grame-cncm/faustlibraries) (author Dario Sanfilippo, STK-4.3). LSP's `Crossover.h` shows the same schema (LPF 0, then APF 1, then APF 2 for band 0; HPF 0, LPF 1, APF 2 for band 1) and an LR4 slope option of 24 dB/octave — [lsp-dsp-units](https://github.com/lsp-plugins/lsp-dsp-units).
- STFT/overlap-add: per-frame spectral multiplication is a time-varying filter; windows must satisfy the COLA condition; "sampling density must be increased along the unit circle when spectral modifications are to be performed" — [J. O. Smith, Overlap-Add (OLA) STFT Processing](https://ccrma.stanford.edu/~jos/sasp/Overlap_Add_OLA_STFT_Processing.html).
- A static EQ inside an APO is an established product approach: Equalizer APO is a GPLv2 APO whose installer offers pre-mix and post-mix installation, and, because it is unsigned, "the protected audio path breaks, making some software using DRM refuse to output any sound" — [Equalizer APO documentation](https://sourceforge.net/p/equalizerapo/wiki/Documentation/); project page [SourceForge](https://sourceforge.net/projects/equalizerapo/).
- For filters whose parameters change every sample, Wishnick (iZotope, DAFx-14) proposes the state-variable filter, gives a proof of time-varying stability for its transposed direct-form II realisation, derives EQ shapes for it, and compares it with other structures in objective and listening tests — [DAFx-14 paper page](https://dafx.de/paper-archive/details/yfd4LbdXr2Cywsqxfby8ug).

### Inferences
**MODEL-CHECK setup (applies to every MODEL-CHECK number in this file).**
- 48 kHz; double-precision states; float32 rounding of input and output wherever "identical samples" is claimed.
- Bell stage: a cascade of bands, low to high frequency. Each band is a trapezoidal (TPT) state-variable filter with `g = tan(pi*f0/fs)`, `k = 1/Q`, and band-pass `b = k*v1` (gain 1 at `f0`).
- Detector: one-pole mean-square of `b`, time constant `max(5 ms, 4/f0)`; level `L = 10*log10(2*p)` dB (a full-scale sine reads 0 dB).
- Gain computer: `target GR = clamp(L - ceiling, 0, maxCut)`, smoothed by the project's dB-domain one-pole (attack time constant 3 ms, release 100 ms, snapped to exactly 0 when the target is 0 and GR is under the snap value); `c = 1 - 10^(-GR/20)`; `y = y - c*b`.
- Default bands: 250, 800, 3000, 6000 Hz; Q 1.4; ceiling -20 dB; max cut 24 dB. "Speech-like" = harmonic series, F0 220 Hz, amplitude `1/sqrt(k)` times a Lorentzian formant envelope (700, 1200, 2900 Hz), with random phases.
- Crossover model: LR4 (two cascaded SVF sections at Q 1/sqrt 2) at 800 and 3000 Hz, a 2nd-order allpass at 3000 Hz on the low band, one detector and one gain per band with the same ballistics.

**Comparison (MODEL-CHECK unless marked DERIVED or UNCERTAIN).**

| Criterion | (a) fixed-pole bell bank | (b) LR4 split + per-band limiter | (c) static EQ | (d) STFT / spectral suppression |
|---|---|---|---|---|
| Exact copy when nothing is cut | Yes. `y = x - 0*bp` is exact. 48000 of 48000 float32 samples identical on a speech-like signal at -30 dBFS rms with every ceiling at -10 dB (largest gain reduction 0.0000 dB); exact again 963 ms after a 0.5 s 3 kHz burst at -6 dBFS (snap 0.001 dB) or 1884 ms (snap 1e-7 dB; release 100 ms) | No. The sum of the bands is an allpass copy: 0 of 30000 samples identical; max abs difference 1.09 for a 0.5-peak noise input. A subtractive split is exact, but its complementary band is 1.5x (+3.5 dB) at the crossover (DERIVED: LP4 = -0.5 there, so 1 - LP4 = 1.5) | Never idle: the cut is always on | Not bit-exact by construction (windowed overlap-add; equal to the input only up to rounding, INFERRED); an idle bypass is possible |
| Response at or below 1 at every frequency | Yes for each band with `c` in [0, 2] in a cascade (DERIVED, below) | Yes if band gains are at most 1: the LR4 outputs are in phase, so the sum is at most `g_lo*abs(LP) + g_hi*abs(HP) = 1` (DERIVED) | Yes by design | Yes if per-bin gains are at most 1 |
| Latency | 0 | 0 | 0 | At least one analysis window; `GetLatency` and H12 change |
| Tone at a band centre vs ceiling | Within 0.02 dB (-20.02 dB for a ceiling of -20) | Within 0.2 dB deep in an outer band (-0.06, +0.13, +0.19 dB at 100, 200, 10000 Hz) | No detector | Per bin (not modelled) |
| Tone elsewhere, -6 dBFS vs ceiling -20 dB | Soft. One Q 1.4 band at 3 kHz: +13.3 dB over the ceiling at 0.5x f0, +11.5 at 0.7x, +7.4 at 0.85x, +7.6 at 1.18x, +11.4 at 1.41x, +13.4 at 2x. Octave-spaced pair: +8.9 dB at the midpoint (Q 1.4; +11.2 at Q 2.0, +12.5 at Q 2.8) | +2.0 to +2.2 dB at 400 and 6000 Hz, +4.3 to +6.2 dB between 600 and 4000 Hz: each band caps at its own ceiling and the adjacent bands add in phase (two bands at their ceilings sum to +6.0 dB, three to +9.5 dB, DERIVED) | n/a | The only one with a per-bin ceiling (UNCERTAIN: not modelled) |
| Memory | Fixed arrays | Fixed arrays | Fixed arrays | FFT buffers scale with the sample rate: allocation in `Prepare` that can throw, or large fixed arrays |
| Speech artefacts | None expected at slow ballistics; detector ripple costs +-0.02 dB at 3 kHz | Phase smear of the allpass sum | None | Pre-echo and musical noise are the classic risks (UNCERTAIN: not tested here) |
| Fit to de-essing | Same code, a high band, fast ballistics | The classic split-band form | No | Wideband or spectral forms |

**Pure-cut proof (DERIVED).** The unity-peak band-pass of a 2nd-order section is `H_bp = 1/(1 + j*x)` with `x = Q*(W - 1/W)` and `W = tan(pi*f/fs)/tan(pi*f0/fs)`. It lies on the circle `abs(z - 1/2) = 1/2`, so `Re(H_bp) = abs(H_bp)^2`. Then `H = 1 - c*H_bp` has `abs(H)^2 = ((1-c)^2 + x^2)/(1 + x^2)`, which is at most 1 for `0 <= c <= 2`, and exactly `1 - c` at the centre. The same holds for any weighted mean `z` of such band-passes (the disc is convex), so a parallel sum `y = x - sum(c_i*b_i)` is a pure cut whenever `sum(c_i) <= 2` (`abs(1 - C*z)^2 <= 1 - C*(2 - C)*abs(z)^2`). A cascade is a pure cut for any individual `c_i` in [0, 2], because it is a product of pure cuts.
- MODEL-CHECK: max `abs(H)` was 0.999997 to 0.99999996 over a 20 Hz to 23.9 kHz grid for Q in {0.7, 1, 2, 4} and `c` in {0.25, 0.5, 0.75, 1}, with centre gain `1 - c` to 9 decimals; the model's response matched the analytic formula to 0.004 dB.
- MODEL-CHECK on the sum rule: three overlapping parallel cuts (1000, 1300, 1700 Hz, Q 0.7, `c` = 1 each, sum 3) boost by up to +4.86 dB at 1302 Hz; with `c` = 1/3 each (sum 1) the maximum is 0.9997; two bands with `c` = 1 each (sum 2) give 1.0000.

**Verdict.**
- Take (a) as the first build: it meets the exact-copy, zero-latency, no-allocation and cut-only rules with simple code, and it contains (c) and the de-esser as configurations.
- Treat the result as a *comfort/harshness* control. Per-band ceilings are not a spectrum mask: the sum of what several bands let through can exceed any one ceiling (above), and a tone between band centres is barely cut. The hard guarantees stay where they are: the Leveler's loudness cap and the Safety Cap's peak ceiling.

**What would change the verdict.**
- If the requirement becomes "no tone anywhere in a band may exceed X", (a) cannot deliver it. (d) can, at the price of latency (a longer `GetLatency`, H12 changes), allocation sized by the sample rate, and artefacts that need listening tests. (b) can approach it in band interiors but overshoots by up to about +6 dB in the transition zones and loses the exact copy.
- If the exact copy is waived and steeper band edges are wanted, (b) is the second choice. It needs the allpass-compensated 3-band schema from Faust or LSP.
- If more bands are needed to reduce the off-centre leakage, the same cascade scales (about 18 flops per band, channel and sample, DERIVED); the open design issue then becomes how bands share a cut (Key Question 4).

### Gaps
- No STFT model was run; the (d) column is textbook relations only.
- Whether the user wants a soft harshness control or a hard per-band ceiling is a product decision outside this note; the numbers above are what each choice costs.
- No real speech recordings were used; the speech-like signal is synthetic.

---

## Key Question 2: Where does the stage go: per-stream SFX beside, before or after the Leveler, or end-of-line EFX before the Safety Cap?

### Takeaway
Put the stage in the end-of-line Safety Cap APO, in front of `cap_.Process`: one instance, always applied (raw streams included), no new CLSID, no added latency, and the cap right behind it absorbs any sample-peak growth. A per-stream copy inside the Leveler class is possible with the same code, but it only reaches Default-mode streams here and multiplies the cost by the number of streams.

### Cited Findings
- SFX is one instance per stream, before the mix; MFX comes after the per-mode mix; EFX comes after the mix of all modes and applies to every stream, including raw ones; SFX is not used for raw streams (in Windows 10 a raw stream loads MFX but not SFX); the EFX page suggests speaker protection and compensation as uses — [Audio Processing Object Architecture](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-processing-object-architecture); modes and raw handling in [Audio Signal Processing Modes](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-signal-processing-modes).
- Windows has seven processing modes (Raw, Default, Movies, Media, Speech, Communications, Notification); an app's stream category maps to a mode and the app cannot see which — [Audio Signal Processing Modes](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-signal-processing-modes).
- Each logical device normally has one APO of each type; composite keys relax that: `PKEY_CompositeFX_StreamEffectClsid = {D04E05A6-594B-4fb6-A80D-01AF5EED7D1D},13` is a REG_MULTI_SZ that allows several effects in one position (Windows 10 1803 and later; page dated 2023-03-06). The page does not state the list-order semantics explicitly — [Implementing Audio Processing Objects](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects); [PKEY_CompositeFX_StreamEffectClsid](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/pkey-compositefx-streameffectclsid).
- On Windows 11, a logical device's audio engine supports one pre-mix render APO per stream (render SFX) and one post-mix render APO (MFX) — [Windows 11 APIs for Audio Processing Objects](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/windows-11-apis-for-audio-processing-objects) (page dated 2023-08-11, updated 2025-07-18).
- APO real-time rules: "All methods that are members of real-time interfaces must be implemented as nonblocking members. They must not block, use paged memory, or call any blocking system routines", and "APOs should not introduce significant latency into the audio processing chain" — [Implementing Audio Processing Objects](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects); the `APOProcess` page says the method "is called from a real-time processing thread" and "must not touch paged memory and it should not call any system blocking routines" — [APOProcess](https://learn.microsoft.com/en-us/windows/win32/api/audioenginebaseapo/nf-audioenginebaseapo-iaudioprocessingobjectrt-apoprocess).
- `GetLatency` returns 100 ns units, is not real-time compliant, and the client sums the latencies of every APO in the chain — [IAudioProcessingObject::GetLatency](https://learn.microsoft.com/en-us/windows/win32/api/audioenginebaseapo/nf-audioenginebaseapo-iaudioprocessingobject-getlatency).
- Failure handling: failures of `CoCreateInstance`, `IsInputFormatSupported`, `IsOutputFormatSupported` and `LockForProcess` are counted; at a count of 10 the SFX, MFX and EFX of the endpoint are disabled by setting `PKEY_Endpoint_Disable_SysFx` to 1 — [Implementing Audio Processing Objects](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects).
- Equalizer APO's Configurator offers pre-mix and post-mix installation, and "Some sound card drivers disable options when they detect that another APO has been registered" — [Equalizer APO documentation](https://sourceforge.net/p/equalizerapo/wiki/Documentation/).
- Our registration: the Leveler is appended to the composite stream-effect list `{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13` after Realtek's `CRtkAPOSFX`; the Safety Cap is appended to the endpoint list `,15` after Sound Research's `SEAPOEFX` and Realtek's `CRtkAPOEFX`; the mode lists `{d3993a3f-99c2-4402-b5ec-a92a0367664b},5 ,6 ,7 ,11 ,12` are Default only; a Realtek driver reinstall rewrites the lists and drops ours — `docs\02\WIN11-BUILD-PLAN.md` lines 64, 119, 143-145; installer constants `installer\SoundProtection.Install.ps1` lines 77-78; install result "Realtek's own effects are still there, before ours" — `docs\03\INSTALL-REPORT.md` line 47.
- Zoom: it runs two processes (`docs\02\WIN11-BUILD-PLAN.md` line 48); whether it plays raw is unknown (lines 128-132 and 266); the install report lists "Zoom in raw mode" as item 5 of its "Declined to judge" lines, with the note "The Headphones' mode lists are Default only, and the EFX still applies" (`docs\03\INSTALL-REPORT.md` line 246), which is a question the reviewer set aside and not a measured result. The plug-in logs each copy's mode (plan line 266) and telemetry carries it (`native\apo\SpShared.h` lines 91-94 and the slot's `mode` field at line 101).
- The existing APO shapes: the Leveler reports latency 0 (`native\apo\SpLevelerApo.cpp` line 56); the Safety Cap reports `cap_.LatencyFrames()` (`native\apo\SpSafetyCapApo.cpp` line 58); its `OnProcess` is `cap_.Process(buf, buf, frames)` (line 43-45) and its `OnPrepare` ignores the channel mask (line 25-27). The latency is 5 ms look-ahead plus 8 frames, 248 frames at 48 kHz (`native\dsp\SpDsp.h` lines 703 and 712; checked by H12, `native\apohost\ApoHost.cpp` lines 1556-1588).
- If `OnPrepare` or `OnSettings` throws, `StartLocked` returns `E_OUTOFMEMORY` from `LockForProcess` — `native\apo\SpApoBase.cpp` lines 303-316. Per the Microsoft page above, that counts toward the 10-failure auto-disable of every effect on the endpoint, including Realtek's.
- The base class final clamp for the Safety Cap APO sits at the ceiling while the cap is enabled and at 1.0 when it is disabled (`limit_ = s.Enabled() ? CeilingLinearFloor(ceilingDb) : 1.0f`, `native\apo\SpSafetyCapApo.cpp` line 31; applied at `native\apo\SpApoBase.cpp` line 411); input is sanitised to finite values within +-1000 (`kSanitiseLimit`, `native\apo\SpApoBase.h` line 39 and `SanitiseCopy` line 88).

### Inferences
- **Recommended placement (DESIGN):** a `BandGuard` member inside the Safety Cap APO class, run first in `OnProcess`: `guard_.Process(buf, buf, frames); cap_.Process(buf, buf, frames);`. No new CLSID: a third class would need a new CLSID in `SpShared.h`, a class-factory entry in `SpDll.cpp`, registration properties, a third composite-list entry in the installer and its undo, a telemetry kind, and rows in the H-test tables (`kClsid[2]` and `kName[2]` in `ApoHost.cpp`).

| Aspect | Per-stream SFX (inside the Leveler class) | End-of-line EFX (inside the Safety Cap class) |
|---|---|---|
| Instances | One per stream (each app, Zoom has two processes) | One per endpoint |
| What it hears | One app's own sound: a loud voice is not hidden by another app's music | The mix of everything |
| Which streams | Only Default-mode streams here (mode lists are Default only); never raw | Everything, raw included; Zoom's mode does not matter |
| Cost | Stage cost times the number of streams | Stage cost once |
| Latency | 0 (IIR) | 0 added; Safety Cap stays 248 frames at 48 kHz |
| What follows the stage | The Leveler's final clamp at +-1.0 (`SpDsp.h` line 385): a cut that raises a sample peak can hard-clip | The Safety Cap, a look-ahead true-peak limiter (`SpSafetyCapApo.cpp` line 43-45) |
| Registry and installer change | None (same class) | None (same class) |

- **Why before the cap (MODEL-CHECK):** a band cut cannot raise a steady-state level, but it can raise a sample peak, because the filter's phase change reshapes the waveform. The worst-case sample-peak gain of `x - c*bp` is the L1 norm of its impulse response: 1.20 (+1.6 dB) at `c` 0.25 and Q 0.7, 1.46 (+3.3 dB) at `c` 0.5 and Q 1, 1.91 (+5.6 dB) at `c` 1 and Q 1, 2.15 (+6.6 dB) at `c` 1 and Q 4 (4 kHz at 48 kHz). Uniform noise (Q 1, `c` 0.75) gave +3.0 dB on its sample peak. A 0.5 s 3 kHz burst at -6 dBFS through the 4-band stage (14 dB of cut at the end) had an output-to-input peak ratio of 1.0000 (INFERRED: the first cycles pass before the attack acts). In practice peak growth is small for real programme, and the cap behind the stage holds the final ceiling. So the existing T22-style "never louder than the input, sample by sample" check cannot be applied to the new stage; use an energy and band-level check instead (Key Question 7).
- **One app versus all sound:** for the complaint (voices in calls and videos), the EFX covers both. Its weakness is masking: a quiet harsh voice mixed with loud music in the same band is judged on the mix, so the cut acts on both.
- **Per-stream copy:** the same class can later be instantiated in the Leveler APO if per-app detection proves necessary; the cost of a second copy is a second telemetry slot and a second set of settings.
- **CPU (DERIVED, UNCERTAIN: not measured):** about 18 floating-point operations per band, channel and sample (state-variable section 10, cut 3, detector 5) plus about 10 operations and one `log10` per band and frame. Four bands and two channels at 48 kHz are about 8.8 Mflop/s plus 192 000 `log10` calls per second; ten concurrent streams in SFX would be ten times that. A 1024-point STFT with a hop of 256 is about 400 flops per frame for stereo (`5*N*log2(N)` per forward-plus-inverse pair), about 19 Mflop/s (DERIVED, rough).
- **Latency and `GetLatency`:** an IIR design adds none, so `OnLatencyFrames()` and H12 stay unchanged. The base class computes latency under its critical section from `OnLatencyFrames()` (`native\apo\SpApoBase.cpp` lines 349-357), so a look-ahead or FFT design must keep the value constant between `Prepare` calls (the same rule the cap follows, `SpDsp.h` lines 39-40).
- **Prepare must not fail:** because a throwing `OnPrepare` counts toward the 10-failure auto-disable of the endpoint's effects, the stage should use fixed-size arrays so that its `Prepare` allocates nothing (Key Question 5).
- **Realtek and Sound Research effects before ours:** in both lists our stage runs after theirs, so it measures and cuts what actually reaches the driver, including any bass or treble boost they add. What the Realtek driver or codec does after the APO chain is not visible to us.
- **Safe mode:** `OnProcess` skips the DSP when `safe_` is set (`SpSafetyCapApo.cpp` line 44), so the new stage drops out together with the cap's DSP and only the base class clamp remains.

### Gaps
- Zoom's stream mode is unknown (plan lines 128-132, 266); only the EFX is guaranteed to reach it.
- The list-order semantics of composite keys are not stated on the Microsoft page; our installer relies on the order of the REG_MULTI_SZ.
- I found no 2026 Microsoft statement about changed APO signing or loading rules; the searches returned only kernel-driver signing news of unclear relevance to an APO DLL in `audiodg.exe`.
- CPU numbers are operation counts, not measurements.

---

## Key Question 3: Which filter structure: RBJ biquads, Cytomic/Simper SVF, LR4, complementary or subtractive splits, FFT/STFT?

### Takeaway
Use the trapezoidal (Simper/Cytomic) state-variable filter with fixed poles: only the output mix `c` changes, so the stage is exact at `c = 0`, the gain cannot destabilise the filter, and no coefficient interpolation is needed. RBJ biquads give the same transfer function in this fixed-pole form but are exact only once their state error has decayed; moving-pole bells, LR4 sums and STFT each give up the exact copy.

### Cited Findings
- RBJ cookbook definitions: `A = 10^(dBgain/40)`, `w0 = 2*pi*f0/Fs`, `alpha = sin(w0)/(2*Q)`; band-pass with constant 0 dB peak gain: `b0 = alpha, b1 = 0, b2 = -alpha, a0 = 1 + alpha, a1 = -2*cos(w0), a2 = 1 - alpha`; peaking EQ: `b0 = 1 + alpha*A, b1 = -2*cos(w0), b2 = 1 - alpha*A, a0 = 1 + alpha/A, a1 = -2*cos(w0), a2 = 1 - alpha/A`; allpass: `b0 = 1 - alpha, b1 = -2*cos(w0), b2 = 1 + alpha` with the band-pass `a`; shelves use `2*sqrt(A)*alpha` terms; direct form I, bilinear transform with prewarping — [Audio EQ Cookbook (W3C Note, 8 June 2021)](https://www.w3.org/TR/audio-eq-cookbook/).
- Simper/Cytomic trapezoidal SVF as implemented in Faust: `g = tan(pi*f/fs)` (clamped below Nyquist), `k = 1/Q` for every type except the bell, where `k = 1/(Q*A)`; `A = 10^(G/40)`; `hp = (v0 - (g + k)*ic1 - ic2)/(1 + g*(g + k))`, `v1 = g*hp + ic1`, `v2 = g*v1 + ic2`; output `m0*v0 + m1*v1 + m2*v2` with the mixes below; the shelves scale `g` by `1/sqrt(A)` (low) or `sqrt(A)` (high) — [Faust `filters.lib`, `svf` environment](https://github.com/grame-cncm/faustlibraries) (author Oleg Nesterov, STK-4.3).

| Type | m0 | m1 | m2 |
|---|---|---|---|
| low-pass | 0 | 0 | 1 |
| band-pass | 0 | 1 | 0 |
| high-pass | 1 | -k | -1 |
| notch | 1 | -k | 0 |
| peak | 1 | -k | -2 |
| all-pass | 1 | -2k | 0 |
| bell | 1 | k(A^2 - 1) | 0 |
| low shelf | 1 | k(A - 1) | A^2 - 1 |
| high shelf | A^2 | k(1 - A)A | 1 - A^2 |

- The Faust file notes that solving for the band-pass first (as in Simper's listings) loses damping precision in single precision at low cutoff and high rate: at 20 Hz, Q 10, 192 kHz the error was 8.8e-4 versus 3.7e-6 for the high-pass-first form — [Faust `filters.lib`](https://github.com/grame-cncm/faustlibraries).
- The derivation is Simper's note `SvfLinearTrapOptimised2.pdf` (copyright 2013, allpass added 2016) — [Cytomic](https://cytomic.com/files/dsp/SvfLinearTrapOptimised2.pdf). chowdsp's `StateVariableFilter` (Bell, LowShelf, HighShelf and Crossover types) cites the sibling note `SvfLinearTrapAllOutputs.pdf` — [chowdsp_utils](https://github.com/Chowdhury-DSP/chowdsp_utils).
- Time-varying filters: Wishnick proposes the SVF for parameters that change every sample, gives a proof of time-varying stability for its TDF-II realisation, and notes a class of time-varying techniques from speech coding that is unsuitable for music; transposed direct form II is not guaranteed stable under variation — [DAFx-14 page](https://dafx.de/paper-archive/details/yfd4LbdXr2Cywsqxfby8ug) and [paper PDF](https://dafx.de/paper-archive/2014/dafx14_aaron_wishnick_time_varying_filters_for_.pdf).
- LR4 and the 3-band allpass compensation: see Key Question 1 ([Rane Note 160](https://www.ranecommercial.com/legacy/note160.html), [Faust `crossover3LR4`](https://github.com/grame-cncm/faustlibraries), [LSP `Crossover.h`](https://github.com/lsp-plugins/lsp-dsp-units)). LSP's crossover also offers a bilinear versus matched-transform mode.
- STFT: COLA windows, spectral multiplication as a time-varying filter, denser sampling needed when spectra are modified — [J. O. Smith](https://ccrma.stanford.edu/~jos/sasp/Overlap_Add_OLA_STFT_Processing.html). The page does not discuss latency, Hann windows or specific overlaps.

### Inferences
**The fixed-pole subtractive cut (DESIGN, DERIVED, MODEL-CHECK).**
```
per band, per channel, per sample (states s1, s2 in double):
  hp = (x - (g + k)*s1 - s2) * a          // a = 1/(1 + g*(g + k)), g = tan(pi*f0/fs), k = 1/Q
  v1 = g*hp + s1
  v2 = g*v1 + s2
  s1 = 2*v1 - s1 ;  s2 = 2*v2 - s2       // trapezoidal state update
  bp = k*v1                               // unity gain at f0: the detector input and the part that is subtracted
  y  = x - c*bp                           // c in [0, c_max], c_max < 1 ; c == 0.0 gives y == x exactly
```
- This is Simper's bell mix `m1 = k*(A^2 - 1)` with `A^2 = 1 - c`, but with `k` held at `1/Q` instead of `1/(Q*A)`: only the zeros move, the poles never do. The state-update form `s = 2*v - s` is my own check of the filter: the model reproduced the analytic response at the centre to 9 decimals. I did not verify how the Faust listing writes the update, and the Cytomic PDF text could not be read cleanly (its equations came out garbled), so the equations were taken from the Faust code.
- Why it is safe: `c` does not enter the filter's feedback. The band-pass `bp` is computed from the input alone, so `c(t)` can change at any rate without making the filter unstable, and zipper noise can only come from `c(t)` itself, which is smoothed by the project's dB-domain one-pole.
- **Exact copy.** At `c == 0.0` the output is `x` regardless of the filter state (IEEE: `x - 0.0*bp == x` for finite `bp`), which relies on `/fp:precise` (`SpDsp.h` line 13). MODEL-CHECK: 30000 of 30000 float32 samples identical right after a hot cut history. The same test with an RBJ peaking filter in direct form I and gain returned to 0 dB: 42 of 30000 samples were not identical (the state error decays within tens of samples, then the output is exact again).
- **RBJ peaking is also `x + (A^2 - 1)*bandpass` (DERIVED).** `H - 1 = alpha*(A - 1/A)*(1 - z^-2)/(a0 + a1*z^-1 + a2*z^-2)`, and `alpha*(A - 1/A) = (A^2 - 1)*(alpha/A)`, so the peaking EQ is `x` plus `(A^2 - 1)` times the unity-peak RBJ band-pass with `alpha/A`. Its poles move with the gain. With the RBJ band-pass at fixed `alpha`, `y = x - c*BPF(x)` is the same transfer function as the SVF form (both are bilinear transforms of the same analogue prototype) and is just as safe; the SVF's advantages are shared code with shelves and LR4, numerics at low `f0/fs`, and state continuity if a pole ever has to move (for example a band that follows a formant).
- **Cut shape (MODEL-CHECK, dB at multiples of the centre frequency; centre 4 kHz at 48 kHz, and a 3 kHz centre differs by up to 0.2 dB (recomputed from the closed form); `c` = 0.75 so the centre is -12 dB):**

| Q | 0.25x | 0.5x | 0.7x | 1x | 1.4x | 2x | 4x |
|---|---|---|---|---|---|---|---|
| 0.7 | -0.5 | -2.5 | -5.7 | -12.0 | -5.9 | -2.2 | -0.2 |
| 1.0 | -0.3 | -1.4 | -4.0 | -12.0 | -4.1 | -1.2 | -0.1 |
| 2.0 | -0.1 | -0.4 | -1.5 | -12.0 | -1.5 | -0.3 | -0.0 |

- **Shelf cuts with fixed poles (DERIVED, MODEL-CHECK).** With the shelf mixes of the table and `g` left fixed, the low shelf is `(s^2 + k*A*s + A^2)/(s^2 + k*s + 1)` and the high shelf `(A^2*s^2 + k*A*s + 1)/(s^2 + k*s + 1)`. The numerator-minus-denominator magnitude is `(1 - A^2)*(w^2*(2 - k^2) - (1 + A^2))` (low) and `w^2*(1 - A^2)*(2 - k^2 - w^2*(1 + A^2))` (high), so both are pure cuts only when `k^2 >= 2`, that is Q at most 1/sqrt 2. MODEL-CHECK at Q = 1/sqrt 2: max `abs(H)` = 1.000000 for `A^2` in {0.5, 0.25, 0.1}, with `1 - c` at 20 Hz (low shelf) or 23.9 kHz (high shelf). A plain complement `x - 0.9*LP2` (Q 0.707) is not a pure cut: max `abs(H)` = 1.2378 (+1.85 dB).
- **Parallel versus cascade.** A cascade is always a pure cut; a parallel sum needs `sum(c_i) <= 2` (proof in Key Question 1). The cascade also lets a later band detect the signal after the earlier band's cut, which matters for leakage (Key Question 4).
- **LR4 (MODEL-CHECK, DERIVED).** `LP4 + HP4` is a 2nd-order allpass, so a 3-band split sums flat only with the allpass on the low band. With crossovers at 500 and 3000 Hz: with the allpass, the summed magnitude is 1.000000000 at every grid frequency; without it, the minimum is 0.9646 (-0.31 dB). Even then the sum is not the input: in that test, for uniform noise, the maximum of `abs(sum - input)` was 2.007 (input rms 0.580). In the limiter model of Key Question 1 (crossovers 800 and 3000 Hz, all band gains 1, white noise of peak 0.5) the maximum difference was 1.09 and 0 of 30000 samples were identical. A subtractive split (`y = lp + (x - lp)` in double, then cast to float32) was exact (0 mismatches in 200000 samples) but, as shown in Key Question 1, its complementary band is 1.5x at the crossover.
- **STFT/OLA (INFERRED, UNCERTAIN).** With a COLA window and no modification the overlap-add returns the input up to rounding, and latency is at least one window. Per-frame gains cut musically only with smoothing over time and across bins (JOS: spectral modification needs denser sampling).
- **Speech artefacts and zipper noise.** The fixed-pole SVF stage has no coefficient motion. The detector's squared band-pass carries a ripple at twice the band frequency that modulates `c`; with time constant `tau` the ripple on the power reading is about `1/(4*pi*f0*tau)` (DERIVED), which is +-0.023 dB at 3 kHz with 5 ms (MODEL-CHECK: the settled tone was 0.019 dB under the ceiling) and +-0.09 dB if `tau = 4/f0` for a low band.
- **Operation counts (DERIVED, UNCERTAIN: not measured).** Band section, cut and detector: about 18 flops per sample per channel; an LR4 3-band split: 9 sections per channel, about 90 flops plus detectors; STFT: see Key Question 2.

### Gaps
- The Cytomic PDF equations were not read directly (garbled text extraction); the SVF equations come from Faust.
- No STFT implementation was modelled.
- The Wishnick paper's detailed equations were only skimmed; its proof concerns the TDF-II SVF, not the TPT SVF used here, although both are fixed-pole in our design anyway.

---

## Key Question 4: Detector and gain computer: RMS or peak per band, speech ballistics, look-ahead, stereo link, absolute ceilings or thresholds relative to the whole spectrum, max cut, smoothing, gain at most 1

### Takeaway
Per band: a band-pass detector on the signal, a mean-square level with a time constant of at least four periods of the band, a hard (or soft-knee) gain computer against an absolute ceiling, the project's dB-domain one-pole attack and release, one gain for all channels driven by the loudest channel, and `c = 1 - 10^(-GR/20)` with a maximum cut. The ceiling is exact only at the band centre, neighbouring bands must be kept from tripping each other (cascade order or a narrower detector), and not using look-ahead is acceptable because the Safety Cap catches peaks.

### Cited Findings
- Dynamics maths, from the compressor literature as restated by MathWorks (citing Giannoulis, Massberg and Reiss, JAES 60(6), 399-408, 2012): level `x_dB = 20*log10(abs(x))`; hard knee `x_sc = x_dB` below the threshold `T`, `T + (x_dB - T)/R` above; soft knee of width `W`: `x_dB` below `T - W/2`, `x_dB + (1/R - 1)*(x_dB - T + W/2)^2/(2*W)` for `abs(x_dB - T) <= W/2`, `T + (x_dB - T)/R` above `T + W/2`; computed gain `g_c = x_sc - x_dB`; smoothing `g_s[n] = alpha_A*g_s[n-1] + (1 - alpha_A)*g_c[n]` when `g_c <= g_s[n-1]` (more reduction), otherwise with `alpha_R`; `alpha = exp(-ln(9)/(Fs*T))` where attack and release times are defined as 10%-90% times; `g_lin = 10^(g/20)` — [MathWorks `compressor`](https://www.mathworks.com/help/audio/ref/compressor.html); the paper is listed at [AES e-library](https://aes2.org/publications/elibrary-page/?id=16354).
- De-essing practice: band-pass side-chain, the dynamic-EQ form, rise and fall times under 10 ms, sibilance ranges — [Wikipedia: De-essing](https://en.wikipedia.org/wiki/De-essing) (rough, see Key Question 1).
- Our existing gain pattern (`native\dsp\SpDsp.h` lines 369-381): the target is clamped to [0, max]; `grDb_ += (target - grDb_) * (target > grDb_ ? attackCoef_ : releaseCoef_)`; `if (target == 0.0 && grDb_ < 1e-7) grDb_ = 0.0` ("exactly unity once recovered"); the linear gain `min(1.0, DbToLinD(-grDb_))` is recomputed only when `grDb_` changed. The coefficient is `OnePoleCoef(ms, fs) = 1 - exp(-1/(ms*0.001*fs))` (line 117), a time constant, not a 10%-90% time. Disabled means a linear fall of GR (line 375), and defaults are attack 100 ms, release 1000 ms, max reduction 40 dB (lines 311-317).
- The Leveler weights channels (LFE 0, surrounds 1.41) via `ChannelWeightsFromMask` — `native\apo\SpLevelerApo.cpp` lines 24-28; the Safety Cap APO receives the channel mask but ignores it — `native\apo\SpSafetyCapApo.cpp` line 25.
- The Safety Cap uses one gain for all channels — `native\dsp\SpDsp.h` line 4.

### Inferences
- **Detector (DESIGN).** Feed-forward, on the band-pass `bp` that the cut already computes (one filter serves both). Mean-square (RMS) rather than peak: it is smooth, and peaks are the Safety Cap's job. One-pole `p += cd*(bp^2 - p)` with `tau = max(5 ms, 4/f0)`; level `L = 10*log10(2*p)` (full-scale sine = 0 dB). The `4/f0` rule keeps the ripple on the reading near +-0.09 dB for low bands (DERIVED, Key Question 3). A peak or fast-RMS second reading could be added for transients, but nothing here needs it.
- **Gain computer (DESIGN).** `target = clamp(L - ceiling, 0, maxCut)` (ratio infinity, hard knee), optionally the soft-knee form above with `GR = (L - T)*(1 - 1/R)` for a finite ratio. Smoothing: the Leveler's dB-domain one-pole, attack and release time constants, snap to exactly 0 when the target is 0 and `GR < snapDb`. Convert with `c = 1 - 10^(-GR/20)`, recomputed only when `GR` changes. `maxCut` gives `c_max = 1 - 10^(-maxCut/20)`, below 1.
- **Unit warning for cross-researchers.** Our `attackMs`/`releaseMs` are time constants (63%); published attack and release times (Giannoulis, MathWorks) are 10%-90% times, a factor `ln 9 = 2.197` longer. A 3 ms constant here is a 6.6 ms 10%-90% time.
- **Ballistics values.** I found no citable typical values for speech-tuned attack and release (hearing-aid wide-dynamic-range compressors would be the source; see Gaps) beyond the de-esser's "under 10 ms". The model used a 5 ms detector, 3 ms attack and 100 ms release purely as a test configuration; the per-person tuning researcher owns real values. MODEL-CHECK on that configuration: a 14 dB step reaches within 1 dB of the ceiling about 13 ms after it starts; recovery to exactly 0 takes 963 ms with `snapDb = 0.001` and 1884 ms with 1e-7 (release 100 ms). A larger snap trades a longer-tailed one-pole for a tiny final step (`c` about 1.2e-4 at 0.001 dB, a change of the band component of about -79 dB).
- **Look-ahead: none.** The first milliseconds of a sudden loud band pass at the pre-cut level, but the Safety Cap's 5 ms look-ahead already bounds the true peak, and this stage is a comfort control. Look-ahead would add latency and change `GetLatency`/H12.
- **Stereo link (DESIGN).** Use the weighted power of the loudest channel for the detector (`peakP = max_c(w_c*bp_c^2)`) and apply the same `c` to every channel, so the image does not shift; a hard-panned loud voice then still triggers, which is the protective choice for a headphone ear. A summed or mean power is the alternative (it reads 3 dB lower for a single-channel source). Weights come from the channel mask like the Leveler's (LFE 0).
- **Absolute versus relative thresholds.** Absolute ceilings (dB re full scale, before the volume slider) match the project's pre-slider philosophy and give a stable bound that does not depend on other content. A relative threshold (`L_band - L_wideband > R`) reshapes the tonal balance and would also cut quiet but spectrally peaky content, which may be what "too hard even when low" needs; the combined rule `GR = max(GR_absolute, GR_relative)` keeps GR at least 0 and so keeps the gain at most 1. Which to use is for the tuning researcher; the code only has to carry both parameters.
- **Guarantee gain at most 1 (DERIVED).** `c` in [0, c_max], c_max below 1, cascade form: every section is a pure cut at every frequency (Key Question 1). Energy: for constant `c`, `||y||^2 = ||x||^2 - c*(2 - c)*||bp||^2` (because `<x, bp> = ||bp||^2` for the unity-peak band-pass), so energy cannot rise; with time-varying `c` a small modulation term appears (UNCERTAIN: not bounded here; use a small tolerance in tests). Sample peaks can rise (Key Question 2); the cap follows.
- **Ceilings are soft (MODEL-CHECK).**
  - Tone at the centre: -6 dBFS in, ceiling -20: out -20.019 dB, GR 14.019 dB.
  - Off-centre tones through one Q 1.4 band at 3 kHz, with the same ceiling:

    | f / f0 | 0.5 | 0.7 | 0.85 | 1 | 1.18 | 1.41 | 2 |
    |---|---|---|---|---|---|---|---|
    | over ceiling (dB) | +13.3 | +11.5 | +7.4 | -0.0 | +7.6 | +11.4 | +13.4 |
    | GR (dB) | 6.6 | 10.8 | 13.2 | 14.0 | 13.1 | 10.9 | 6.2 |

    The model and the closed-form response agreed to 0.004 dB. The cause is structural: the detector reading falls with distance from the centre and so does the cut, so the pair cannot hold a ceiling off-centre, and even `c = 1` is only about -3 dB at `x = 1` (DERIVED).
  - A speech-like signal (F0 220 Hz, formant at 2.9 kHz) at -22 dBFS rms with the 3 kHz ceiling 8 dB under its own reading: GR 8.55 dB; the 3 kHz band level fell from -24.18 to -30.05 dB against a ceiling of -32.24 (2.2 dB over, 73% of the demanded 8 dB); harmonics in 2.4-3.6 kHz were cut 4.2 to 8.4 dB, in 1.5-2.4 kHz 0.8 to 2.8 dB, in 3.6-5 kHz 1.4 to 4.1 dB, below 1.5 kHz at most 0.5 dB, above 5 kHz at most 1.2 dB. An octave-spaced Q 1.4 pair left +8.9 dB at the midpoint. Practical consequence: more and wider-overlapping bands, or lower Q, reduce the off-centre excess; the per-person tuning researcher picks the grid.
- **Leakage between bands (MODEL-CHECK).** A loud 3 kHz tone (-6 dBFS) with quiet tones (-40 dBFS) at the other band centres and all ceilings at -20 dB:

| Structure | GR per band (250 / 800 / 3000 / 6000) | Level change of the quiet 250 / 800 / 6000 Hz tones |
|---|---|---|
| Parallel, detectors on the input | 0 / 0.02 / 14.02 / 6.23 | -0.01 / -0.15 / -6.67 dB |
| Cascade low-to-high, same detector Q | 0 / 0.02 / 14.02 / 0 | -0.02 / -0.19 / -0.75 dB |
| Cascade, detector Q twice the cut Q | 0 / 0 / 14.02 / 0 | -0.01 / -0.17 / -0.75 dB |
| Skirt of the 3 kHz cut alone (analytic) | | -0.01 / -0.17 / -0.75 dB |

  - Parallel detectors let the 3 kHz tone trip the 6 kHz band and cut a quiet 6 kHz tone by 6.7 dB. In a cascade the later band sees the already-cut signal and only the unavoidable skirt remains. Bands earlier in the cascade than the loud band still see its leakage; a narrower detector (a separate section at twice the Q, costing one more SVF per band) gives about 6 dB more rejection (DERIVED: at 3.75x the centre the rejection of a 2nd-order section is 14 dB at Q 1.4 and about 20 dB at Q 2.8).
  - A "dominant band" rule (ignore a band's reading when a louder neighbour explains it through the analytic leakage) is a possible further fix. UNCERTAIN: not modelled.
- **Max cut.** 0 to 30 dB range is enough; `c_max` is below 1 so that a band is never a full notch.

### Gaps
- The JAES 2012 paper could not be read (the QMUL copy failed TLS verification, and the fallback returned a web page, not a PDF); its detector recommendations (smooth decoupled peak detector, log-domain smoothing) are from memory and not used.
- No typical hearing-aid attack/release values or ANSI S3.22 definitions were found; none were available from the Tympan or openMHA sources I read.
- A dedicated dynamic-EQ paper was not found; one self-published PDF ("FreeEQ8") claims DAFx26 but is unverified.
- All numbers marked MODEL-CHECK use synthetic signals; no real voice material, no listening tests.

---

## Key Question 5: Which SpDsp.h real-time rules apply, and what would the code look like?

### Takeaway
The stage fits every rule if it is a header-only class with fixed-size state arrays (so `Prepare` allocates nothing and cannot cause an `E_OUTOFMEMORY` lock failure), sanitises its input before the filters, and mirrors the Leveler's `Sanitise`/`Same`/`UpdateCoefficients` and fail-closed patterns. Coefficients (`tan`, `exp`) are computed only in `SetParams`/`Prepare`; the per-sample path has no transcendental calls, and the per-frame gain step reuses the Leveler's "recompute only when changed" pattern.

### Cited Findings
- The rule list (`native\dsp\SpDsp.h` lines 6-25): gains at most 1; finite output; no allocation, free, lock, wait or OS call in `Process`, `Add` and `SetParams`; only `Prepare` allocates and may throw `std::bad_alloc`, which leaves the object unprepared; one thread per object; `/fp:precise` with finiteness tested by its own bit test (`detail::IsFinite`, lines 71-75); in == out allowed, partial overlap not; an unprepared object fails closed: NaN and Inf become 0 and every other sample is clamped without gain or delay (to +-1.0 for the Leveler); `Process` writes nothing if `Prepare` was never called or the last call had 0 or more than 65535 channels (lines 16-25).
- Limits: rates 8000-768000, 1-64 channels (`SpDsp.h` lines 63-65, `FormatOk` 87-89); `FailClosedChannels` (93-95); `MsToFrames` (111), `OnePoleCoef` (117).
- The patterns to mirror: `Leveler::Prepare` records the fail-closed channel count first (lines 323-336); `SetParams` = `Sanitise` + `Same` + `UpdateCoefficients` (347-352, 404-424); `Process` reads the whole frame before writing (364-388); `FailClosed` reads before writing, so in == out is safe (396-402); the final write is `IsFinite(x) ? ClampF(x*g, -1, 1) : 0` (line 385).
- `SpApoBase` runs `FtzDazScope` (flush-to-zero and denormals-are-zero) around the DSP (`native\apo\SpApoBase.cpp` line 386); `OnSettings` is called from `SyncSettings` on the audio thread when the settings sequence changed (lines 363-371); `OnSettings` and `OnPrepare` exceptions turn into `E_OUTOFMEMORY` (lines 303-316).
- Microsoft's APO rules (no blocking, no paged memory) and failure count: see Key Question 2 ([Implementing Audio Processing Objects](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects)).
- The build flags are `/std:c++17 /EHsc /W4 /WX /O2 /fp:precise /MT` (`native\build-apo.cmd` lines 36 and 44, `native\build-dsp.cmd` line 22); the plumbing can be built against a stub DSP selected in `native\apo\SpDspSelect.h` (`native\apo\SpDspStub.h`), so any new DSP class also needs a stub body.
- The channel limit differs by layer: the APO layer accepts at most 32 channels (`kMaxChannels = 32`, `native\apo\SpApoBase.h` line 33, used by the format check at `native\apo\SpApoBase.cpp` line 44), while the DSP accepts 64 (`native\dsp\SpDsp.h` line 65); `native\apo\SpLevelerApo.cpp` line 25 sizes its weights array with the APO's 32. The sketch below keeps its per-channel state at the DSP's 64 and sizes the wiring's weights array at the APO's 32.

### Inferences
**How each rule is met (DESIGN).**

| Rule | How |
|---|---|
| No allocation in `Process`/`SetParams` | Fixed arrays `s1_[kMaxBands][64]`, `s2_[kMaxBands][64]` (about 4 KB per instance), no `std::vector` |
| `Prepare` cannot throw | It allocates nothing: it checks the format, copies channel weights, computes coefficients. A stage that cannot throw cannot add to the 10-failure auto-disable |
| Finite output | Input sanitised first (`Sanitize`); the final write is `IsFinite ? ClampF : 0`; states reset if they ever stop being finite |
| Not poisoned by one bad sample | The filters see only sanitised input, so a NaN cannot enter a state (a NaN in an SVF state would never leave) |
| Gain at most 1 | `c` in [0, c_max), cascade form (Key Question 1); the cut can raise a sample peak, so the Safety Cap must follow it (Key Question 2) |
| Fail closed when unprepared | Same as the Leveler: NaN and Inf to 0, clamp to +-1.0, no gain, writes nothing when `Prepare` was never called |
| In-place | Read the whole frame into a local array before writing any output sample |
| 8-768 kHz, 1-64 channels | `FormatOk`; a band is active only for `20 Hz <= f0 <= 0.45*fs`, otherwise `c` stays 0 (so at 8 kHz a 6 kHz band is simply off) |
| Control-rate coefficient updates | `UpdateCoefficients()` runs only when `SetParams` changes something (`tan`, `exp`); nothing else in `Process` except the per-frame `log10` and the recompute-when-changed `exp` |
| Chunk-size independence | No frame counters tied to call boundaries; every update happens per frame |
| Denormals | Follow the project idiom: `LoudnessMeter::FinishBlock` flushes with `if (std::fabs(s) < 1e-200) s = 0.0;` once per block (`SpDsp.h` lines 278-280). The DLL adds flush-to-zero and denormals-are-zero through `FtzDazScope` (`SpApoBase.cpp` line 386; class at `SpApoBase.h` lines 102-107). The DSP tests set no FTZ/DAZ (none in `DspTests.cpp` or `build-dsp.cmd`), so the hand flush is the only protection there. Once per block versus once per frame is UNCERTAIN |
| IEEE | `/fp:precise`; the exactness `x - 0.0*bp == x` is an IEEE fact, no fast-math |
| Output bound when it sits before the cap | Clamp to the base class's input bound (+-1000, `SpApoBase.h` line 39), not +-1.0: a hard clip at 1.0 ahead of the limiter would change what the cap sees and defeats its look-ahead |
| Removing a band or disabling the stage | Ramp `GR` to 0 through the normal release (as T11/T20 do for the Leveler and the cap); only then drop the band, zeroing its state |

**Sketch (DESIGN; not compiled; names follow `LevelerParams` and `Leveler`).**
```cpp
// Goes into native\dsp\SpDsp.h between Leveler and SafetyCap.
struct BandGuardBand {
    float centreHz  = 3000.0f;   // active only for 20 <= centreHz <= 0.45 * rate
    float q         = 1.4f;      // 0.3 .. 8   (pole damping k = 1/q)
    float ceilingDb = -20.0f;    // absolute, dB re full-scale sine peak, -100 .. 0
    float maxCutDb  = 18.0f;     // 0 .. 30
};
struct BandGuardParams {
    bool          enabled = false;   // no change to the sound until switched on
    std::uint32_t bands = 0;         // 0 .. BandGuard::kMaxBands
    BandGuardBand band[4];
    float detectorMs = 5.0f;         // lower bound; real constant = max(detectorMs, 4000 / centreHz)
    float attackMs = 3.0f, releaseMs = 100.0f;
    float snapDb = 0.001f;           // below this the gain reduction snaps to exactly 0
};

class BandGuard {
public:
    static constexpr std::uint32_t kMaxBands = 4;
    static constexpr float kOutLimit = 1000.0f;   // same bound SpApoBase.h:39 puts on input

    // Allocates nothing, so it cannot throw: LockForProcess cannot fail on it (SpApoBase.cpp:303-316).
    void Prepare(std::uint32_t rate, std::uint32_t channels, const float* weights) {
        ready_ = false;
        failClosedChannels_ = detail::FailClosedChannels(channels);   // first of all, as Leveler::Prepare does
        if (!detail::FormatOk(rate, channels)) return;
        fs_ = rate; channels_ = channels;
        for (std::uint32_t c = 0; c < channels; ++c) w_[c] = weights ? weights[c] : 1.0f;
        UpdateCoefficients();
        ready_ = true;
        Reset();                                                      // zero all states and gain reductions
    }
    void SetParams(const BandGuardParams& p);                         // Sanitise -> Same -> UpdateCoefficients
    void Process(const float* in, float* out, std::uint32_t frames);  // see below
    std::uint32_t LatencyFrames() const { return 0; }
    float GainReductionDb(std::uint32_t band) const { return static_cast<float>(gr_[band]); }
    // ... Reset(), private Sanitise/Same/UpdateCoefficients/FailClosed as in Leveler ...
private:
    double s1_[kMaxBands][detail::kMaxChannels] = {}, s2_[kMaxBands][detail::kMaxChannels] = {};
    double p_[kMaxBands] = {}, gr_[kMaxBands] = {}, c_[kMaxBands] = {}, grFor_[kMaxBands] = {};
    float  w_[detail::kMaxChannels] = {};
    // per band: g, k, gk = g + k, a = 1/(1 + g*gk), detector coefficient, active flag, ceiling, maxCut
};

void BandGuard::Process(const float* in, float* out, std::uint32_t frames) {
    if (in == nullptr || out == nullptr) return;
    if (!ready_) { FailClosed(in, out, frames); return; }
    const std::size_t ch = channels_;
    for (std::uint32_t f = 0; f < frames; ++f) {
        const float* src = in + f * ch;  float* dst = out + f * ch;
        double y[detail::kMaxChannels], bp[detail::kMaxChannels];
        for (std::size_t c = 0; c < ch; ++c) y[c] = detail::Sanitize(src[c]);   // whole frame read before any write
        for (std::uint32_t b = 0; b < nBands_; ++b) {
            if (!band_[b].active) continue;
            double peakP = 0.0;
            for (std::size_t c = 0; c < ch; ++c) {                              // cascade: y is the running signal
                const double hp = (y[c] - band_[b].gk * s1_[b][c] - s2_[b][c]) * band_[b].a;
                const double v1 = band_[b].g * hp + s1_[b][c];
                const double v2 = band_[b].g * v1 + s2_[b][c];
                s1_[b][c] = 2.0 * v1 - s1_[b][c];  s2_[b][c] = 2.0 * v2 - s2_[b][c];
                bp[c] = band_[b].k * v1;
                peakP = std::max(peakP, w_[c] * bp[c] * bp[c]);                 // link: loudest channel
            }
            p_[b] += band_[b].cd * (peakP - p_[b]);
            const double L = p_[b] > 1e-30 ? 10.0 * std::log10(2.0 * p_[b]) : -300.0;
            double target = std::min(std::max(L - band_[b].ceilingDb, 0.0), band_[b].maxCutDb);
            if (!params_.enabled) target = 0.0;
            gr_[b] += (target - gr_[b]) * (target > gr_[b] ? attackCoef_ : releaseCoef_);
            if (target == 0.0 && gr_[b] < params_.snapDb) gr_[b] = 0.0;         // exactly idle once recovered
            if (gr_[b] != grFor_[b]) { c_[b] = 1.0 - detail::DbToLinD(-gr_[b]); grFor_[b] = gr_[b]; }
            if (c_[b] != 0.0) for (std::size_t c = 0; c < ch; ++c) y[c] -= c_[b] * bp[c];
        }
        for (std::size_t c = 0; c < ch; ++c) {
            const float v = static_cast<float>(y[c]);
            dst[c] = detail::IsFinite(v) ? detail::ClampF(v, -kOutLimit, kOutLimit) : 0.0f;
        }
    }
}
```
- Details the sketch leaves out: hand-flushing denormals in `s1_`, `s2_`, `p_`; resetting a state that is not finite; skipping the arithmetic of a band only when it is inactive (never when `GR` is 0, otherwise its states go stale and a re-enabled band starts with a transient); keeping a band active until its `GR` reaches exactly 0 when the band count or enable flag changes; `FailClosed` identical to the Leveler's (NaN/Inf to 0, clamp +-1.0).

**Wiring (DESIGN, `native\apo\SpSafetyCapApo.cpp`).**
```cpp
void OnPrepare(uint32_t rate, uint32_t channels, uint32_t channelMask) override {
    float weights[kMaxChannels];
    ChannelWeightsFromMask(channelMask, channels, weights);   // as SpLevelerApo.cpp:25-26
    guard_.Prepare(rate, channels, weights);                  // cannot throw
    cap_.Prepare(rate, channels);                             // unchanged; may throw bad_alloc
}
void OnSettings(const Settings& s) override { /* unchanged */ if (!safe_) guard_.SetParams(GuardParamsFrom(s)); }
void OnReset() override { if (!safe_) { guard_.Reset(); cap_.Reset(); } }
void OnProcess(float* buf, uint32_t frames) override { if (!safe_) { guard_.Process(buf, buf, frames); cap_.Process(buf, buf, frames); } }
uint32_t OnLatencyFrames() const override { return safe_ ? 0u : cap_.LatencyFrames(); }   // unchanged: the guard adds 0
```
- The stub header gets a `BandGuard` with the same public members and trivial bodies. The existing `SafetyCap` and `Leveler` stay untouched, so their tests T01-T27 stay valid.

### Gaps
- The sketch is not compiled; control flow, naming and clamps need review against the real header.
- Where to flush denormals is open: once per block as `FinishBlock` does, or once per frame; not measured.
- The build treats warnings as errors (`/W4 /WX`), so the sketch needs explicit `static_cast`s for `double`-to-`float` and for signed/unsigned counters, and no unused variables; it was not compiled.
- Whether a fixed `kMaxBands = 4` is enough depends on tuning (Key Question 4); the cost of more is linear.

---

## Key Question 6: Settings and telemetry extension (config.bin v2 `SpConfig`, per-band gain-reduction telemetry, mirrored in C# and the installer; design only)

### Takeaway
Keep `config.bin` v1 byte-for-byte and add a separate `bands.bin` (own magic, version, sequence and FNV-1a, absent file = stage off), published to the audio thread through the same sequence lock; report per-band gain reduction in the 8 reserved bytes (56..63) of the Safety Cap telemetry slot, which the existing C# and installer readers already ignore. A `config.bin` v2 is possible but forces plug-in-before-tray ordering and touches every golden vector.

### Cited Findings
- Config contract: 32 bytes, little-endian: magic "SPCF", version 1, sequence, enabled, capDb, marginDb, `reserved` at offset 24, FNV-1a 32-bit checksum over bytes 0..27; two golden byte strings are in the comment — `native\apo\SpShared.h` lines 44-62.
- The plug-in reads `config.bin` only if it is exactly 32 bytes, with the right magic and version, a matching checksum and finite floats; it does not look at `reserved` — `native\apo\SpRuntime.cpp` lines 75-90.
- The C# side: `ProtectionConfigFile.Size = 32` (line 15), `Version = 1` (line 25), checksummed bytes 28 (line 26), `FromBytes` rejects any other size, version or checksum (lines 72-77), and the writer leaves bytes 24-27 at 0 — `src\SoundProtection.Core\ProtectionConfig.cs`.
- Settings reach the audio path through a sequence lock of atomics: `setSeq_`, `setCap_`, `setMargin_`, `setFlags_` — `native\apo\SpRuntime.h` lines 204-208; writer `PublishSettingsLocked` and reader `ReadSettings` — `native\apo\SpRuntime.cpp` lines 346-374; the `Settings` struct — `native\apo\SpRuntime.h` lines 45-53; the audio thread compares the sequence once per block (`SyncSettings`, `native\apo\SpApoBase.cpp` lines 363-371). The worker ticks every 50 ms (`kWorkerTickMs = 50`, `native\apo\SpRuntime.cpp` line 19; the heartbeat comment at `native\apo\SpShared.h` line 81 says the same), and the config is checked on every 4th tick, 200 ms (`kConfigEveryNTicks = 4`, line 20; the call is at line 267).
- Telemetry: 2112 bytes, header 64 + 32 slots of 64 bytes, `kTelemetryVersion = 1`; the slot has 8 reserved bytes at 56..63 — `native\apo\SpShared.h` lines 64-110. The writer fills the slot fields from `SlotRt` atomics each tick — `native\apo\SpRuntime.cpp` lines 501-561 (header version and slot size at lines 546-549); `SlotRt` — `native\apo\SpRuntime.h` lines 64-82.
- C# reader: `ApoTelemetry.Parse` requires `Version == 1` (lines 75-85), skips slot kinds other than Leveler and SafetyCap (lines 103-106), and `SlotKind` has Free=0, Leveler=1, SafetyCap=2 (line 5) — `src\SoundProtection.Core\ApoTelemetry.cs`; `ProtectionEngine.cs` consumes only the Leveler and SafetyCap slots.
- Installer (`installer\SoundProtection.Install.ps1`): `ConvertFrom-SpTelemetryBytes` (line 428) requires header version 1 and slot size 64 (read at lines 433 and 435, checked at line 436); `Get-SpVerifyMissing` (lines 460-481) requires one Kind 1 and one Kind 2 slot with `Frames > 0` (lines 476-479); the golden config bytes are in `Get-SpDefaultConfigBytes` (lines 378-382); the list constants are at lines 77-78. `installer\Install.Tests.ps1` mirrors them: `$Golden1` (lines 68-70), test U1 (line 1263), test U3 (line 1314).
- Data folder: `C:\ProgramData\SoundProtection\` is created by the install and its permissions are set (`docs\03\INSTALL-REPORT.md` line 45). `Set-SpDataGrants` (lines 983-996) removes inheritance and grants `(OI)(CI)` full control to SYSTEM and Administrators, modify to Users (`S-1-5-32-545`) and read-and-execute to `S-1-5-19`, `S-1-15-2-1` and `S-1-15-2-2`; only the `telemetry` subfolder gives modify to those last three. The build plan gives them as read access for audiodg (`docs\02\WIN11-BUILD-PLAN.md` line 154). Test U6 (`installer\Install.Tests.ps1` lines 1400-1439) checks these grants.
- Tray write path: the tray writes `config.bin.tmp` and moves it over `config.bin`, trying up to 10 times, 50 ms apart, and never creates the folder (`src\SoundProtection.Core\ProtectionConfig.cs` lines 103-135); the plug-in opens the file with shared access (`kShareAll`, `native\apo\SpRuntime.cpp` line 76); `config.bin` stays after Uninstall as the user's (`installer\Install.Tests.ps1` lines 1142-1148).
- Existing tests that pin the contract: `tests\SoundProtection.Tests\ProtectionConfigTests.cs`, `ApoTelemetryTests.cs`, `SettingsTests.cs`.

### Inferences
**Option A, recommended (DESIGN): leave `config.bin` v1 alone, add `bands.bin`.**
```
bands.bin, 128 bytes, little-endian
   0  uint32  magic     "SPBD" = 0x44425053
   4  uint32  version   1
   8  uint32  sequence  +1 on every write
  12  uint32  flags     bit 0 = stage enabled
  16  uint32  bandCount 0..4
  20  float   attackMs
  24  float   releaseMs
  28  float   detectorMs
  32  4 x { float centreHz; float q; float ceilingDb; float maxCutDb }   (32..95)
  96  float   snapDb
 100  uint8   reserved[24]                                               (100..123)
 124  uint32  checksum  FNV-1a over bytes 0..123
```
- Reader rules: exact size 128, magic, version, checksum, every float finite and clamped to the same ranges as `Sanitise`; any failure or no file = stage off (the current behaviour), logged once. Unknown flag bits ignored.
- Publication: extend the sequence lock with an array of atomics for the band parameters, written under the same odd/even protocol (`PublishSettingsLocked`), and read in `ReadSettings` into `Settings` (new fields). The audio thread keeps comparing one sequence number per block.
- A new file leaves old plug-ins (which never look at it) and old trays (which never write it) unaffected, so deployment order does not matter.

**Option B (v2 of `config.bin`).** One file, atomic with the cap and margin. The plug-in's `ReadConfigFile` would have to accept both 32-byte v1 and the larger v2; the tray's `FromBytes` and writer, the installer's golden vectors and `Install.Tests.ps1` all change. An old plug-in rejects a v2 file and falls back to defaults, so the plug-in must be installed before the tray writes v2. Choose it only if the cap and the band settings must change together atomically.

**Option C (smallest first step).** Use bit 0 of the unchecked `reserved` word as the enable switch and compile the band table in. The checksum covers bytes 0..27 including `reserved`, so both ends can carry it. The tray's writer would have to stop writing the 0 at lines 63-64 of `ProtectionConfig.cs`. No per-band tuning without a rebuild.

**Telemetry (DESIGN).** Rename the 8 reserved bytes at 56..63 of the Safety Cap slot to `int16_t bandGrCentiDb[4]` (current gain reduction per band in 0.01 dB, 0 to 32767). Old readers ignore the bytes; old files carry zeros. Keep the telemetry header version at 1: the C# parser and installer reject any other value. In the plug-in: `SlotRt` gets four atomics for the values, `OnTelemetry` stores `guard_.GainReductionDb(b)`, `WriteTelemetry` converts to centi-dB. A separate slot kind (3) is only needed if the stage ever becomes its own APO class: unknown kinds are skipped by C# and ignored by the installer, but a new kind would still cost one of the 32 slots per locked instance.

**Mirror list for the contract change (DESIGN).**
1. `native\apo\SpShared.h`: bands-file constants, the renamed slot field (keep the 64-byte `static_assert`), a comment with two golden byte strings as for `SpConfig`.
2. `native\apo\SpRuntime.h` and `.cpp`: `Settings`, the sequence-lock atomics, `ReadBandsFile` beside `ReadConfigFile`, the 200 ms watcher (`CheckConfig`, lines 389-452) watching the second file, `PublishSettingsLocked`/`ReadSettings`, `SlotRt`, `WriteTelemetry`.
3. `native\apo\SpSafetyCapApo.cpp`: `GuardParamsFrom(Settings)`, telemetry store.
4. `src\SoundProtection.Core\`: a bands-file writer with the same atomic write and FNV-1a as `ProtectionConfig.cs`; `ApoTelemetry.cs` reads the four values (version unchanged).
5. `installer\SoundProtection.Install.ps1`: nothing is required for a tray-written `bands.bin`. It inherits the folder grants (`Set-SpDataGrants`, lines 983-996; Users have modify, so the tray can create it) and Uninstall leaves it where it leaves `config.bin` (`installer\Install.Tests.ps1` lines 1142-1148). If the installer should instead seed a default `bands.bin` (stage off), the exact-listing assertions that expect only `config.bin` and `telemetry` change (`Install.Tests.ps1` lines 1142, 1145, 1148, 1759 and 1773) and golden bytes are added with a mirrored test. `ConvertFrom-SpTelemetryBytes` needs no change.
6. Tests: new C# tests next to `ProtectionConfigTests.cs` and `ApoTelemetryTests.cs`; `DspTests.cpp` and `ApoHost.cpp` (Key Question 7).

### Gaps
- Inheritance of the folder grants by a file the tray creates follows from the `(OI)(CI)` flags. The project's own test U6 checks it for an existing `config.bin` (`installer\Install.Tests.ps1` lines 1435-1437), and the install report says the plug-in reads the cap from its own file (`docs\03\INSTALL-REPORT.md` line 32). I did not test it with a second file such as `bands.bin`, and I ran no test.
- Whether the tray should write the band file at all (as opposed to a hand-edited file) is a product question; the plug-in side is the same either way.

---

## Key Question 7: Tests: how to extend `DspTests.cpp` and `ApoHost.cpp`

### Takeaway
Add ten DSP tests (T28-T37) and five host checks (H18-H22) that prove, with stated tolerances: bit-exact output when nothing is cut, a loud band capped at its centre, quiet bands changed only by the analytic skirt, no band cut ever raising a steady-state level, chunk-size independence, no allocation anywhere, and unchanged latency. The tolerances quoted below are what the model achieved and must be re-derived against the real code.

### Cited Findings
- `native\dsp\tests\DspTests.cpp` (1856 lines) replaces the global `operator new/delete` with counting versions (lines 6-9 and 32-40, the `NoAlloc` scope, plus an option to make one allocation fail); helpers: `Sine` (line 100), `WhiteNoise` (137), `Programme` (146), `AbruptBurst` (166), `AnyReduced` (184, so that "never louder" checks cannot pass vacuously), `MaxAbsDiff` (195), `PeakAbs` (207), `SameBits` (1231, a `memcmp` of two float vectors).
- Existing tests to imitate: T07 quiet input untouched (line 459), T10 Part C bit-exact copy after recovery (comment at lines 574-575), T11 disabled ramps to unity (599), T12 non-finite input gives zeros (628), T13 in-place equals out-of-place (679), T14 chunk-size independent (693), T22 cap never louder than input (1112; per sample and channel, the output magnitude minus the delayed input's magnitude is at most 1e-6), T23 non-finite safe (1130), T24 formats and out-of-memory (1310), T25 chunk-size independent (1702); the test table is `kTests[]` at line 1800 with `static_assert(... == 27, "the brief pins exactly 27 tests")` at line 1831.
- `native\apohost\ApoHost.cpp` (about 2200 lines) loads the real DLL through COM; helpers `Bring`, `RunTone`, `Drain`, `HostMeter`, `MakeConfig`, `WriteConfig`, `PollTel`, `FillSine`; H12 checks latency, expecting `(240 + 8)/48000 * 1e7` 100 ns units for the cap and 0 for the Leveler, and a different value at 96 kHz (lines 1556-1588); the checks to imitate are H5 lock and process across formats with re-lock (line 1022), H6 non-finite input (1126), H7 separate buffers (1198), H8 telemetry (1234), H9 config file (1318), H10 crash-loop guard (1427), H11 many streams (1491), H14 `Reset` and a second `Initialize` (1635) and H17 the silent-flagged tail (2014); checks are registered by `Run("H17", CheckH17)` at lines 2175-2191.
- The latest install ran 27 of 27 DSP tests, host checks H1-H17, 56 of 56 installer tests and 497 of 497 tray tests — `docs\03\INSTALL-REPORT.md` lines 54-58.
- C# tests live in `tests\SoundProtection.Tests\`.

### Inferences
**Signals (DESIGN).**
- Sine sweeps: one tone per step, log-spaced from 20 Hz to 0.45*fs, with an integer number of cycles in the measuring window so that amplitude by correlation is exact (as in the model).
- Band-limited noise: `WhiteNoise` through a band-pass biquad (`sp::Biquad`, `SpDsp.h` line 149).
- Speech-like: harmonic series (F0 220 Hz and 110 Hz) with a Lorentzian formant envelope plus a 4-9 kHz noise burst with an 80 ms envelope for sibilance.
- Burst-then-quiet sequences for recovery tests (as in `AbruptBurst`).

**New DSP tests (T28-T37; `kTests[]` and the `static_assert` at line 1831 go from 27 to 37, and the brief that pins 27 must change with them).**

| # | Name | What it proves | Tolerance (model value in brackets) |
|---|---|---|---|
| T28 | idle is bit-exact | Speech-like and noise input whose band levels stay under every ceiling: output == input for every sample, in-place and out-of-place, mono and stereo | exact (48000 of 48000) |
| T29 | caps a centre tone | A sine at `f0`, 14 dB over the ceiling: settled level within the tolerance of the ceiling; `GainReductionDb()` within 0.1 dB of 14; within 1 dB of the ceiling after a stated time | +-0.05 dB (0.019); 20 ms (13 ms) |
| T30 | skirt matches the closed form | A loud centre tone plus quiet tones at other frequencies: each quiet tone's level change equals the analytic bell response for the `c` in force (computed in the test from `GainReductionDb()` and the formula in Key Question 1, not from the DSP) | +-0.05 dB (0.02) |
| T31 | never amplifies a tone | Sweep at several levels: every tone's settled output level is at most its input level; the response of any band never above 0 dB | +0.01 dB |
| T32 | energy and peak bounds | Stationary programme with a forced steady cut: `sum(y^2) <= sum(x^2)*(1 + 1e-3)`; sample peak at most `prod(1 + c_max*L1_b)` times the input peak, with `L1_b` the L1 norm of the band-pass impulse response computed by a separate double-precision simulation in the test | the energy tolerance is UNCERTAIN (time-varying `c` adds a small term) |
| T33 | non-finite and overload | NaN, Inf and +-1000 inputs: output finite; the next clean block equals a clean run (no poisoned state) | exact |
| T34 | chunk-size independent | Chunks of 1, 7, 64, 480 and 4096 frames give identical output | bit-exact |
| T35 | formats and no allocation | Rates 8000, 11025, 22050, 44100, 48000, 96000, 192000, 384000, 768000 and channels 1, 2, 6, 8, 64; bands above `0.45*fs` inactive; zero allocations in `Prepare`, `SetParams` and `Process` (a stronger statement than the library rule, and the proof that `Prepare` cannot throw) | exact |
| T36 | in-place and unprepared | `in == out` equals out-of-place; unprepared fails closed (NaN to 0, clamp +-1.0, no gain); writes nothing when `Prepare` was never called | exact |
| T37 | disabled and parameter changes | `enabled = false` ramps GR to exactly 0 and the output is again an exact copy; a mid-stream ceiling change produces no sample-to-sample jump beyond what the smoothing allows | exact; jump bound derived from the coefficients |

**New host checks (DESIGN; `native\apohost\ApoHost.cpp`).**
- H18 (pattern: H8, telemetry, lines 1234-1317): write `bands.bin` with one 3 kHz band; through the real DLL, a 3 kHz tone at -6 dBFS ends within tolerance of the ceiling while a 300 Hz tone at the same level changes only by the analytic skirt; `PollTel` shows the per-band gain reduction.
- H19: latency is unchanged: H12 still passes (the stage adds 0) at 48 kHz and at 96 kHz.
- H20 (pattern: H9, config file, lines 1318-1426): an absent, truncated or corrupt `bands.bin` leaves the stage off and the output an exact copy (apart from the cap's delay); `config.bin` v1 handling is unaffected.
- H21: safe mode (as H10 trips the guard) bypasses the stage.
- H22: many streams (as H11): the stage adds no slot and no failure.

**C# and installer (DESIGN).** A `bands.bin` golden-vector test; an `ApoTelemetryTests` case for nonzero values in bytes 56..63 and for old zero-filled files; an installer test that the existing telemetry verification still passes with the new bytes; installer changes for `bands.bin` itself only if the installer seeds it (Key Question 6, mirror-list item 5).

**What to assert to "prove a loud band is capped and quiet bands are untouched".** "Capped" means: the tone at the band centre is at the ceiling within the stated tolerance. It does not mean any tone in the band is capped (Key Question 4). "Untouched" means: bit-exact when no band is above its ceiling, and otherwise within the analytic skirt of the cut bands (not "unchanged"). A band-reading check on band-limited content (for the speech-like case: the output band level at most about 3 dB over the ceiling when 8 dB of cut is demanded; the model gave 2.2 dB) is a regression guard, not a specification (UNCERTAIN).

### Gaps
- The tolerances come from a model with synthetic signals; each must be re-measured on the real implementation, and the tolerance for time-varying `c` in T32 needs derivation.
- I did not read all of `ApoHost.cpp`; the helper signatures need checking before writing H18-H22.
- No real voice recordings are available in the repository for a listening or recorded-speech test.

---

## Key Question 8: Open-source references and licences: what can be learned, what could be copied

### Takeaway
Only Faust's `svf` and crossover functions (STK-4.3), Airwindows (MIT) and Tympan (MIT, but with unclear provenance of its core) are permissively licensed; openMHA (AGPL-3.0), LSP (LGPL-3.0), chowdsp's filter and compressor modules (GPLv3), x42 (GPL) and Equalizer APO (GPLv2) are learn-only. Nothing needs to be copied: the RBJ cookbook and Simper's note supply all the maths.

### Cited Findings
Licences and file paths were read from the GitHub repositories (API and headers) in November 2026; this is not legal advice.

| Project | Licence | What it offers | Source |
|---|---|---|---|
| Faust libraries | Per function. `filters.lib`: `svf` and `crossover2LR4`/`crossover3LR4` are STK-4.3 (MIT-style); `tf2_tpt`/`tf2_lpt` MIT; `TF2` is LGPL-2.1+ with the Faust exception. `compressors.lib`: header LGPL with exception, many functions GPL-3.0-only, the JOS compressors STK-4.3 | The reference TPT SVF with all mixes, LR4 crossovers, compressor and limiter maths | [grame-cncm/faustlibraries](https://github.com/grame-cncm/faustlibraries) |
| LSP plug-ins | LGPL-3.0 (`lsp-plugins`, `lsp-plugins-mb-compressor`, `lsp-dsp-units`) | `lsp-dsp-units`: `dynamics/` (Compressor, DynamicProcessor, Expander, Gate, Limiter, SurgeProtector, AutoGain), `filters/` (ButterworthFilter, DynamicFilters, Equalizer, Filter, FilterBank, SpectralTilt), `util/` (Crossover, FFTCrossover, LPCrossover, SpectralProcessor, SpectralSplitter, Sidechain, Oversampler, LatencyDetector); `Crossover.h` has the LR4 slope and the allpass compensation schema | [lsp-plugins/lsp-dsp-units](https://github.com/lsp-plugins/lsp-dsp-units) |
| openMHA | AGPL-3.0 | Hearing-aid framework: plugins `dc`, `dc_simple`, `multibandcompressor` (FFT filterbank `MHAOvlFilter::fftfb_t`, per-band levels and gains), `fftfilterbank`, `gtfb_analyzer`; `dc.hh` carries level and time-constant parameters (`taurmslevel`, `tauattack`, `taudecay`) | [HoerTech-gGmbH/openMHA](https://github.com/HoerTech-gGmbH/openMHA) |
| Tympan library | MIT | `src/AudioEffectCompWDRC_F32.h`: a wide-dynamic-range compressor derived from CHAPRO (BTNRH) whose licence line in the header reads "Creative Commons?" (provenance uncertain); repository pushed 2026-10-02 | [Tympan/Tympan_Library](https://github.com/Tympan/Tympan_Library) |
| Airwindows | MIT | `plugins/MacVST/DeEss/source/DeEssProc.cpp`: a wideband-style de-esser that adds a noise-floor dither | [airwindows/airwindows](https://github.com/airwindows/airwindows) |
| chowdsp_utils | `LICENSE.md`: each module has its own licence and non-module code is GPLv3; `chowdsp_filters` and `chowdsp_compressor` are GPLv3 | `StateVariableFilter` (Bell, LowShelf, HighShelf, Crossover types), `LinkwitzRileyFilter`, `ThreeWayCrossoverFilter`, `CompressorGainComputer`, `LevelDetector`, `chowdsp_LinearPhase3WayCrossover.h` | [Chowdhury-DSP/chowdsp_utils](https://github.com/Chowdhury-DSP/chowdsp_utils) |
| x42 | `dpl.lv2` GPL-3.0; `fil4.lv2` GPL-2.0 | A digital peak limiter and a parametric EQ | [x42/dpl.lv2](https://github.com/x42/dpl.lv2), [x42/fil4.lv2](https://github.com/x42/fil4.lv2) |
| Equalizer APO | GPLv2; version 1.4.2; project last updated 2025-11-28 | How an APO host is installed and configured (pre-mix and post-mix); the signing and DRM consequence; it cannot process compressed Dolby bitstreams | [SourceForge](https://sourceforge.net/projects/equalizerapo/), [documentation](https://sourceforge.net/p/equalizerapo/wiki/Documentation/) |
| RBJ Audio EQ Cookbook | W3C Note, 8 June 2021 | The formulas in Key Question 3 | [w3.org](https://www.w3.org/TR/audio-eq-cookbook/) |
| Cytomic SVF notes | Copyright Andrew Simper (2013, allpass added 2016) | The derivation of the trapezoidal SVF | [SvfLinearTrapOptimised2.pdf](https://cytomic.com/files/dsp/SvfLinearTrapOptimised2.pdf) |

### Inferences
- **What to learn.** Faust `svf` (the exact mixes, shelf `g` scaling, high-pass-first solution); Faust and LSP `Crossover.h` for the 3-band allpass compensation (option b); openMHA's `multibandcompressor` and `dc` for how a hearing-aid-grade multiband compressor organises per-band levels, gains and time constants (their numbers were not read); chowdsp's `CompressorGainComputer` and `LevelDetector` for gain-computer structure; Equalizer APO for installation pitfalls (unsigned DRM effect, drivers that disable options when another APO is registered), which our install already faces.
- **What could be copied under licence (DESIGN, UNCERTAIN on licence texts).** Only maths is needed; if code is ever copied, Faust's STK-4.3 functions and Airwindows (MIT) are the permissive candidates, with their notices kept. I did not re-read the STK-4.3 text in this pass; check the header of the specific function before copying. Tympan's MIT licence sits on a file that says it is derived from CHAPRO with a question-marked licence, so do not copy from it. For the AGPL, LGPL and GPL projects, copying code into a project that might one day be shared would carry copyleft obligations; a personal, undistributed build is a different case that I have not analysed (UNCERTAIN, not legal advice). Re-deriving from the RBJ and Simper maths avoids the question.
- **What not to take.** Airwindows DeEss is a wideband de-esser with added dither, which conflicts with the exact-copy rule; openMHA and LSP are FFT or crossover heavy, not zero-latency.

### Gaps
- openMHA's `dc` time-constant defaults, LSP's `DynamicFilters` internals and chowdsp's dynamics code were identified by file name but not read in detail.
- The STK-4.3 licence text was not fetched in this pass.
- Tympan's CHAPRO provenance was not resolved.
- No search result gave typical hearing-aid attack/release values.
