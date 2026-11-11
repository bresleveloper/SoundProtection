// SpRuntime.h - process-wide state of the Sound Protection plug-in.
//
// One Runtime exists per process (per loaded copy of the DLL). It owns:
//   - the settings every effect reads on the audio path (cap, margin, enabled, safe mode), published through a
//     sequence-numbered set of atomics, so the audio path never takes a lock,
//   - the worker thread (50 ms tick): config.bin watcher, telemetry writer, health check, log flusher,
//   - the memory-mapped telemetry file, the crash-loop guard file and the log file,
//   - the table of 32 telemetry slots, one per locked effect.
//
// Threading rules:
//   - ApoCreated/ApoDestroyed/EffectLocked/EffectUnlocked/Log run on control threads (LockForProcess,
//     UnlockForProcess, constructors, destructors). They may take locks and touch files.
//   - SettingsSeq/ReadSettings and the SlotRt atomics are the only things the audio path touches. They are
//     lock-free and allocate nothing.
//   - Nothing here is ever called from DllMain. The worker is started by the first LockForProcess and joined by
//     the Release that destroys the last object.
//   - Every file, mapping or thread failure is logged once and degrades the feature. None of them throws or
//     crashes the process.
#pragma once

// SpDsp.h uses std::min and std::max: windows.h must not define the min and max macros.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <atomic>
#include <cstdint>

#include "SpShared.h"

namespace sp {
namespace apo {

constexpr uint32_t kSettingsEnabled = 0x1;  // config.bin says "on"
constexpr uint32_t kSettingsSafe    = 0x2;  // crash-loop guard tripped: clamp-only processing

// What the telemetry file shows for "no signal".
constexpr float kTelemetryFloorDb = -120.0f;

// The settings the audio path follows. They always come from a validated, clamped config (or the defaults).
struct Settings {
    float    capDb    = sp::kDefaultCapDb;
    float    marginDb = sp::kDefaultMarginDb;
    uint32_t flags    = kSettingsEnabled;
    uint32_t seq      = 0;  // the publication this copy was read from

    bool Enabled() const { return (flags & kSettingsEnabled) != 0; }
    bool Safe() const { return (flags & kSettingsSafe) != 0; }
};

// Lock-free "keep the largest" for floats. NaN never wins (every comparison with it is false).
inline void AtomicMax(std::atomic<float>& a, float v) {
    float cur = a.load(std::memory_order_relaxed);
    while (v > cur && !a.compare_exchange_weak(cur, v, std::memory_order_relaxed)) {
    }
}

// One telemetry slot as the runtime keeps it. The audio thread of the owning effect writes the atomics; the
// worker reads them (and exchanges the max-values back to zero). Everything else is guarded by the state lock.
struct SlotRt {
    // ---- guarded by the runtime's state lock ----
    bool     inUse = false;
    uint32_t kind = 0;        // SpSlotKind
    uint32_t instanceId = 0;  // process-unique, never 0
    uint32_t sampleRate = 0;
    uint32_t channels = 0;
    uint32_t mode = 0;        // SpMode
    uint64_t lastFrames = 0;  // frames seen at the previous tick
    int64_t  lastProcessFileTime = 0;

    // ---- written by the audio thread, read by the worker ----
    std::atomic<uint64_t> frames{0};
    std::atomic<uint32_t> clipperHits{0};
    std::atomic<float>    inputDb{kTelemetryFloorDb};
    std::atomic<float>    gainReductionDb{0.0f};
    std::atomic<float>    maxGainReductionDb{0.0f};
    std::atomic<float>    outputPeakLin{0.0f};  // linear; the worker converts to dB once per tick
};

class Runtime {
public:
    // ---- live-object accounting; control threads --------------------------------------------------------
    void ApoCreated();
    // Call this AFTER the object's memory is gone. It stops the worker when the last object is destroyed, and its
    // final act is the decrement of the live count: nothing in this DLL's data is touched after it.
    void ApoDestroyed();
    void FactoryCreated() { factories_.fetch_add(1, std::memory_order_relaxed); }
    void FactoryDestroyed() { factories_.fetch_sub(1, std::memory_order_relaxed); }
    void ServerLock(bool lock);
    // S_OK-able only when no object, no class factory, no server lock and no worker thread exists.
    bool CanUnloadNow() const;

    // ---- effect lifecycle; control threads only ---------------------------------------------------------
    // Registers a locked effect. Starts the worker and reads the config on the first one, and counts an unclean
    // start in the guard when the process had no locked effect before. Returns the effect's telemetry slot, or
    // nullptr when all 32 are taken (the effect then runs without telemetry).
    SlotRt* EffectLocked(uint32_t kind, uint32_t sampleRate, uint32_t channels, uint32_t mode);
    // Releases the slot. When the last locked effect of the process unlocks, the guard goes back to 0.
    void EffectUnlocked(SlotRt* slot);

    // ---- audio path: atomics only -----------------------------------------------------------------------
    uint32_t SettingsSeq() const { return setSeq_.load(std::memory_order_acquire); }
    // Copies the current settings. False when a publication was in progress on every try: keep the old values.
    bool ReadSettings(Settings& out) const;

    // ---- diagnostics; control threads and the worker, never the audio path -------------------------------
    void Log(_Printf_format_string_ const char* fmt, ...);

private:
    static unsigned __stdcall WorkerMain(void* self);
    void WorkerLoop();
    void Tick();

    void ResolvePathsLocked();
    void EnsureStartedLocked();
    void StopWorkerLocked();

    void CheckConfig();
    void PublishSettingsLocked();
    void LatchSafeMode();

    void OpenTelemetry();
    void CloseTelemetry();
    // Writes one image of the telemetry file. advancing = at least one slot processed frames since the last tick.
    void WriteTelemetry(bool& advancing);

    // What reading guard.bin at the start of a session found. NoHistory: no file (ERROR_FILE_NOT_FOUND), or a file
    // that is not a guard and so holds no count to lose. Unavailable: it exists, or might, but cannot be opened or
    // read (sharing violation, access denied, missing folder, I/O error): its contents are unknown, so it is left alone.
    enum class GuardRead { Ok, NoHistory, Unavailable };
    GuardRead ReadGuardFile(sp::SpGuard& g, DWORD& error) const;
    DWORD WriteGuardFile(uint32_t uncleanStarts) const;  // 0 = written; else the Win32 error (never 0 for a failure)

    void GuardSessionStart();
    // Puts 0 back into guard.bin when this process's +1 is in it. A reset that fails stays owed: the guard stays armed
    // and the worker tries again (retryOnly = the worker's paced retry, which does nothing unless one is owed).
    void GuardReset(bool retryOnly);
    void HealthTick(bool advancing);

    SlotRt* ClaimSlotLocked(uint32_t kind, uint32_t sampleRate, uint32_t channels, uint32_t mode);
    uint32_t NextInstanceId();

    void FlushLog();

    // ---- counters ------------------------------------------------------------------------------------------
    // live_ is what DllCanUnloadNow looks at. objects_ is the same count, kept under lifecycle_, so that exactly
    // one destroying thread sees "I am the last". live_ is decremented after objects_ and after the worker stopped.
    std::atomic<uint32_t> live_{0};
    uint32_t              objects_ = 0;  // lifecycle_
    std::atomic<uint32_t> factories_{0};
    std::atomic<uint32_t> locks_{0};
    std::atomic<bool>     workerRunning_{false};

    // Lock order: lifecycle_ -> state_. guardLock_ and logLock_ are leaf locks. The worker never takes lifecycle_.
    SRWLOCK lifecycle_ = SRWLOCK_INIT;  // serialises start, stop, first lock and last unlock
    SRWLOCK state_ = SRWLOCK_INIT;      // slots, config values, settings publication
    SRWLOCK guardLock_ = SRWLOCK_INIT;  // guard.bin and the guard flags
    SRWLOCK logLock_ = SRWLOCK_INIT;    // the log queue

    // ---- paths (set once per load, under lifecycle_) ---------------------------------------------------------
    bool pathsReady_ = false;
    bool pathsFailed_ = false;
    wchar_t dataDir_[MAX_PATH] = {};
    wchar_t configPath_[MAX_PATH] = {};
    wchar_t telemetryPath_[MAX_PATH] = {};
    wchar_t guardPath_[MAX_PATH] = {};
    wchar_t logPath_[MAX_PATH] = {};
    wchar_t logOldPath_[MAX_PATH + 8] = {};

    // ---- worker ------------------------------------------------------------------------------------------------
    HANDLE   thread_ = nullptr;
    HANDLE   stopEvent_ = nullptr;
    uint64_t tick_ = 0;       // worker only
    uint64_t heartbeat_ = 0;  // worker only

    // ---- telemetry file ------------------------------------------------------------------------------------------
    HANDLE telFile_ = INVALID_HANDLE_VALUE;
    HANDLE telMap_ = nullptr;
    void*  telView_ = nullptr;
    bool   telFailureLogged_ = false;

    // ---- slots (state_) ---------------------------------------------------------------------------------------------
    SlotRt   slots_[sp::kTelemetrySlots];
    uint32_t lockedEffects_ = 0;
    bool     slotsFullLogged_ = false;
    std::atomic<uint32_t> nextInstanceId_{1};

    // ---- config in use (state_) ---------------------------------------------------------------------------------------
    float    cfgCap_ = sp::kDefaultCapDb;
    float    cfgMargin_ = sp::kDefaultMarginDb;
    bool     cfgEnabled_ = true;
    uint32_t cfgSequence_ = 0;  // 0 = defaults
    bool     cfgValid_ = false;
    // What the config watcher saw last (worker or first-lock thread, never both at once).
    bool     sigHave_ = false;
    int64_t  sigWrite_ = 0;
    uint64_t sigSize_ = 0;
    bool     cfgInvalidLogged_ = false;

    // ---- settings publication: a sequence lock over atomics --------------------------------------------------------------
    std::atomic<uint32_t> setSeq_{0};  // even = stable, odd = being written
    std::atomic<float>    setCap_{sp::kDefaultCapDb};
    std::atomic<float>    setMargin_{sp::kDefaultMarginDb};
    std::atomic<uint32_t> setFlags_{kSettingsEnabled};

    // ---- crash-loop guard ---------------------------------------------------------------------------------------------------
    std::atomic<bool>     safe_{false};  // sticky for the life of this copy of the DLL
    bool                  guardArmed_ = false;  // guard.bin currently holds this process's +1 (guardLock_)
    // A reset was tried (last unlock, or 10 s healthy) and failed, so guardArmed_ stays set until one works. Written
    // under guardLock_; the worker reads it without the lock as a hint and checks again under the lock.
    std::atomic<bool>     guardResetOwed_{false};
    uint64_t              guardLastTryMs_ = 0;              // when that reset was last tried (guardLock_)
    bool                  guardUnavailableLogged_ = false;  // this episode's "crash guard unavailable" line is written (guardLock_)
    bool                  guardResetFailLogged_ = false;    // this episode's "cannot reset guard.bin" line is written (guardLock_)
    std::atomic<uint32_t> sessionEpoch_{0};     // +1 whenever the process goes from no locked effect to one
    uint32_t              healthEpoch_ = 0;     // worker only
    uint64_t              healthySinceMs_ = 0;  // worker only
    uint64_t              lastAdvanceMs_ = 0;   // worker only

    // ---- log queue (logLock_) ------------------------------------------------------------------------------------------------------
    static constexpr uint32_t kLogLines = 64;
    static constexpr uint32_t kLogLineMax = 256;
    char     logLines_[kLogLines][kLogLineMax] = {};
    uint32_t logHead_ = 0;
    uint32_t logCount_ = 0;
    uint32_t logDropped_ = 0;
    bool     logFileFailedOnce_ = false;
    std::atomic<uint32_t> formatLogBudget_{50};

public:
    // Format rejections are logged at most 50 times per load, so a chatty caller cannot flood the log.
    bool TakeFormatLogBudget();
};

Runtime& GetRuntime();

}  // namespace apo
}  // namespace sp
