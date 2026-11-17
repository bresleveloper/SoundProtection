# Bass and Treble Caps (V2): Design

2026-11-17. Ariel's go: "ok, dont commit anything, we are testing a V2 for this app. use sonnets subagents, and build the thing".
This design is built and tested on the PC, but it is **not committed and not installed**. Installing needs Ariel's own go (admin prompt).

## Why

V1 works ("the solution works amazing") for loudness. But Ariel says some voices still sound too hard even when they are low: "sometimes a woman with a high voice, or a man with a deep voice, both sound too hard even when low". V1's Leveler and Safety Cap turn the whole sound down by one amount. That leaves the voice's tonal shape unchanged, so a shrill voice stays shrill at any volume. The background is in `research\Harsh voice frequency protection.md`.

Ariel's ask was: "can you do another 2 sliders with bass and treble caps?", and then "but all INSIDE the sound engine right?". The answer is yes.

## What Ariel sees

The tray panel gets two new rows under the cap slider and its "Cap (dB)" row:

```
Bass     ━━━━━━━━━━━━━━━━━━━●  Off
Treble   ━━━━━━━━━━●────────   -32.0 dB   cutting 4.0 dB
```

- They follow the same rule as the main cap. When that part of the sound goes over its slider, only that part is turned down. Below the slider, nothing changes.
- A slider at its right end (0 dB) is **Off**. Both sliders start at Off.
- "cutting N dB" is the cut the plug-in is making right now. It is blank when there is no cut.
- The tooltips are in plain words. Bass is "the deep, boomy part"; Treble is "the sharp, high part".

## Behaviour (binding)

1. **Inside the sound engine.** Both caps run in the plug-in, inside the Safety Cap effect: end of line, all sound on the protected output, before the Windows volume slider. They run ahead of its limiter. The plug-in reads `bands.bin` itself, so the caps work while the tray is closed.
2. **Bands.** Bass is centred at 160 Hz with Q 0.6, covering about 75 to 340 Hz. Treble is centred at 3500 Hz with Q 0.6, covering about 1.6 to 7.5 kHz.
3. **Cuts only.** A cap never boosts any frequency, and it cuts at most 12 dB per band.
4. **Exact copy when idle.** A band that is off or under its cap leaves the output bit for bit equal to the input.
5. **Level and range.** A band's level is its mean square, in dB relative to a full-scale sine: a −20 dBFS sine at the band centre reads −20. A cap goes from −60 to 0 dB. Over the cap, the cut is the excess (limiter rule), up to the 12 dB maximum.
6. **Timing.** All values are one-pole time constants.

   | Band | Detector | Attack | Release |
   |---|---|---|---|
   | Bass | 25 ms | 20 ms | 300 ms |
   | Treble | 5 ms | 5 ms | 150 ms |

   A cut that reaches 0.01 dB or less on its way down snaps to exactly 0, which brings back the exact copy.
7. **Linked channels.** Each band makes one cut for all channels, driven by the loudest channel, so the stereo image holds.
8. **No latency.** The Safety Cap's reported latency stays as it is today.
9. **Off and safe mode.**
   - Protection switched off (config.bin `enabled = 0`): both cuts release to 0, the way the existing effects ramp to pass-through.
   - Safe mode (crash-loop guard tripped): no DSP runs, so the caps are bypassed, as today.
10. **A bad `bands.bin`.** If it is missing or invalid, the plug-in keeps the last good values. Before any good file has been read, both caps are off.
11. **V1 compatibility.**
    - `config.bin` version 1 is unchanged.
    - An old tray with the new plug-in: no `bands.bin`, so the caps are off.
    - The new tray with the old plug-in: `bands.bin` is ignored.

## Contract

`native\apo\SpShared.h` holds the contract. The controller owns it, and the C# side mirrors it.

**`bands.bin`** sits in the data folder next to `config.bin`. It is 32 bytes, little-endian. The tray writes it (temporary file, then move) and the plug-in reads it.

| Offset | Type | Field | Value |
|---|---|---|---|
| 0 | u32 | magic | `0x44425053` ("SPBD") |
| 4 | u32 | version | 1 |
| 8 | u32 | sequence | +1 on every tray write |
| 12 | u32 | flags | bit 0 bass on, bit 1 treble on, other bits 0 |
| 16 | f32 | bassCapDb | read clamped to [−60, 0]; NaN reads as 0 |
| 20 | f32 | trebleCapDb | the same |
| 24 | u32 | reserved | 0 |
| 28 | u32 | checksum | FNV-1a 32 over bytes 0..27 |

- Golden B1 (seq 1, bass on −30, treble on −35):
  `53 50 42 44 01 00 00 00 01 00 00 00 03 00 00 00 00 00 F0 C1 00 00 0C C2 00 00 00 00 3A 06 98 16`
- Golden B2 (seq 42, treble only −27.5, bass 0):
  `53 50 42 44 01 00 00 00 2A 00 00 00 02 00 00 00 00 00 00 00 00 00 DC C1 00 00 00 00 4A 2C 0A 69`
- The tray sets a band's bit when its slider is below 0 dB.

**Telemetry.** In each slot, bytes 56..63 were reserved. They become two floats:
- `bassCutDb` at 56 and `trebleCutDb` at 60;
- Safety Cap slots carry the current cuts (each ≥ 0); Leveler slots carry 0;
- the header version stays 1.

## Approaches

| | Approach | Verdict |
|---|---|---|
| A | A cut-only band filter per cap, `y = x − c·bandpass(x)` (Simper SVF), in the Safety Cap | **Chosen** |
| B | A Linkwitz-Riley crossover with a limiter per band | Rejected |
| C | FFT spectral suppression | Rejected |
| D | A static always-on EQ as the main tool | Rejected |

- **Why A:**
  - the filter's poles never move;
  - it is an exact copy when idle;
  - it is provably cut-only, because for a unity-peak band-pass, `|1 − c·BP|² = 1 − c(2 − c)|BP|² ≤ 1` for 0 ≤ c ≤ 2;
  - it adds no latency and costs little CPU.
- **Why not B:** it is never an exact copy, and it can overshoot near the crossovers.
- **Why not C:** it adds latency and needs buffers sized by the sample rate.
- **Why not D:** it dulls every voice all the time. The same engine with a fixed cut can do this later.

Ariel chose the user-facing shape, two cap sliders. The method (A) is the controller's call, based on the research's implementation notes.

## Non-goals in V2

- No per-app caps.
- No third band for "S" sounds.
- No relative (shape-based) detection.
- No self-test and no audio capture.
- No installer change.
- No change to `config.bin`.
- No git commit.
- No install.

## Constraints

- **DSP rules.** The rules at the top of `native\dsp\SpDsp.h` apply:
  - no allocation, lock or OS call in Process or SetParams;
  - finite output;
  - IEEE arithmetic (`/fp:precise`, no fast-math);
  - 8 kHz to 768 kHz, and 1 to 64 channels.
- **Builds.** MSVC builds use `/W4 /WX`, with zero warnings. `dotnet build` reports 0 warnings.
- **Tests that stay green.**
  - The DSP tests go from 27 to 40 (T28 to T40).
  - Host checks H1 to H17 stay, and H18 to H21 are added.
  - The C# suite.
- **What stays untouched.**
  - `dist\`, where the V1 tray is running.
  - `C:\Program Files\SoundProtection`, `C:\ProgramData\SoundProtection` and the registry.
- **Outputs.**
  - The V2 tray is published to `dist-staging\`.
  - The V2 DLL builds to `native\out\apo\`, as before.
- **Git.** No commits. The controller stages files as review checkpoints.

## Left to the builder

The exact spacing in the panel, the names of private helpers, and test-signal details beyond the values the plan pins.
