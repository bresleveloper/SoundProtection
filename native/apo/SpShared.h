// SpShared.h - the contract shared by the plug-in (C++), the installer (PowerShell) and the tray app (C#).
// Owned by the controller. Agents read it; they never edit it. Any change here must be mirrored in
// src/SoundProtection.Core (ProtectionConfig.cs, ApoTelemetry.cs) and installer/SoundProtection.Install.ps1.
#pragma once

#include <cstdint>
#include <guiddef.h>

namespace sp {

// ---- Class IDs --------------------------------------------------------------------------------
// Leveler: per-stream (SFX) effect, appended to the composite list {d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13
// {026BF7A9-ED1A-4C62-ADC6-142802421CBA}
static const GUID kClsidLeveler = { 0x026bf7a9, 0xed1a, 0x4c62, { 0xad, 0xc6, 0x14, 0x28, 0x02, 0x42, 0x1c, 0xba } };
#define SP_CLSID_LEVELER_STR L"{026BF7A9-ED1A-4C62-ADC6-142802421CBA}"

// Safety cap: end-of-line (EFX) effect, appended to the composite list {d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15
// {148D0A3C-621B-40F5-B7FD-13E6CB834BCB}
static const GUID kClsidSafetyCap = { 0x148d0a3c, 0x621b, 0x40f5, { 0xb7, 0xfd, 0x13, 0xe6, 0xcb, 0x83, 0x4b, 0xcb } };
#define SP_CLSID_SAFETYCAP_STR L"{148D0A3C-621B-40F5-B7FD-13E6CB834BCB}"

#define SP_LEVELER_FRIENDLY_NAME   L"Sound Protection Leveler"
#define SP_SAFETYCAP_FRIENDLY_NAME L"Sound Protection Safety Cap"

// ---- Files ------------------------------------------------------------------------------------
// Data folder: %ProgramData%\SoundProtection, unless the environment variable below is set (tests only).
#define SP_DATA_DIR_ENV    L"SOUNDPROTECTION_DATA_DIR"
#define SP_DATA_DIR_NAME   L"SoundProtection"
#define SP_CONFIG_FILE     L"config.bin"          // <data>\config.bin            (tray writes, plug-in reads)
#define SP_TELEMETRY_DIR   L"telemetry"           // <data>\telemetry\            (plug-in writes)
#define SP_TELEMETRY_FILE  L"apo.bin"             // <data>\telemetry\apo.bin     (memory-mapped, tray reads)
#define SP_GUARD_FILE      L"guard.bin"           // <data>\telemetry\guard.bin   (crash-loop guard)
#define SP_LOG_FILE        L"apo.log"             // <data>\telemetry\apo.log     (UTF-8, capped at 256 KB, then .old)

// ---- Defaults and limits ----------------------------------------------------------------------
constexpr float kDefaultCapDb    = -20.0f;  // loudness cap before the volume slider (LUFS-like dB)
constexpr float kDefaultMarginDb = 12.0f;   // safety cap ceiling = min(cap + margin, -1)
constexpr float kMinCapDb = -60.0f, kMaxCapDb = 0.0f;
constexpr float kMinMarginDb = 0.0f, kMaxMarginDb = 30.0f;
constexpr float kCeilingMaxDb = -1.0f;

#pragma pack(push, 1)

// ---- Config: 32 bytes, little-endian ------------------------------------------------------------
// checksum = FNV-1a 32-bit over bytes 0..27 (offset basis 2166136261, prime 16777619).
// Golden 1 (seq 1, enabled, cap -20, margin 12):
//   53 50 43 46 01 00 00 00 01 00 00 00 01 00 00 00 00 00 A0 C1 00 00 40 41 00 00 00 00 EA FA CF 1E
// Golden 2 (seq 42, disabled, cap -35.5, margin 6):
//   53 50 43 46 01 00 00 00 2A 00 00 00 00 00 00 00 00 00 0E C2 00 00 C0 40 00 00 00 00 58 06 44 0E
constexpr uint32_t kConfigMagic   = 0x46435053;  // "SPCF"
constexpr uint32_t kConfigVersion = 1;
struct SpConfig {
    uint32_t magic;     // 0
    uint32_t version;   // 4
    uint32_t sequence;  // 8   incremented by the tray on every write
    uint32_t enabled;   // 12  0 = off (effects ramp to pass-through, stay in line), 1 = on
    float    capDb;     // 16  clamp to [kMinCapDb, kMaxCapDb] when read
    float    marginDb;  // 20  clamp to [kMinMarginDb, kMaxMarginDb] when read
    uint32_t reserved;  // 24  0
    uint32_t checksum;  // 28
};
static_assert(sizeof(SpConfig) == 32, "SpConfig must be 32 bytes");

// ---- Telemetry: header (64 bytes) + kTelemetrySlots slots (64 bytes each) = 2112 bytes -------------
constexpr uint32_t kTelemetryMagic   = 0x4D545053;  // "SPTM"
constexpr uint32_t kTelemetryVersion = 1;
constexpr uint32_t kTelemetrySlots   = 32;
constexpr uint32_t kTelemetryBytes   = 64 + 32 * 64;

constexpr uint32_t kFlagConfigValid = 0x1;  // a valid config.bin is in use (else defaults)
constexpr uint32_t kFlagSafeMode    = 0x2;  // crash-loop guard tripped: clamp-only processing
constexpr uint32_t kFlagEnabled     = 0x4;  // config enabled

struct SpTelemetryHeader {
    uint32_t magic;              // 0
    uint32_t version;            // 4
    uint32_t slotCount;          // 8   = kTelemetrySlots
    uint32_t slotSize;           // 12  = 64
    uint32_t writerPid;          // 16  audiodg process id
    uint32_t configSequence;     // 20  sequence of the config in use (0 = defaults)
    uint64_t heartbeat;          // 24  +1 on every worker tick (every 50 ms)
    int64_t  heartbeatFileTime;  // 32  UTC FILETIME of the last tick
    uint32_t flags;              // 40  kFlag*
    float    capDb;              // 44  cap in use
    float    ceilingDb;          // 48  safety cap ceiling in use (0 when disabled)
    uint8_t  reserved[12];       // 52..63
};
static_assert(sizeof(SpTelemetryHeader) == 64, "SpTelemetryHeader must be 64 bytes");

enum SpSlotKind : uint32_t { kSlotFree = 0, kSlotLeveler = 1, kSlotSafetyCap = 2 };
enum SpMode : uint32_t {
    kModeUnknown = 0, kModeDefault = 1, kModeRaw = 2, kModeCommunications = 3, kModeSpeech = 4,
    kModeMedia = 5, kModeMovie = 6, kModeNotification = 7, kModeOther = 8
};

struct SpTelemetrySlot {
    uint32_t kind;                 // 0   SpSlotKind
    uint32_t instanceId;           // 4   unique within the process, never 0
    uint32_t sampleRate;           // 8
    uint32_t channels;             // 12
    uint32_t mode;                 // 16  SpMode, from Initialize
    uint32_t clipperHits;          // 20  safety cap: cumulative final-clamp hits; leveler: 0
    uint64_t framesProcessed;      // 24  cumulative
    int64_t  lastProcessFileTime;  // 32  UTC FILETIME stamped by the worker when framesProcessed changed
    float    inputDb;              // 40  leveler: momentary loudness of its input; safety cap: input true peak over the last tick
    float    gainReductionDb;      // 44  current, >= 0
    float    maxGainReductionDb;   // 48  largest during the last tick
    float    outputPeakDb;         // 52  output sample peak during the last tick
    uint8_t  reserved[8];          // 56..63
};
static_assert(sizeof(SpTelemetrySlot) == 64, "SpTelemetrySlot must be 64 bytes");

// ---- Crash-loop guard: 16 bytes ------------------------------------------------------------------
constexpr uint32_t kGuardMagic = 0x44475053;  // "SPGD"
constexpr uint32_t kGuardSafeModeAt = 3;      // this many unclean starts in a row => safe mode
struct SpGuard {
    uint32_t magic;          // 0
    uint32_t version;        // 4  = 1
    uint32_t uncleanStarts;  // 8  +1 each time the process goes from no locked effect to one (several effects
                             //    locking together count once); 0 after 10 s of healthy processing or when the
                             //    process's last locked effect unlocks cleanly. A crash in any session therefore
                             //    leaves the count raised for the next start.
    uint32_t reserved;       // 12
};
static_assert(sizeof(SpGuard) == 16, "SpGuard must be 16 bytes");

#pragma pack(pop)

}  // namespace sp
