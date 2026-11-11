// SpRuntime.cpp - process-wide state of the Sound Protection plug-in. See SpRuntime.h for the rules.
#include "SpRuntime.h"

#include <process.h>
#include <strsafe.h>

#include <cstdarg>
#include <cstring>
#include <cwchar>

#include "SpDspSelect.h"

namespace sp {
namespace apo {

namespace {

constexpr DWORD    kShareAll = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;
constexpr DWORD    kWorkerTickMs = 50;
constexpr uint64_t kConfigEveryNTicks = 4;           // 200 ms
constexpr uint64_t kTelemetryRetryTicks = 20;        // 1 s
constexpr int64_t  kRecentWrite100ns = 10000000;      // a config written in the last second is re-read every check
constexpr uint64_t kHealthyMs = 10000;                // healthy processing that clears the guard
constexpr uint64_t kStallMs = 2000;                   // no frames for this long restarts the healthy count
constexpr DWORD    kJoinTimeoutMs = 3000;
constexpr size_t   kLogCapBytes = 256 * 1024;
constexpr size_t   kLogFlushBytes = 16 * 1024;
constexpr uint32_t kGuardVersion = 1;
constexpr uint32_t kGuardCountMax = 1000000u;         // the count saturates here
constexpr uint64_t kGuardRetryMs = 1000;              // a reset that failed is tried again this often (worker)

Runtime g_runtime;

// GetLastError() right after a failed call, never 0: the guard code uses 0 for "no error".
DWORD LastErrorNonZero() {
    const DWORD e = GetLastError();
    return e != 0 ? e : ERROR_GEN_FAILURE;
}

inline int64_t ToInt64(const FILETIME& ft) {
    return (static_cast<int64_t>(ft.dwHighDateTime) << 32) | static_cast<int64_t>(ft.dwLowDateTime);
}

uint32_t Fnv1a(const uint8_t* p, size_t n) {
    uint32_t h = 2166136261u;
    for (size_t i = 0; i < n; ++i) {
        h ^= p[i];
        h *= 16777619u;
    }
    return h;
}

float ClampFloat(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

bool IsFinite(float v) { return v > -3.4e38f && v < 3.4e38f; }  // false for NaN and Inf

bool JoinPath(wchar_t* out, size_t cap, const wchar_t* a, const wchar_t* b) {
    return SUCCEEDED(StringCchPrintfW(out, cap, L"%s\\%s", a, b));
}

// Copies a block into the mapped view. A disk that fails under the mapping raises EXCEPTION_IN_PAGE_ERROR; that
// must cost us the telemetry, never the process. (No C++ objects here: __try cannot unwind them.)
bool CopyToView(void* view, const void* src, size_t n) {
    __try {
        std::memcpy(view, src, n);
        return true;
    } __except (GetExceptionCode() == EXCEPTION_IN_PAGE_ERROR || GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
                    ? EXCEPTION_EXECUTE_HANDLER
                    : EXCEPTION_CONTINUE_SEARCH) {
        return false;
    }
}

// Reads config.bin. True only for a complete, well-formed file: 32 bytes, magic, version, checksum, finite floats.
bool ReadConfigFile(const wchar_t* path, sp::SpConfig& out) {
    HANDLE h = CreateFileW(path, GENERIC_READ, kShareAll, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size = {};
    uint8_t raw[sizeof(sp::SpConfig)] = {};
    DWORD got = 0;
    bool ok = GetFileSizeEx(h, &size) != 0 && size.QuadPart == static_cast<LONGLONG>(sizeof(raw));
    if (ok) ok = ReadFile(h, raw, sizeof(raw), &got, nullptr) != 0 && got == sizeof(raw);
    CloseHandle(h);
    if (!ok) return false;
    std::memcpy(&out, raw, sizeof(out));
    if (out.magic != sp::kConfigMagic || out.version != sp::kConfigVersion) return false;
    if (Fnv1a(raw, 28) != out.checksum) return false;
    if (!IsFinite(out.capDb) || !IsFinite(out.marginDb)) return false;
    return true;
}

// UTF-8 for the log. Returns the number of bytes written (without the terminator).
void ToUtf8(const wchar_t* w, char* out, size_t cap) {
    if (cap == 0) return;
    out[0] = 0;
    const int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, out, static_cast<int>(cap), nullptr, nullptr);
    if (n <= 0) out[cap - 1] = 0;
}

}  // namespace

Runtime& GetRuntime() { return g_runtime; }

// =====================================================================================================
// Accounting
// =====================================================================================================

void Runtime::ServerLock(bool lock) {
    if (lock) {
        locks_.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    uint32_t cur = locks_.load(std::memory_order_relaxed);
    while (cur > 0 && !locks_.compare_exchange_weak(cur, cur - 1, std::memory_order_relaxed)) {
    }
}

bool Runtime::CanUnloadNow() const {
    return live_.load(std::memory_order_acquire) == 0 && factories_.load(std::memory_order_acquire) == 0 &&
           locks_.load(std::memory_order_acquire) == 0 && !workerRunning_.load(std::memory_order_acquire);
}

bool Runtime::TakeFormatLogBudget() {
    uint32_t cur = formatLogBudget_.load(std::memory_order_relaxed);
    while (cur > 0) {
        if (formatLogBudget_.compare_exchange_weak(cur, cur - 1, std::memory_order_relaxed)) return true;
    }
    return false;
}

uint32_t Runtime::NextInstanceId() {
    uint32_t id = nextInstanceId_.fetch_add(1, std::memory_order_relaxed);
    if (id == 0) id = nextInstanceId_.fetch_add(1, std::memory_order_relaxed);  // wrapped: 0 is never an id
    return id;
}

void Runtime::ApoCreated() {
    AcquireSRWLockExclusive(&lifecycle_);
    ++objects_;
    live_.fetch_add(1, std::memory_order_acq_rel);
    ReleaseSRWLockExclusive(&lifecycle_);
}

void Runtime::ApoDestroyed() {
    AcquireSRWLockExclusive(&lifecycle_);
    // Under the lifecycle lock exactly one destroyer sees "last" and stops the worker.
    if (objects_ > 0 && --objects_ == 0) StopWorkerLocked();
    ReleaseSRWLockExclusive(&lifecycle_);
    // The very last access to this DLL's data. Until it happens DllCanUnloadNow says "no".
    live_.fetch_sub(1, std::memory_order_acq_rel);
}

// =====================================================================================================
// Paths
// =====================================================================================================

void Runtime::ResolvePathsLocked() {
    if (pathsReady_ || pathsFailed_) return;
    wchar_t dir[MAX_PATH] = {};
    bool ok = true;
    const DWORD n = GetEnvironmentVariableW(SP_DATA_DIR_ENV, dir, MAX_PATH);
    if (n >= MAX_PATH) {
        // The test variable is set but does not fit. Never fall back to the live folder in that case.
        ok = false;
        dir[0] = 0;
    } else if (n == 0) {
        wchar_t programData[MAX_PATH] = {};
        const DWORD m = GetEnvironmentVariableW(L"ProgramData", programData, MAX_PATH);
        if (m == 0 || m >= MAX_PATH) StringCchCopyW(programData, MAX_PATH, L"C:\\ProgramData");
        ok = JoinPath(dir, MAX_PATH, programData, SP_DATA_DIR_NAME);
    }
    size_t len = std::wcslen(dir);
    while (len > 3 && (dir[len - 1] == L'\\' || dir[len - 1] == L'/')) dir[--len] = 0;  // no doubled separators

    wchar_t sub[MAX_PATH] = {};
    ok = ok && SUCCEEDED(StringCchCopyW(dataDir_, MAX_PATH, dir)) && JoinPath(configPath_, MAX_PATH, dir, SP_CONFIG_FILE) &&
         JoinPath(sub, MAX_PATH, dir, SP_TELEMETRY_DIR) && JoinPath(telemetryPath_, MAX_PATH, sub, SP_TELEMETRY_FILE) &&
         JoinPath(guardPath_, MAX_PATH, sub, SP_GUARD_FILE) && JoinPath(logPath_, MAX_PATH, sub, SP_LOG_FILE) &&
         SUCCEEDED(StringCchPrintfW(logOldPath_, sizeof(logOldPath_) / sizeof(logOldPath_[0]), L"%s.old", logPath_));
    if (ok) {
        pathsReady_ = true;
        char utf8[MAX_PATH * 3] = {};
        ToUtf8(dataDir_, utf8, sizeof(utf8));
        Log("data folder: %s", utf8);
    } else {
        pathsFailed_ = true;
        dataDir_[0] = configPath_[0] = telemetryPath_[0] = guardPath_[0] = logPath_[0] = logOldPath_[0] = 0;
        Log("the data folder path is too long; config, telemetry, guard and log are off");
    }
}

// =====================================================================================================
// Start and stop of the worker
// =====================================================================================================

void Runtime::EnsureStartedLocked() {
    ResolvePathsLocked();
    if (workerRunning_.load(std::memory_order_acquire)) return;

    sigHave_ = false;
    tick_ = 0;
    OpenTelemetry();
    CheckConfig();  // the first buffer must already follow the config, not the defaults

    stopEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!stopEvent_) {
        Log("cannot create the worker's stop event (error %lu); telemetry and config reloads are off", GetLastError());
        CloseTelemetry();
        return;
    }
    unsigned threadId = 0;
    const uintptr_t h = _beginthreadex(nullptr, 0, &Runtime::WorkerMain, this, 0, &threadId);
    if (h == 0) {
        Log("cannot start the worker thread; telemetry and config reloads are off");
        CloseHandle(stopEvent_);
        stopEvent_ = nullptr;
        CloseTelemetry();
        return;
    }
    thread_ = reinterpret_cast<HANDLE>(h);
    workerRunning_.store(true, std::memory_order_release);
    Log("worker started");
#ifdef SP_DSP_STUB
    // A stub build must never be mistaken for the real one: it passes audio through and protects nothing.
    Log("WARNING: this build uses the STUB DSP (pass-through). It does not level or limit anything. Test builds only.");
#endif
}

void Runtime::StopWorkerLocked() {
    if (!workerRunning_.load(std::memory_order_acquire)) return;
    if (stopEvent_) SetEvent(stopEvent_);
    const DWORD w = thread_ ? WaitForSingleObject(thread_, kJoinTimeoutMs) : WAIT_OBJECT_0;
    if (w == WAIT_OBJECT_0) {
        if (thread_) CloseHandle(thread_);
        thread_ = nullptr;
        if (stopEvent_) CloseHandle(stopEvent_);
        stopEvent_ = nullptr;
        CloseTelemetry();
        workerRunning_.store(false, std::memory_order_release);
        return;
    }
    // The worker is stuck (a file call that never returns). It still runs this DLL's code, so the DLL must never
    // be unloaded: pin it, and keep workerRunning_ true so DllCanUnloadNow keeps saying no.
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                       reinterpret_cast<LPCWSTR>(&Runtime::WorkerMain), &self);
    Log("the worker did not stop within %lu ms; the plug-in stays loaded", kJoinTimeoutMs);
}

unsigned __stdcall Runtime::WorkerMain(void* self) {
    static_cast<Runtime*>(self)->WorkerLoop();
    return 0;
}

void Runtime::WorkerLoop() {
    for (;;) {
        Tick();  // first thing: the telemetry file is valid at once
        if (WaitForSingleObject(stopEvent_, kWorkerTickMs) == WAIT_OBJECT_0) {
            Tick();  // last thing: slots freed, heartbeat, log flushed
            return;
        }
    }
}

void Runtime::Tick() {
    ++tick_;
    if (tick_ % kConfigEveryNTicks == 1) CheckConfig();
    if (!telView_ && tick_ % kTelemetryRetryTicks == 0) OpenTelemetry();  // the folder may appear later
    bool advancing = false;
    WriteTelemetry(advancing);
    HealthTick(advancing);
    FlushLog();
}

// =====================================================================================================
// Effects
// =====================================================================================================

SlotRt* Runtime::EffectLocked(uint32_t kind, uint32_t sampleRate, uint32_t channels, uint32_t mode) {
    SlotRt* slot = nullptr;
    bool newSession = false;
    bool slotsFull = false;

    AcquireSRWLockExclusive(&lifecycle_);
    EnsureStartedLocked();
    AcquireSRWLockExclusive(&state_);
    newSession = (lockedEffects_ == 0);
    ++lockedEffects_;
    slot = ClaimSlotLocked(kind, sampleRate, channels, mode);
    if (!slot && !slotsFullLogged_) {
        slotsFullLogged_ = true;
        slotsFull = true;
    }
    ReleaseSRWLockExclusive(&state_);
    if (newSession) GuardSessionStart();
    ReleaseSRWLockExclusive(&lifecycle_);

    if (slotsFull) Log("all %u telemetry slots are taken; further effects run without telemetry", sp::kTelemetrySlots);
    return slot;
}

void Runtime::EffectUnlocked(SlotRt* slot) {
    bool lastOne = false;
    AcquireSRWLockExclusive(&lifecycle_);
    AcquireSRWLockExclusive(&state_);
    if (slot) {
        slot->inUse = false;
        slot->kind = 0;
    }
    if (lockedEffects_ > 0) {
        --lockedEffects_;
        lastOne = (lockedEffects_ == 0);
    }
    ReleaseSRWLockExclusive(&state_);
    if (lastOne) GuardReset(false);
    ReleaseSRWLockExclusive(&lifecycle_);
}

SlotRt* Runtime::ClaimSlotLocked(uint32_t kind, uint32_t sampleRate, uint32_t channels, uint32_t mode) {
    for (uint32_t i = 0; i < sp::kTelemetrySlots; ++i) {
        SlotRt& s = slots_[i];
        if (s.inUse) continue;
        s.kind = kind;
        s.instanceId = NextInstanceId();
        s.sampleRate = sampleRate;
        s.channels = channels;
        s.mode = mode;
        s.lastFrames = 0;
        s.lastProcessFileTime = 0;
        s.frames.store(0, std::memory_order_relaxed);
        s.clipperHits.store(0, std::memory_order_relaxed);
        s.inputDb.store(kTelemetryFloorDb, std::memory_order_relaxed);
        s.gainReductionDb.store(0.0f, std::memory_order_relaxed);
        s.maxGainReductionDb.store(0.0f, std::memory_order_relaxed);
        s.outputPeakLin.store(0.0f, std::memory_order_relaxed);
        s.inUse = true;
        return &s;
    }
    return nullptr;
}

// =====================================================================================================
// Settings (sequence lock over atomics)
// =====================================================================================================

void Runtime::PublishSettingsLocked() {
    const uint32_t v = setSeq_.load(std::memory_order_relaxed);
    setSeq_.store(v + 1, std::memory_order_relaxed);  // odd: readers back off
    std::atomic_thread_fence(std::memory_order_release);
    setCap_.store(cfgCap_, std::memory_order_relaxed);
    setMargin_.store(cfgMargin_, std::memory_order_relaxed);
    setFlags_.store((cfgEnabled_ ? kSettingsEnabled : 0u) | (safe_.load(std::memory_order_relaxed) ? kSettingsSafe : 0u),
                    std::memory_order_relaxed);
    setSeq_.store(v + 2, std::memory_order_release);  // even: stable
}

bool Runtime::ReadSettings(Settings& out) const {
    for (int attempt = 0; attempt < 4; ++attempt) {
        const uint32_t s1 = setSeq_.load(std::memory_order_acquire);
        if (s1 & 1u) continue;
        const float cap = setCap_.load(std::memory_order_relaxed);
        const float margin = setMargin_.load(std::memory_order_relaxed);
        const uint32_t flags = setFlags_.load(std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_acquire);
        if (setSeq_.load(std::memory_order_relaxed) == s1) {
            out.capDb = cap;
            out.marginDb = margin;
            out.flags = flags;
            out.seq = s1;
            return true;
        }
    }
    return false;
}

void Runtime::LatchSafeMode() {
    AcquireSRWLockExclusive(&state_);
    if (!safe_.load(std::memory_order_relaxed)) {
        safe_.store(true, std::memory_order_relaxed);
        PublishSettingsLocked();
    }
    ReleaseSRWLockExclusive(&state_);
}

// =====================================================================================================
// Config
// =====================================================================================================

void Runtime::CheckConfig() {
    if (!pathsReady_) return;

    FILETIME nowFt = {};
    GetSystemTimeAsFileTime(&nowFt);
    const int64_t now = ToInt64(nowFt);

    bool ok = false;
    sp::SpConfig c = {};
    WIN32_FILE_ATTRIBUTE_DATA fad = {};
    if (GetFileAttributesExW(configPath_, GetFileExInfoStandard, &fad)) {
        const int64_t write = ToInt64(fad.ftLastWriteTime);
        const uint64_t size = (static_cast<uint64_t>(fad.nFileSizeHigh) << 32) | fad.nFileSizeLow;
        const bool changed = !sigHave_ || write != sigWrite_ || size != sigSize_;
        // Two writes inside one timestamp tick look identical, so a file written in the last second is read again.
        const bool recent = (now - write) < kRecentWrite100ns;
        if (!changed && !recent) return;
        sigHave_ = true;
        sigWrite_ = write;
        sigSize_ = size;
        ok = ReadConfigFile(configPath_, c);
        if (!ok) sigHave_ = false;  // torn, locked or wrong: look again at the next check, however old the file is
    } else {
        sigHave_ = false;  // missing: keep the last good values
    }

    bool logApplied = false;
    bool logInvalid = false;
    float cap = 0.0f;
    float margin = 0.0f;
    bool enabled = false;
    uint32_t sequence = 0;

    AcquireSRWLockExclusive(&state_);
    if (ok) {
        cap = ClampFloat(c.capDb, sp::kMinCapDb, sp::kMaxCapDb);
        margin = ClampFloat(c.marginDb, sp::kMinMarginDb, sp::kMaxMarginDb);
        enabled = (c.enabled != 0);
        sequence = c.sequence;
        const bool differs = cfgSequence_ != sequence || cfgCap_ != cap || cfgMargin_ != margin || cfgEnabled_ != enabled;
        cfgValid_ = true;
        cfgInvalidLogged_ = false;
        if (differs) {
            cfgCap_ = cap;
            cfgMargin_ = margin;
            cfgEnabled_ = enabled;
            cfgSequence_ = sequence;
            PublishSettingsLocked();
            logApplied = true;
        }
    } else {
        cfgValid_ = false;
        if (!cfgInvalidLogged_) {
            cfgInvalidLogged_ = true;
            logInvalid = true;
        }
    }
    ReleaseSRWLockExclusive(&state_);

    if (logApplied) {
        Log("config applied: sequence %u, %s, cap %.1f dB, margin %.1f dB", sequence, enabled ? "on" : "off", cap, margin);
    }
    if (logInvalid) Log("config.bin is missing or invalid; keeping the last good values");
}

// =====================================================================================================
// Telemetry
// =====================================================================================================

void Runtime::OpenTelemetry() {
    if (telView_ || !pathsReady_) return;
    HANDLE f = CreateFileW(telemetryPath_, GENERIC_READ | GENERIC_WRITE, kShareAll, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) {
        if (!telFailureLogged_) {
            telFailureLogged_ = true;
            Log("telemetry is off: cannot open apo.bin (error %lu)", GetLastError());
        }
        return;
    }
    HANDLE m = CreateFileMappingW(f, nullptr, PAGE_READWRITE, 0, sp::kTelemetryBytes, nullptr);
    if (!m) {
        if (!telFailureLogged_) {
            telFailureLogged_ = true;
            Log("telemetry is off: cannot map apo.bin (error %lu)", GetLastError());
        }
        CloseHandle(f);
        return;
    }
    void* v = MapViewOfFile(m, FILE_MAP_WRITE, 0, 0, sp::kTelemetryBytes);
    if (!v) {
        if (!telFailureLogged_) {
            telFailureLogged_ = true;
            Log("telemetry is off: cannot map a view of apo.bin (error %lu)", GetLastError());
        }
        CloseHandle(m);
        CloseHandle(f);
        return;
    }
    telFile_ = f;
    telMap_ = m;
    telView_ = v;
}

void Runtime::CloseTelemetry() {
    if (telView_) UnmapViewOfFile(telView_);
    if (telMap_) CloseHandle(telMap_);
    if (telFile_ != INVALID_HANDLE_VALUE) CloseHandle(telFile_);
    telView_ = nullptr;
    telMap_ = nullptr;
    telFile_ = INVALID_HANDLE_VALUE;
}

void Runtime::WriteTelemetry(bool& advancing) {
    advancing = false;

    alignas(8) uint8_t image[sp::kTelemetryBytes];
    std::memset(image, 0, sizeof(image));
    sp::SpTelemetryHeader* hdr = reinterpret_cast<sp::SpTelemetryHeader*>(image);
    sp::SpTelemetrySlot* slots = reinterpret_cast<sp::SpTelemetrySlot*>(image + sizeof(sp::SpTelemetryHeader));

    FILETIME ft = {};
    GetSystemTimeAsFileTime(&ft);
    const int64_t now = ToInt64(ft);

    AcquireSRWLockExclusive(&state_);
    hdr->configSequence = cfgSequence_;
    hdr->flags = (cfgValid_ ? sp::kFlagConfigValid : 0u) | (safe_.load(std::memory_order_relaxed) ? sp::kFlagSafeMode : 0u) |
                 (cfgEnabled_ ? sp::kFlagEnabled : 0u);
    hdr->capDb = cfgCap_;
    hdr->ceilingDb = cfgEnabled_ ? sp::CeilingDb(cfgCap_, cfgMargin_) : 0.0f;
    for (uint32_t i = 0; i < sp::kTelemetrySlots; ++i) {
        SlotRt& s = slots_[i];
        if (!s.inUse) continue;
        sp::SpTelemetrySlot& t = slots[i];
        const uint64_t frames = s.frames.load(std::memory_order_relaxed);
        if (frames != s.lastFrames) {
            s.lastFrames = frames;
            s.lastProcessFileTime = now;
            advancing = true;
        }
        t.kind = s.kind;
        t.instanceId = s.instanceId;
        t.sampleRate = s.sampleRate;
        t.channels = s.channels;
        t.mode = s.mode;
        t.clipperHits = s.clipperHits.load(std::memory_order_relaxed);
        t.framesProcessed = frames;
        t.lastProcessFileTime = s.lastProcessFileTime;
        // The Safety Cap reports its input peak over the last tick; the Leveler reports its latest loudness.
        t.inputDb = (s.kind == sp::kSlotSafetyCap) ? s.inputDb.exchange(kTelemetryFloorDb, std::memory_order_relaxed)
                                                   : s.inputDb.load(std::memory_order_relaxed);
        t.gainReductionDb = s.gainReductionDb.load(std::memory_order_relaxed);
        t.maxGainReductionDb = s.maxGainReductionDb.exchange(0.0f, std::memory_order_relaxed);
        t.outputPeakDb = sp::LinearToDb(s.outputPeakLin.exchange(0.0f, std::memory_order_relaxed));
    }
    ReleaseSRWLockExclusive(&state_);

    hdr->magic = sp::kTelemetryMagic;
    hdr->version = sp::kTelemetryVersion;
    hdr->slotCount = sp::kTelemetrySlots;
    hdr->slotSize = sizeof(sp::SpTelemetrySlot);
    hdr->writerPid = GetCurrentProcessId();
    hdr->heartbeat = ++heartbeat_;
    hdr->heartbeatFileTime = now;

    if (telView_ && !CopyToView(telView_, image, sizeof(image))) {
        if (!telFailureLogged_) {
            telFailureLogged_ = true;
            Log("telemetry is off: writing to the mapped file failed");
        }
        CloseTelemetry();
    }
}

// =====================================================================================================
// Crash-loop guard
// =====================================================================================================

// The rules, in one place (a failure of guard.bin must neither erase a crash count nor leave a stale +1 behind):
//   - A start adds 1 and arms the guard. A clean end (the last unlock, or 10 s of healthy processing) writes 0.
//   - No guard.bin (ERROR_FILE_NOT_FOUND) is "no history". A file that reads but is not a guard (short, foreign
//     magic or version) holds no count to lose: also "no history", and the start repairs it.
//   - A guard.bin that cannot be opened or read for any other reason is unavailable: its count is unknown, so the file
//     is left exactly as it is, this start is not counted and does not arm the guard, and apo.log says so once.
//   - A start whose +1 cannot be written is treated the same way: not counted, not armed, said once.
//   - A reset that fails stays owed: the guard stays armed, and the worker tries again every second (the last unlock
//     tries too, if the failure was at the 10 s mark). The log gets one line for a failing episode, not one per try.
//     Limit: if the last object is destroyed while a reset is still owed, nobody retries it; the +1 stays in the file
//     and the next start counts it.

Runtime::GuardRead Runtime::ReadGuardFile(sp::SpGuard& g, DWORD& error) const {
    error = 0;
    HANDLE h = CreateFileW(guardPath_, GENERIC_READ, kShareAll, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        error = LastErrorNonZero();
        return error == ERROR_FILE_NOT_FOUND ? GuardRead::NoHistory : GuardRead::Unavailable;
    }
    uint8_t raw[sizeof(sp::SpGuard)] = {};
    DWORD got = 0;
    const BOOL ok = ReadFile(h, raw, sizeof(raw), &got, nullptr);
    if (!ok) error = LastErrorNonZero();  // before CloseHandle
    CloseHandle(h);
    if (!ok) return GuardRead::Unavailable;
    if (got != sizeof(raw)) return GuardRead::NoHistory;  // empty or short: not a guard
    std::memcpy(&g, raw, sizeof(g));
    return g.magic == sp::kGuardMagic && g.version == kGuardVersion ? GuardRead::Ok : GuardRead::NoHistory;
}

DWORD Runtime::WriteGuardFile(uint32_t uncleanStarts) const {
    HANDLE h = CreateFileW(guardPath_, GENERIC_WRITE, kShareAll, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return LastErrorNonZero();
    sp::SpGuard g = {};
    g.magic = sp::kGuardMagic;
    g.version = kGuardVersion;
    g.uncleanStarts = uncleanStarts;
    g.reserved = 0;
    DWORD written = 0;
    DWORD error = 0;
    if (!WriteFile(h, &g, sizeof(g), &written, nullptr)) {
        error = LastErrorNonZero();
    } else if (written != sizeof(g)) {
        error = ERROR_WRITE_FAULT;
    } else if (!SetEndOfFile(h)) {  // exactly 16 bytes
        error = LastErrorNonZero();
    }
    CloseHandle(h);
    return error;
}

// Called with lifecycle_ held when the process goes from no locked effect to one.
void Runtime::GuardSessionStart() {
    sessionEpoch_.fetch_add(1, std::memory_order_relaxed);
    if (!pathsReady_) return;  // "the data folder path is too long" is in the log already: the guard is off for this load

    sp::SpGuard g = {};
    uint32_t before = 0;
    DWORD readError = 0;
    DWORD writeError = 0;
    bool logUnavailable = false;
    AcquireSRWLockExclusive(&guardLock_);
    const GuardRead rd = ReadGuardFile(g, readError);
    if (rd == GuardRead::Ok) before = g.uncleanStarts;
    if (rd != GuardRead::Unavailable) writeError = WriteGuardFile(before < kGuardCountMax ? before + 1 : before);
    if (rd != GuardRead::Unavailable && writeError == 0) {
        guardArmed_ = true;
        guardResetOwed_.store(false, std::memory_order_relaxed);  // a reset still owed from an earlier session is part of this one's
        guardUnavailableLogged_ = false;                          // the guard works again: the next failure is a new episode
        guardResetFailLogged_ = false;
    } else if (!guardUnavailableLogged_) {
        // guardArmed_ stays as it is: a +1 of this process that is still owed stays owed, and nothing new was written.
        guardUnavailableLogged_ = true;
        logUnavailable = true;
    }
    ReleaseSRWLockExclusive(&guardLock_);

    if (before >= sp::kGuardSafeModeAt) {
        LatchSafeMode();
        Log("safe mode: %u unclean starts in a row; clamp-only processing until the DLL is unloaded", before);
    }
    if (logUnavailable) {
        if (rd == GuardRead::Unavailable) {
            Log("crash guard unavailable: cannot read guard.bin (error %lu); this start is not counted", readError);
        } else {
            Log("crash guard unavailable: cannot write guard.bin (error %lu); this start is not counted", writeError);
        }
    }
}

void Runtime::GuardReset(bool retryOnly) {
    const uint64_t nowMs = GetTickCount64();
    DWORD logError = 0;
    AcquireSRWLockExclusive(&guardLock_);
    if (!guardArmed_) guardResetOwed_.store(false, std::memory_order_relaxed);
    if (guardArmed_ && (!retryOnly || (guardResetOwed_.load(std::memory_order_relaxed) && nowMs - guardLastTryMs_ >= kGuardRetryMs))) {
        const DWORD error = WriteGuardFile(0);
        if (error == 0) {
            guardArmed_ = false;
            guardResetOwed_.store(false, std::memory_order_relaxed);
            guardResetFailLogged_ = false;
        } else {
            guardResetOwed_.store(true, std::memory_order_relaxed);
            guardLastTryMs_ = nowMs;
            if (!guardResetFailLogged_) {
                guardResetFailLogged_ = true;
                logError = error;
            }
        }
    }
    ReleaseSRWLockExclusive(&guardLock_);
    if (logError != 0) Log("crash guard: cannot reset guard.bin (error %lu); the +1 of this start stays in it until a retry works", logError);
}

// Worker only. After 10 s of frames moving and no safe mode, the process counts as a healthy start.
void Runtime::HealthTick(bool advancing) {
    const uint64_t nowMs = GetTickCount64();
    // A reset that failed (at the last unlock or at the 10 s mark) is tried again, whatever the health state.
    if (guardResetOwed_.load(std::memory_order_relaxed)) GuardReset(true);
    const uint32_t epoch = sessionEpoch_.load(std::memory_order_relaxed);
    if (epoch != healthEpoch_) {
        healthEpoch_ = epoch;
        healthySinceMs_ = 0;
        lastAdvanceMs_ = 0;
    }
    if (safe_.load(std::memory_order_relaxed)) {
        healthySinceMs_ = 0;
        return;
    }
    if (advancing) {
        if (healthySinceMs_ == 0) healthySinceMs_ = nowMs;
        lastAdvanceMs_ = nowMs;
    } else if (healthySinceMs_ != 0 && nowMs - lastAdvanceMs_ > kStallMs) {
        healthySinceMs_ = 0;
    }
    if (healthySinceMs_ != 0 && nowMs - healthySinceMs_ >= kHealthyMs) {
        GuardReset(false);  // if this fails the reset stays owed, and the retry above takes over
        healthySinceMs_ = 0;
    }
}

// =====================================================================================================
// Log
// =====================================================================================================

void Runtime::Log(const char* fmt, ...) {
    char msg[192];
    va_list ap;
    va_start(ap, fmt);
    StringCchVPrintfA(msg, sizeof(msg), fmt, ap);  // truncates, always terminates
    va_end(ap);

    SYSTEMTIME st = {};
    GetSystemTime(&st);
    char line[kLogLineMax];
    StringCchPrintfA(line, sizeof(line), "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ pid=%lu tid=%lu %s\r\n", st.wYear, st.wMonth, st.wDay,
                     st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, GetCurrentProcessId(), GetCurrentThreadId(), msg);

    AcquireSRWLockExclusive(&logLock_);
    if (logCount_ < kLogLines) {
        char* dst = logLines_[(logHead_ + logCount_) % kLogLines];
        StringCchCopyA(dst, kLogLineMax, line);
        ++logCount_;
    } else {
        ++logDropped_;
    }
    ReleaseSRWLockExclusive(&logLock_);
}

// Worker only (and nothing else touches the file). Lines are UTF-8; past 256 KB the file rolls to apo.log.old.
void Runtime::FlushLog() {
    char buf[kLogFlushBytes];
    size_t used = 0;
    uint32_t dropped = 0;

    AcquireSRWLockExclusive(&logLock_);
    while (logCount_ > 0) {
        const char* line = logLines_[logHead_];
        const size_t len = std::strlen(line);
        if (used + len > sizeof(buf) - 96) break;
        std::memcpy(buf + used, line, len);
        used += len;
        logHead_ = (logHead_ + 1) % kLogLines;
        --logCount_;
    }
    dropped = logDropped_;
    logDropped_ = 0;
    ReleaseSRWLockExclusive(&logLock_);

    if (dropped > 0) {
        char note[96];
        StringCchPrintfA(note, sizeof(note), "%u log lines were dropped because the queue was full\r\n", dropped);
        const size_t len = std::strlen(note);
        std::memcpy(buf + used, note, len);
        used += len;
    }
    if (used == 0 || !pathsReady_) return;

    HANDLE h = CreateFileW(logPath_, FILE_APPEND_DATA, kShareAll, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        logFileFailedOnce_ = true;  // the lines are dropped; the data folder or its telemetry folder is not there
        return;
    }
    LARGE_INTEGER size = {};
    if (GetFileSizeEx(h, &size) && static_cast<uint64_t>(size.QuadPart) + used > kLogCapBytes) {
        CloseHandle(h);
        MoveFileExW(logPath_, logOldPath_, MOVEFILE_REPLACE_EXISTING);
        h = CreateFileW(logPath_, FILE_APPEND_DATA, kShareAll, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) return;
    }
    DWORD written = 0;
    WriteFile(h, buf, static_cast<DWORD>(used), &written, nullptr);
    CloseHandle(h);
}

}  // namespace apo
}  // namespace sp
