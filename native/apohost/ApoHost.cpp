// ApoHost.cpp - test host for SoundProtection.Apo.dll: checks H1 to H12 of the plug-in plan (Task 2), plus H13, which
// pins the guard's ten-second healthy reset (the brief asks for it; H1 to H12 do not cover it; it takes about 11 s),
// and H14, which covers two calls the engine makes that the brief's list leaves out (Reset, a second Initialize).
// H15 and H16 pin how the crash-loop guard treats a guard.bin that cannot be written or read (H15 takes about 26 s),
// and H17 pins that a silent-flagged period still delivers the DSP's look-ahead tail.
//
// Like the brief's set-up, every object is created through a test outer object (aggregated). H1 covers both ways of
// creating an object, and H5 and H6 run once plain and once aggregated.
//
// The host loads the DLL with LoadLibrary and reaches it only through DllGetClassObject and DllCanUnloadNow.
// It never calls DllRegisterServer, DllUnregisterServer or RegisterAPO, it never plays sound, and it points
// SOUNDPROTECTION_DATA_DIR at %TEMP%\SoundProtectionHost-<pid>, so nothing outside that folder is touched.
//
// Usage:  ApoHost.exe <path to SoundProtection.Apo.dll>
// Output: one line per check, "PASS Hn" or "FAIL Hn: why". "SKIP Hn: ..." marks a sub-check that needs the real
//         DSP and therefore does not run when the DLL was built with the stub (SP_DSP_STUB).
// Exit:   0 when every check passed, 1 when any failed, 2 when the host itself could not start.

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <audioenginebaseapo.h>
#include <audioengineextensionapo.h>
#include <audiomediatype.h>
#include <ks.h>
#include <ksmedia.h>

#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

#include "SpShared.h"

namespace {

// ===================================================================================================
// Reporting
// ===================================================================================================

int g_failed = 0;

std::string Str(const char* fmt, ...) {
    char buf[600];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    buf[sizeof(buf) - 1] = 0;
    return std::string(buf);
}

std::string HrText(HRESULT hr) { return Str("0x%08lX", static_cast<unsigned long>(hr)); }

struct Check {
    explicit Check(const char* n) : name(n) {}
    const char* name;
    std::string why;
    std::string ctx;  // put in front of every failure ("[aggregated] ") while a check runs the same steps twice

    void Fail(const std::string& s) {
        if (!why.empty()) why += "; ";
        why += ctx + s;
    }
    bool Expect(bool ok, const std::string& s) {
        if (!ok) Fail(s);
        return ok;
    }
    void Skip(const std::string& s) const {
        printf("SKIP %s: %s\n", name, s.c_str());
        fflush(stdout);
    }
    void Done() {
        if (why.empty()) {
            printf("PASS %s\n", name);
        } else {
            printf("FAIL %s: %s\n", name, why.c_str());
            ++g_failed;
        }
        fflush(stdout);
    }
};

#ifdef SP_DSP_STUB
constexpr bool kStub = true;
#else
constexpr bool kStub = false;
#endif

// For conditions that mix the build flavour with a run-time value: "kStub && x" is a constant condition (C4127,
// an error under /WX) in a real build, a call is not.
bool StubBuild() { return kStub; }

// ===================================================================================================
// Paths, files
// ===================================================================================================

std::wstring g_dllPath;
std::wstring g_root;  // %TEMP%\SoundProtectionHost-<pid>

std::wstring DataPath(const wchar_t* rel) { return g_root + L"\\" + rel; }

bool WriteAll(const std::wstring& path, const void* data, DWORD n, bool truncate) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           truncate ? CREATE_ALWAYS : OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD w = 0;
    bool ok = WriteFile(h, data, n, &w, nullptr) != 0 && w == n;
    if (ok && !truncate) ok = SetEndOfFile(h) != 0;  // the file ends where the new content ends
    CloseHandle(h);
    return ok;
}

bool ReadAll(const std::wstring& path, std::vector<uint8_t>& out) {
    out.clear();
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    bool ok = GetFileSizeEx(h, &size) != 0 && size.QuadPart >= 0 && size.QuadPart < (1 << 20);
    if (ok) {
        out.resize(static_cast<size_t>(size.QuadPart));
        DWORD got = 0;
        if (!out.empty()) ok = ReadFile(h, out.data(), static_cast<DWORD>(out.size()), &got, nullptr) != 0 && got == out.size();
    }
    CloseHandle(h);
    if (!ok) out.clear();
    return ok;
}

void RemoveTree(const std::wstring& dir) {
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((dir + L"\\*").c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE) {
        do {
            const std::wstring name = fd.cFileName;
            if (name == L"." || name == L"..") continue;
            const std::wstring full = dir + L"\\" + name;
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
                RemoveTree(full);
            } else {
                SetFileAttributesW(full.c_str(), FILE_ATTRIBUTE_NORMAL);
                DeleteFileW(full.c_str());
            }
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    RemoveDirectoryW(dir.c_str());
}

// ===================================================================================================
// Config files
// ===================================================================================================

const uint8_t kGolden1[32] = { 0x53, 0x50, 0x43, 0x46, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
                               0x00, 0x00, 0xA0, 0xC1, 0x00, 0x00, 0x40, 0x41, 0x00, 0x00, 0x00, 0x00, 0xEA, 0xFA, 0xCF, 0x1E };
const uint8_t kGolden2[32] = { 0x53, 0x50, 0x43, 0x46, 0x01, 0x00, 0x00, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                               0x00, 0x00, 0x0E, 0xC2, 0x00, 0x00, 0xC0, 0x40, 0x00, 0x00, 0x00, 0x00, 0x58, 0x06, 0x44, 0x0E };

uint32_t Fnv1a(const uint8_t* p, size_t n) {
    uint32_t h = 2166136261u;
    for (size_t i = 0; i < n; ++i) {
        h ^= p[i];
        h *= 16777619u;
    }
    return h;
}

void MakeConfig(uint8_t out[32], uint32_t sequence, uint32_t enabled, float capDb, float marginDb) {
    sp::SpConfig c{};
    c.magic = sp::kConfigMagic;
    c.version = sp::kConfigVersion;
    c.sequence = sequence;
    c.enabled = enabled;
    c.capDb = capDb;
    c.marginDb = marginDb;
    c.reserved = 0;
    memcpy(out, &c, 28);
    c.checksum = Fnv1a(out, 28);
    memcpy(out + 28, &c.checksum, 4);
}

bool WriteConfig(const uint8_t cfg[32]) { return WriteAll(DataPath(L"config.bin"), cfg, 32, false); }

bool ReadGuard(sp::SpGuard& g) {
    std::vector<uint8_t> raw;
    if (!ReadAll(DataPath(L"telemetry\\guard.bin"), raw) || raw.size() != sizeof(sp::SpGuard)) return false;
    memcpy(&g, raw.data(), sizeof(g));
    return g.magic == sp::kGuardMagic;
}

bool WriteGuard(uint32_t uncleanStarts) {
    sp::SpGuard g{};
    g.magic = sp::kGuardMagic;
    g.version = 1;
    g.uncleanStarts = uncleanStarts;
    return WriteAll(DataPath(L"telemetry\\guard.bin"), &g, sizeof(g), true);
}

// ===================================================================================================
// Telemetry
// ===================================================================================================

struct Tel {
    std::vector<uint8_t> raw;
    const sp::SpTelemetryHeader& H() const { return *reinterpret_cast<const sp::SpTelemetryHeader*>(raw.data()); }
    const sp::SpTelemetrySlot& S(uint32_t i) const {
        return *reinterpret_cast<const sp::SpTelemetrySlot*>(raw.data() + sizeof(sp::SpTelemetryHeader) + i * sizeof(sp::SpTelemetrySlot));
    }
    bool Valid() const { return raw.size() >= sp::kTelemetryBytes && H().magic == sp::kTelemetryMagic; }
    int CountKind(uint32_t kind) const {
        int n = 0;
        for (uint32_t i = 0; i < sp::kTelemetrySlots; ++i) n += (S(i).kind == kind) ? 1 : 0;
        return n;
    }
    // Index of the first slot of this kind that has processed frames, or -1.
    int FindActive(uint32_t kind) const {
        for (uint32_t i = 0; i < sp::kTelemetrySlots; ++i)
            if (S(i).kind == kind && S(i).framesProcessed > 0) return static_cast<int>(i);
        return -1;
    }
};

bool ReadTel(Tel& t) { return ReadAll(DataPath(L"telemetry\\apo.bin"), t.raw) && t.Valid(); }

// Polls the telemetry file until pred(tel) holds or the timeout passes. The last snapshot stays in t.
template <class Pred>
bool PollTel(DWORD timeoutMs, Tel& t, Pred pred) {
    const ULONGLONG t0 = GetTickCount64();
    for (;;) {
        if (ReadTel(t) && pred(t)) return true;
        if (GetTickCount64() - t0 >= timeoutMs) return false;
        Sleep(10);
    }
}

bool Near(float a, float b, float tol = 1e-3f) { return std::fabs(a - b) <= tol; }

// A file someone else holds open (a scanner, a backup tool). 'share' is what the holder lets others do meanwhile. The
// plug-in opens guard.bin with every share mode, so its own open fails with a sharing violation (error 32) exactly
// when the holder's share mode lacks what the plug-in asks for: reading needs FILE_SHARE_READ, writing needs
// FILE_SHARE_WRITE. A holder that blocks reading blocks the host's reads too, so the checks let go before they look.
class FileHold {
public:
    FileHold() = default;
    FileHold(const FileHold&) = delete;
    FileHold& operator=(const FileHold&) = delete;
    ~FileHold() { Release(); }
    bool Take(const std::wstring& path, DWORD share) {
        Release();
        h_ = CreateFileW(path.c_str(), GENERIC_READ, share, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        return h_ != INVALID_HANDLE_VALUE;
    }
    void Release() {
        if (h_ != INVALID_HANDLE_VALUE) CloseHandle(h_);
        h_ = INVALID_HANDLE_VALUE;
    }

private:
    HANDLE h_ = INVALID_HANDLE_VALUE;
};

// The whole of apo.log (the worker flushes its queue every 50 ms; the file only grows during a run).
std::string ReadLog() {
    std::vector<uint8_t> raw;
    if (!ReadAll(DataPath(L"telemetry\\apo.log"), raw)) return std::string();
    return std::string(raw.begin(), raw.end());
}

size_t CountText(const std::string& text, const char* needle) {
    size_t count = 0;
    for (size_t at = text.find(needle); at != std::string::npos; at = text.find(needle, at + 1)) ++count;
    return count;
}

// The last line that contains the text, without its line end; empty when there is none.
std::string LastLineWith(const std::string& text, const char* needle) {
    const size_t at = text.rfind(needle);
    if (at == std::string::npos) return std::string();
    const size_t nl = text.rfind('\n', at);
    const size_t begin = (nl == std::string::npos) ? 0 : nl + 1;
    const size_t cr = text.find('\r', at);
    const size_t end = (cr == std::string::npos) ? text.size() : cr;
    return text.substr(begin, end - begin);
}

// ===================================================================================================
// Signals and an independent BS.1770 loudness meter (does not use the plug-in's DSP)
// ===================================================================================================

constexpr double kPi = 3.14159265358979323846;

void FillSine(float* p, uint32_t frames, uint32_t ch, double rate, double amp, double freq, uint64_t startFrame) {
    for (uint32_t f = 0; f < frames; ++f) {
        const float v = static_cast<float>(amp * std::sin(2.0 * kPi * freq * static_cast<double>(startFrame + f) / rate));
        for (uint32_t c = 0; c < ch; ++c) p[static_cast<size_t>(f) * ch + c] = v;
    }
}

bool AllFinite(const float* p, size_t n) {
    for (size_t i = 0; i < n; ++i)
        if (!std::isfinite(p[i])) return false;
    return true;
}

double MaxAbs(const float* p, size_t n) {
    double m = 0.0;
    for (size_t i = 0; i < n; ++i) m = std::max(m, static_cast<double>(std::fabs(p[i])));
    return m;
}

double SumSquares(const float* p, size_t n) {
    double s = 0.0;
    for (size_t i = 0; i < n; ++i) s += static_cast<double>(p[i]) * static_cast<double>(p[i]);
    return s;
}

double MaxDiff(const float* a, const float* b, size_t n) {
    double m = 0.0;
    for (size_t i = 0; i < n; ++i) m = std::max(m, static_cast<double>(std::fabs(a[i] - b[i])));
    return m;
}

std::vector<double> ChannelWeights(uint32_t mask, uint32_t ch) {
    std::vector<double> w(ch, 1.0);
    uint32_t idx = 0;
    for (uint32_t bit = 0; bit < 32 && idx < ch; ++bit) {
        const uint32_t m = 1u << bit;
        if (!(mask & m)) continue;
        double g = 1.0;
        if (m == 0x8) g = 0.0;                                                    // LFE is not measured
        else if (m == 0x10 || m == 0x20 || m == 0x200 || m == 0x400) g = 1.41;    // back and side: +1.5 dB
        w[idx++] = g;
    }
    return w;
}

class HostMeter {
public:
    HostMeter(uint32_t rate, uint32_t ch, uint32_t mask)
        : ch_(ch), w_(ChannelWeights(mask, ch)), zs_(ch * 2, 0.0), zh_(ch * 2, 0.0), acc_(ch, 0.0) {
        const double fs = static_cast<double>(rate);
        {
            const double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
            const double k = std::tan(kPi * f0 / fs);
            const double vh = std::pow(10.0, g / 20.0), vb = std::pow(vh, 0.4996667741545416);
            const double a0 = 1.0 + k / q + k * k;
            sb0_ = (vh + vb * k / q + k * k) / a0;
            sb1_ = 2.0 * (k * k - vh) / a0;
            sb2_ = (vh - vb * k / q + k * k) / a0;
            sa1_ = 2.0 * (k * k - 1.0) / a0;
            sa2_ = (1.0 - k / q + k * k) / a0;
        }
        {
            const double f0 = 38.13547087602444, q = 0.5003270373238773;
            const double k = std::tan(kPi * f0 / fs);
            const double a0 = 1.0 + k / q + k * k;
            ha1_ = 2.0 * (k * k - 1.0) / a0;
            ha2_ = (1.0 - k / q + k * k) / a0;
        }
    }

    // Runs the filters over the block. Energy counts only while accumulate is true.
    void Add(const float* x, uint32_t frames, bool accumulate) {
        for (uint32_t f = 0; f < frames; ++f) {
            for (uint32_t c = 0; c < ch_; ++c) {
                const double in = static_cast<double>(x[static_cast<size_t>(f) * ch_ + c]);
                double* s = &zs_[c * 2];
                const double y1 = sb0_ * in + s[0];
                s[0] = sb1_ * in - sa1_ * y1 + s[1];
                s[1] = sb2_ * in - sa2_ * y1;
                double* h = &zh_[c * 2];
                const double y2 = y1 + h[0];
                h[0] = -2.0 * y1 - ha1_ * y2 + h[1];
                h[1] = y1 - ha2_ * y2;
                if (accumulate) acc_[c] += y2 * y2;
            }
        }
        if (accumulate) frames_ += frames;
    }

    double Lufs() const {
        if (frames_ == 0) return -120.0;
        double sum = 0.0;
        for (uint32_t c = 0; c < ch_; ++c) sum += w_[c] * acc_[c] / static_cast<double>(frames_);
        if (!(sum > 1e-12)) return -120.0;
        return -0.691 + 10.0 * std::log10(sum);
    }

private:
    uint32_t ch_;
    std::vector<double> w_, zs_, zh_, acc_;
    double sb0_ = 1, sb1_ = 0, sb2_ = 0, sa1_ = 0, sa2_ = 0, ha1_ = 0, ha2_ = 0;
    uint64_t frames_ = 0;
};

// ===================================================================================================
// The DLL
// ===================================================================================================

typedef HRESULT(STDAPICALLTYPE* PFN_GetClassObject)(REFCLSID, REFIID, LPVOID*);
typedef HRESULT(STDAPICALLTYPE* PFN_CanUnloadNow)(void);

HMODULE g_mod = nullptr;
PFN_GetClassObject g_getClassObject = nullptr;
PFN_CanUnloadNow g_canUnload = nullptr;

std::wstring DllFileName() {
    const size_t slash = g_dllPath.find_last_of(L"\\/");
    return slash == std::wstring::npos ? g_dllPath : g_dllPath.substr(slash + 1);
}

bool LoadDll(std::string& why) {
    g_mod = LoadLibraryW(g_dllPath.c_str());
    if (!g_mod) {
        why = Str("LoadLibrary failed, error %lu", GetLastError());
        return false;
    }
    g_getClassObject = reinterpret_cast<PFN_GetClassObject>(GetProcAddress(g_mod, "DllGetClassObject"));
    g_canUnload = reinterpret_cast<PFN_CanUnloadNow>(GetProcAddress(g_mod, "DllCanUnloadNow"));
    if (!g_getClassObject || !g_canUnload) {
        why = "DllGetClassObject or DllCanUnloadNow is not exported";
        return false;
    }
    return true;
}

// Unloads the DLL and proves that it is really gone (no worker thread or leaked reference keeps it pinned).
bool UnloadDll(std::string& why) {
    if (!g_mod) return true;
    g_getClassObject = nullptr;
    g_canUnload = nullptr;
    const BOOL ok = FreeLibrary(g_mod);
    g_mod = nullptr;
    if (!ok) {
        why = Str("FreeLibrary failed, error %lu", GetLastError());
        return false;
    }
    if (GetModuleHandleW(DllFileName().c_str()) != nullptr) {
        why = "the DLL is still loaded after FreeLibrary (something holds a reference to it)";
        return false;
    }
    return true;
}

// A fresh copy of the DLL: a new runtime with no safe-mode latch and no log-once memory. No object may be alive.
bool ReloadDll(std::string& why) { return UnloadDll(why) && LoadDll(why); }

// ===================================================================================================
// Aggregation test object, media types, and a wrapper around one plug-in object
// ===================================================================================================

// An outer object that forwards QueryInterface for everything except IUnknown to the inner non-delegating unknown.
class TestOuter : public IUnknown {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (IsEqualIID(riid, __uuidof(IUnknown))) {
            *ppv = static_cast<IUnknown*>(this);
            AddRef();
            return S_OK;
        }
        if (!inner) return E_NOINTERFACE;
        return inner->QueryInterface(riid, ppv);
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&refs_)); }
    ULONG STDMETHODCALLTYPE Release() override {
        const LONG r = InterlockedDecrement(&refs_);
        if (r == 0) {
            IUnknown* i = inner;
            inner = nullptr;
            if (i) i->Release();
            delete this;
        }
        return static_cast<ULONG>(r);
    }
    IUnknown* inner = nullptr;  // the object's non-delegating IUnknown (one reference, released with the outer)

private:
    LONG refs_ = 1;
};

IAudioMediaType* MakeType(const GUID& sub, DWORD ch, DWORD bytes, DWORD bits, float rate, DWORD mask) {
    UNCOMPRESSEDAUDIOFORMAT f{};
    f.guidFormatType = sub;
    f.dwSamplesPerFrame = ch;
    f.dwBytesPerSampleContainer = bytes;
    f.dwValidBitsPerSample = bits;
    f.fFramesPerSecond = rate;
    f.dwChannelMask = mask;
    IAudioMediaType* t = nullptr;
    if (FAILED(CreateAudioMediaTypeFromUncompressedAudioFormat(&f, &t))) return nullptr;
    return t;
}

struct Format {
    uint32_t rate;
    uint32_t ch;
    uint32_t mask;
};

const GUID kClsid[2] = { sp::kClsidLeveler, sp::kClsidSafetyCap };
const char* const kName[2] = { "Leveler", "SafetyCap" };

struct Period {
    uint32_t flags = 0;
    uint32_t frames = 0;
};

struct Apo {
    Apo() = default;
    Apo(const Apo&) = delete;
    Apo& operator=(const Apo&) = delete;
    ~Apo() { Cleanup(); }

    TestOuter* outer = nullptr;
    IUnknown* unk = nullptr;  // identity: the object's non-delegating IUnknown
    IAudioProcessingObject* apo = nullptr;
    IAudioProcessingObjectRT* rt = nullptr;
    IAudioProcessingObjectConfiguration* cfg = nullptr;
    IAudioSystemEffects2* fx = nullptr;
    bool locked = false;
    bool inPlace = true;
    uint32_t channels = 0, rate = 0, maxFrames = 0;
    IAudioMediaType* type = nullptr;
    std::vector<float> a, b;  // a: input (and output when in place), b: separate output
    APO_CONNECTION_DESCRIPTOR dIn{}, dOut{};
    APO_CONNECTION_PROPERTY pIn{}, pOut{};

    float* In() { return a.data(); }
    float* Out() { return inPlace ? a.data() : b.data(); }

    bool Create(const GUID& clsid, bool aggregated, std::string& why) {
        if (!g_getClassObject) {
            why = "the DLL is not loaded";
            return false;
        }
        IClassFactory* f = nullptr;
        HRESULT hr = g_getClassObject(clsid, __uuidof(IClassFactory), reinterpret_cast<void**>(&f));
        if (FAILED(hr) || !f) {
            why = "DllGetClassObject failed " + HrText(hr);
            return false;
        }
        IUnknown* from = nullptr;
        if (aggregated) {
            outer = new TestOuter();
            hr = f->CreateInstance(outer, __uuidof(IUnknown), reinterpret_cast<void**>(&outer->inner));
            unk = outer->inner;
            from = outer;
        } else {
            hr = f->CreateInstance(nullptr, __uuidof(IUnknown), reinterpret_cast<void**>(&unk));
            from = unk;
        }
        f->Release();
        if (FAILED(hr) || !unk) {
            why = Str("CreateInstance(%s, IUnknown) failed %s", aggregated ? "aggregated" : "plain", HrText(hr).c_str());
            if (outer) {
                outer->inner = nullptr;
                outer->Release();
                outer = nullptr;
            }
            unk = nullptr;
            return false;
        }
        struct Q {
            const IID& iid;
            void** dst;
        } list[] = {
            { __uuidof(IAudioProcessingObject), reinterpret_cast<void**>(&apo) },
            { __uuidof(IAudioProcessingObjectRT), reinterpret_cast<void**>(&rt) },
            { __uuidof(IAudioProcessingObjectConfiguration), reinterpret_cast<void**>(&cfg) },
            { __uuidof(IAudioSystemEffects2), reinterpret_cast<void**>(&fx) },
        };
        for (const Q& q : list) {
            hr = from->QueryInterface(q.iid, q.dst);
            if (FAILED(hr) || !*q.dst) {
                why = "QueryInterface failed " + HrText(hr);
                return false;
            }
        }
        return true;
    }

    HRESULT Init(const GUID& clsid, const GUID& mode) {
        APOInitSystemEffects2 d{};
        d.APOInit.cbSize = sizeof(d);
        d.APOInit.clsid = clsid;
        d.AudioProcessingMode = mode;
        d.InitializeForDiscoveryOnly = FALSE;
        return apo->Initialize(sizeof(d), reinterpret_cast<BYTE*>(&d));
    }

    HRESULT Lock(const Format& f, bool inPlaceMode, uint32_t maxFrm) {
        if (type) {
            type->Release();
            type = nullptr;
        }
        type = MakeType(KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, f.ch, 4, 32, static_cast<float>(f.rate), f.mask);
        if (!type) return E_FAIL;
        channels = f.ch;
        rate = f.rate;
        maxFrames = maxFrm;
        inPlace = inPlaceMode;
        const size_t n = static_cast<size_t>(maxFrm) * f.ch;
        a.assign(n, 0.0f);
        b.assign(inPlace ? 0 : n, 0.0f);
        dIn = APO_CONNECTION_DESCRIPTOR{};
        dOut = APO_CONNECTION_DESCRIPTOR{};
        dIn.Type = APO_CONNECTION_BUFFER_TYPE_EXTERNAL;
        dIn.pBuffer = reinterpret_cast<UINT_PTR>(a.data());
        dIn.u32MaxFrameCount = maxFrm;
        dIn.pFormat = type;
        dIn.u32Signature = APO_CONNECTION_DESCRIPTOR_SIGNATURE;
        dOut.Type = inPlace ? APO_CONNECTION_BUFFER_TYPE_DEPENDANT : APO_CONNECTION_BUFFER_TYPE_EXTERNAL;
        dOut.pBuffer = reinterpret_cast<UINT_PTR>(Out());
        dOut.u32MaxFrameCount = maxFrm;
        dOut.pFormat = type;
        dOut.u32Signature = APO_CONNECTION_DESCRIPTOR_SIGNATURE;
        APO_CONNECTION_DESCRIPTOR* pi = &dIn;
        APO_CONNECTION_DESCRIPTOR* po = &dOut;
        const HRESULT hr = cfg->LockForProcess(1, &pi, 1, &po);
        locked = SUCCEEDED(hr);
        return hr;
    }

    HRESULT Unlock() {
        const HRESULT hr = cfg->UnlockForProcess();
        locked = false;
        return hr;
    }

    // Processes one period. The caller fills In() first (frames * channels samples).
    Period Process(uint32_t frames, APO_BUFFER_FLAGS flags) {
        pIn = APO_CONNECTION_PROPERTY{};
        pOut = APO_CONNECTION_PROPERTY{};
        pIn.pBuffer = reinterpret_cast<UINT_PTR>(a.data());
        pIn.u32ValidFrameCount = frames;
        pIn.u32BufferFlags = flags;
        pIn.u32Signature = APO_CONNECTION_PROPERTY_SIGNATURE;
        pOut.pBuffer = reinterpret_cast<UINT_PTR>(Out());
        pOut.u32ValidFrameCount = 0;
        pOut.u32BufferFlags = BUFFER_INVALID;
        pOut.u32Signature = APO_CONNECTION_PROPERTY_SIGNATURE;
        APO_CONNECTION_PROPERTY* ppi = &pIn;
        APO_CONNECTION_PROPERTY* ppo = &pOut;
        rt->APOProcess(1, &ppi, 1, &ppo);
        Period r;
        r.flags = static_cast<uint32_t>(pOut.u32BufferFlags);
        r.frames = pOut.u32ValidFrameCount;
        return r;
    }

    void Cleanup() {
        if (locked && cfg) cfg->UnlockForProcess();
        locked = false;
        if (fx) fx->Release();
        if (cfg) cfg->Release();
        if (rt) rt->Release();
        if (apo) apo->Release();
        fx = nullptr;
        cfg = nullptr;
        rt = nullptr;
        apo = nullptr;
        if (outer) {
            outer->Release();  // releases the inner unknown
            outer = nullptr;
        } else if (unk) {
            unk->Release();
        }
        unk = nullptr;
        if (type) {
            type->Release();
            type = nullptr;
        }
    }
};

const GUID kModeDefault = AUDIO_SIGNALPROCESSINGMODE_DEFAULT;

// Creates, initialises (mode Default) and locks one object. Returns false with a reason on any failure. Like the
// brief's set-up it creates through the test outer object (aggregated); H1, H5 and H6 also run the plain path.
bool Bring(Apo& a, int kind, const Format& f, bool inPlace, uint32_t maxFrames, std::string& why, bool aggregated = true) {
    if (!a.Create(kClsid[kind], aggregated, why)) return false;
    HRESULT hr = a.Init(kClsid[kind], kModeDefault);
    if (FAILED(hr)) {
        why = "Initialize failed " + HrText(hr);
        return false;
    }
    hr = a.Lock(f, inPlace, maxFrames);
    if (FAILED(hr)) {
        why = "LockForProcess failed " + HrText(hr);
        return false;
    }
    return true;
}

double CeilingLin(double ceilingDb) { return std::pow(10.0, ceilingDb / 20.0); }

// Runs 'periods' periods of a sine through a locked, in-place object. Measures the gain over the last 'lastN'
// periods. Returns false when an output is not finite or a period came back malformed.
struct ToneRun {
    bool finite = true;
    bool wellFormed = true;
    double maxOut = 0.0;
    double gainDb = 0.0;
};

ToneRun RunTone(Apo& a, uint32_t periods, uint32_t lastN, double amp, uint64_t& pos) {
    ToneRun r;
    double sIn = 0.0, sOut = 0.0;
    const uint32_t frames = 480;
    const size_t n = static_cast<size_t>(frames) * a.channels;
    for (uint32_t p = 0; p < periods; ++p) {
        FillSine(a.In(), frames, a.channels, static_cast<double>(a.rate), amp, 1000.0, pos);
        const double eIn = SumSquares(a.In(), n);
        const Period res = a.Process(frames, BUFFER_VALID);
        if (res.flags != static_cast<uint32_t>(BUFFER_VALID) || res.frames != frames) r.wellFormed = false;
        if (!AllFinite(a.Out(), n)) r.finite = false;
        r.maxOut = std::max(r.maxOut, MaxAbs(a.Out(), n));
        if (p + lastN >= periods) {
            sIn += eIn;
            sOut += SumSquares(a.Out(), n);
        }
        pos += frames;
    }
    r.gainDb = (sIn > 0.0 && sOut > 0.0) ? 10.0 * std::log10(sOut / sIn) : -200.0;
    return r;
}

// Feeds zeros until nothing of an earlier sound is left inside the DSP (the output is not looked at). Four periods
// of 480 frames are far more than the Safety Cap's look-ahead (248 frames at 48 kHz). Needs maxFrames >= 480.
void Drain(Apo& a) {
    const size_t n = static_cast<size_t>(480) * a.channels;
    for (int p = 0; p < 4; ++p) {
        std::memset(a.In(), 0, n * sizeof(float));
        a.Process(480, BUFFER_VALID);
    }
}

// ===================================================================================================
// H1  Aggregation
// ===================================================================================================

void CheckH1(Check& c) {
    for (int k = 0; k < 2; ++k) {
        const char* nm = kName[k];
        {
            Apo a;
            std::string why;
            if (!a.Create(kClsid[k], true, why)) {
                c.Fail(Str("%s: aggregated create: %s", nm, why.c_str()));
                continue;
            }
            IUnknown* ident = nullptr;
            const HRESULT hr = a.apo->QueryInterface(__uuidof(IUnknown), reinterpret_cast<void**>(&ident));
            c.Expect(SUCCEEDED(hr) && ident == static_cast<IUnknown*>(a.outer),
                     Str("%s: QueryInterface(IUnknown) on an interface of an aggregated object did not return the outer unknown", nm));
            if (ident) ident->Release();
            c.Expect(a.unk != static_cast<IUnknown*>(a.outer), Str("%s: the inner unknown must differ from the outer unknown", nm));
            c.Expect(g_canUnload() == S_FALSE, Str("%s: DllCanUnloadNow must be S_FALSE while an object is alive", nm));
        }
        c.Expect(g_canUnload() == S_OK, Str("%s: DllCanUnloadNow is not S_OK after releasing the aggregated object", nm));

        {
            Apo a;
            std::string why;
            if (!a.Create(kClsid[k], false, why)) {
                c.Fail(Str("%s: plain create: %s", nm, why.c_str()));
                continue;
            }
            const ULONG r1 = a.apo->AddRef();
            const ULONG r2 = a.apo->Release();
            c.Expect(r1 == r2 + 1, Str("%s: AddRef returned %lu, then Release returned %lu", nm, r1, r2));
            IAudioProcessingObject* again = nullptr;
            const HRESULT hr = a.fx->QueryInterface(__uuidof(IAudioProcessingObject), reinterpret_cast<void**>(&again));
            c.Expect(SUCCEEDED(hr) && again == a.apo, Str("%s: QueryInterface from IAudioSystemEffects2 gave a different IAudioProcessingObject", nm));
            if (again) again->Release();
            IAudioSystemEffects* fx1 = nullptr;
            c.Expect(SUCCEEDED(a.apo->QueryInterface(__uuidof(IAudioSystemEffects), reinterpret_cast<void**>(&fx1))) && fx1,
                     Str("%s: IAudioSystemEffects is not served", nm));
            if (fx1) fx1->Release();
            IUnknown* bogus = nullptr;
            c.Expect(a.apo->QueryInterface(__uuidof(IAudioMediaType), reinterpret_cast<void**>(&bogus)) == E_NOINTERFACE && !bogus,
                     Str("%s: an interface the object does not implement must give E_NOINTERFACE", nm));
        }
        c.Expect(g_canUnload() == S_OK, Str("%s: DllCanUnloadNow is not S_OK after releasing the plain object", nm));

        {
            IClassFactory* f = nullptr;
            if (SUCCEEDED(g_getClassObject(kClsid[k], __uuidof(IClassFactory), reinterpret_cast<void**>(&f))) && f) {
                TestOuter* o = new TestOuter();
                void* p = reinterpret_cast<void*>(1);
                const HRESULT hr = f->CreateInstance(o, __uuidof(IAudioProcessingObject), &p);
                c.Expect(hr == CLASS_E_NOAGGREGATION && p == nullptr,
                         Str("%s: aggregated creation asking for IAudioProcessingObject returned %s (want CLASS_E_NOAGGREGATION)", nm, HrText(hr).c_str()));
                if (SUCCEEDED(hr) && p) static_cast<IUnknown*>(p)->Release();
                o->Release();
                f->Release();
            } else {
                c.Fail(Str("%s: no class factory", nm));
            }
        }

        {
            IClassFactory* f1 = nullptr;
            IClassFactory* f2 = nullptr;
            if (SUCCEEDED(g_getClassObject(kClsid[k], __uuidof(IClassFactory), reinterpret_cast<void**>(&f1))) && f1 &&
                SUCCEEDED(g_getClassObject(kClsid[k], __uuidof(IClassFactory), reinterpret_cast<void**>(&f2))) && f2) {
                f1->LockServer(TRUE);
                f1->Release();
                c.Expect(g_canUnload() == S_FALSE, Str("%s: DllCanUnloadNow must be S_FALSE while the server is locked", nm));
                f2->LockServer(FALSE);
                f2->Release();
            } else {
                c.Fail(Str("%s: no class factory for the lock test", nm));
                if (f1) f1->Release();
                if (f2) f2->Release();
            }
        }
        c.Expect(g_canUnload() == S_OK, Str("%s: DllCanUnloadNow is not S_OK after the server lock was released", nm));
    }

    {
        IClassFactory* f = nullptr;
        GUID unknown = sp::kClsidLeveler;
        unknown.Data1 ^= 0x5A5A5A5Au;
        const HRESULT hr = g_getClassObject(unknown, __uuidof(IClassFactory), reinterpret_cast<void**>(&f));
        c.Expect(hr == CLASS_E_CLASSNOTAVAILABLE && !f, "an unknown CLSID must give CLASS_E_CLASSNOTAVAILABLE, got " + HrText(hr));
        if (f) f->Release();
        void* p = nullptr;
        const HRESULT hr2 = g_getClassObject(sp::kClsidLeveler, __uuidof(IAudioProcessingObject), &p);
        c.Expect(FAILED(hr2) && !p, "DllGetClassObject for a non-factory interface must fail, got " + HrText(hr2));
        if (SUCCEEDED(hr2) && p) static_cast<IUnknown*>(p)->Release();
    }
    c.Expect(g_canUnload() == S_OK, "DllCanUnloadNow is not S_OK at the end of H1");
}

// ===================================================================================================
// H2  Registration properties
// ===================================================================================================

void CheckH2(Check& c) {
    for (int k = 0; k < 2; ++k) {
        const char* nm = kName[k];
        Apo a;
        std::string why;
        if (!a.Create(kClsid[k], true, why)) {
            c.Fail(Str("%s: create: %s", nm, why.c_str()));
            continue;
        }
        APO_REG_PROPERTIES* p = nullptr;
        const HRESULT hr = a.apo->GetRegistrationProperties(&p);
        if (FAILED(hr) || !p) {
            c.Fail(Str("%s: GetRegistrationProperties failed %s", nm, HrText(hr).c_str()));
            continue;
        }
        c.Expect(p->Flags == 0xF, Str("%s: Flags 0x%X (want 0xF)", nm, static_cast<unsigned>(p->Flags)));
        c.Expect(p->u32MaxInstances == 0xFFFFFFFFu, Str("%s: u32MaxInstances 0x%X (want 0xFFFFFFFF)", nm, p->u32MaxInstances));
        c.Expect(IsEqualCLSID(p->clsid, kClsid[k]), Str("%s: wrong CLSID", nm));
        c.Expect(p->u32MinInputConnections == 1 && p->u32MaxInputConnections == 1 && p->u32MinOutputConnections == 1 &&
                     p->u32MaxOutputConnections == 1,
                 Str("%s: connection counts are not 1/1/1/1", nm));
        c.Expect(p->u32MajorVersion == 1 && p->u32MinorVersion == 0, Str("%s: version is not 1.0", nm));
        c.Expect(p->u32NumAPOInterfaces == 1 && IsEqualIID(p->iidAPOInterfaceList[0], __uuidof(IAudioProcessingObject)),
                 Str("%s: interface list is not exactly IAudioProcessingObject", nm));
        const wchar_t* want = (k == 0) ? SP_LEVELER_FRIENDLY_NAME : SP_SAFETYCAP_FRIENDLY_NAME;
        c.Expect(wcscmp(p->szFriendlyName, want) == 0, Str("%s: friendly name differs", nm));
        c.Expect(wcscmp(p->szCopyrightInfo, L"Sound Protection (personal build)") == 0, Str("%s: copyright text differs", nm));
        CoTaskMemFree(p);
    }
    // The registration entry points must exist for regsvr32. They are only looked up here, never called.
    c.Expect(g_mod && GetProcAddress(g_mod, "DllRegisterServer") != nullptr, "DllRegisterServer is not exported");
    c.Expect(g_mod && GetProcAddress(g_mod, "DllUnregisterServer") != nullptr, "DllUnregisterServer is not exported");
}

// ===================================================================================================
// H3  Initialize
// ===================================================================================================

void CheckH3(Check& c) {
    for (int k = 0; k < 2; ++k) {
        const char* nm = kName[k];
        std::string why;
        {
            Apo a;
            if (!a.Create(kClsid[k], true, why)) {
                c.Fail(Str("%s: create: %s", nm, why.c_str()));
                continue;
            }
            const HRESULT hr = a.Init(kClsid[k], kModeDefault);
            c.Expect(hr == S_OK, Str("%s: Initialize(APOInitSystemEffects2, null stores, Default) returned %s", nm, HrText(hr).c_str()));
        }
        {
            Apo a;
            if (!a.Create(kClsid[k], true, why)) continue;
            APOInitSystemEffects2 d{};
            d.APOInit.cbSize = sizeof(d);
            d.APOInit.clsid = kClsid[k];
            d.AudioProcessingMode = kModeDefault;
            const HRESULT h0 = a.apo->Initialize(0, reinterpret_cast<BYTE*>(&d));
            c.Expect(FAILED(h0), Str("%s: Initialize(cb = 0) returned %s", nm, HrText(h0).c_str()));
            const HRESULT h1 = a.apo->Initialize(sizeof(d), nullptr);
            c.Expect(FAILED(h1), Str("%s: Initialize(data = null) returned %s", nm, HrText(h1).c_str()));
            const HRESULT h2 = a.apo->Initialize(static_cast<UINT32>(sizeof(APOInitBaseStruct)) - 1, reinterpret_cast<BYTE*>(&d));
            c.Expect(FAILED(h2), Str("%s: Initialize(cb smaller than APOInitBaseStruct) returned %s", nm, HrText(h2).c_str()));
            // The failed calls must not have marked the object as initialised: locking has to be refused.
            const HRESULT hl = a.Lock(Format{ 48000, 2, 3 }, true, 480);
            c.Expect(FAILED(hl), Str("%s: LockForProcess succeeded on an object whose Initialize only failed", nm));
            if (SUCCEEDED(hl)) a.Unlock();
        }
        {
            Apo a;
            if (!a.Create(kClsid[k], true, why)) continue;
            APOInitSystemEffects3 d{};
            d.APOInit.cbSize = sizeof(d);
            d.APOInit.clsid = kClsid[k];
            d.AudioProcessingMode = kModeDefault;
            const HRESULT hr = a.apo->Initialize(sizeof(d), reinterpret_cast<BYTE*>(&d));
            c.Expect(hr == S_OK, Str("%s: Initialize(APOInitSystemEffects3) returned %s", nm, HrText(hr).c_str()));
        }
        {
            Apo a;
            if (!a.Create(kClsid[k], true, why)) continue;
            APOInitBaseStruct d{};
            d.cbSize = sizeof(d);
            d.clsid = kClsid[k];
            const HRESULT hr = a.apo->Initialize(sizeof(d), reinterpret_cast<BYTE*>(&d));
            c.Expect(hr == S_OK, Str("%s: Initialize(APOInitBaseStruct only) returned %s", nm, HrText(hr).c_str()));
        }
    }
}

// ===================================================================================================
// H4  Formats
// ===================================================================================================

bool ExpectFloat32(const IAudioMediaType* t, uint32_t ch, double rate, std::string& what) {
    UNCOMPRESSEDAUDIOFORMAT f{};
    if (!t || FAILED(const_cast<IAudioMediaType*>(t)->GetUncompressedAudioFormat(&f))) {
        what = "suggested type cannot be read";
        return false;
    }
    const bool ok = IsEqualGUID(f.guidFormatType, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT) && f.dwSamplesPerFrame == ch &&
                    f.dwBytesPerSampleContainer == 4 && f.dwValidBitsPerSample == 32 && std::fabs(static_cast<double>(f.fFramesPerSecond) - rate) < 0.5;
    if (!ok) what = Str("suggested %lu ch, %.0f Hz, %lu bits in %lu bytes", f.dwSamplesPerFrame, static_cast<double>(f.fFramesPerSecond), f.dwValidBitsPerSample, f.dwBytesPerSampleContainer);
    return ok;
}

void CheckH4(Check& c) {
    for (int k = 0; k < 2; ++k) {
        const char* nm = kName[k];
        Apo a;
        std::string why;
        if (!a.Create(kClsid[k], true, why)) {
            c.Fail(Str("%s: create: %s", nm, why.c_str()));
            continue;
        }
        a.Init(kClsid[k], kModeDefault);
        IAudioMediaType* f32 = MakeType(KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, 2, 4, 32, 48000.0f, 3);
        IAudioMediaType* i16 = MakeType(KSDATAFORMAT_SUBTYPE_PCM, 2, 2, 16, 48000.0f, 3);
        IAudioMediaType* f32b = MakeType(KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, 2, 4, 32, 44100.0f, 3);
        IAudioMediaType* slow = MakeType(KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, 2, 4, 32, 4000.0f, 3);
        if (!f32 || !i16 || !f32b || !slow) {
            c.Fail("cannot build media types for the format checks");
            if (f32) f32->Release();
            if (i16) i16->Release();
            if (f32b) f32b->Release();
            if (slow) slow->Release();
            continue;
        }
        for (int dir = 0; dir < 2; ++dir) {
            const char* dn = dir == 0 ? "IsInputFormatSupported" : "IsOutputFormatSupported";
            auto query = [&](IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** out) {
                return dir == 0 ? a.apo->IsInputFormatSupported(opposite, requested, out) : a.apo->IsOutputFormatSupported(opposite, requested, out);
            };
            IAudioMediaType* sup = nullptr;
            HRESULT hr = query(nullptr, f32, &sup);
            c.Expect(hr == S_OK && sup == f32, Str("%s %s: float32 48 kHz stereo gave %s (want S_OK and the same type back)", nm, dn, HrText(hr).c_str()));
            if (sup) sup->Release();

            sup = nullptr;
            hr = query(nullptr, i16, &sup);
            std::string what;
            c.Expect(hr == S_FALSE, Str("%s %s: int16 48 kHz stereo gave %s (want S_FALSE)", nm, dn, HrText(hr).c_str()));
            c.Expect(sup != nullptr && ExpectFloat32(sup, 2, 48000.0, what), Str("%s %s: int16 suggestion: %s", nm, dn, what.empty() ? "none" : what.c_str()));
            if (sup) sup->Release();

            sup = nullptr;
            what.clear();
            hr = query(f32, f32b, &sup);
            c.Expect(hr == S_FALSE, Str("%s %s: 44.1 kHz against a 48 kHz opposite gave %s (want S_FALSE)", nm, dn, HrText(hr).c_str()));
            c.Expect(sup != nullptr && ExpectFloat32(sup, 2, 48000.0, what), Str("%s %s: rate-mismatch suggestion: %s", nm, dn, what.empty() ? "none" : what.c_str()));
            if (sup) sup->Release();

            sup = nullptr;
            what.clear();
            hr = query(nullptr, slow, &sup);
            c.Expect(hr == S_FALSE, Str("%s %s: 4 kHz gave %s (want S_FALSE)", nm, dn, HrText(hr).c_str()));
            c.Expect(sup != nullptr && ExpectFloat32(sup, 2, 8000.0, what), Str("%s %s: 4 kHz suggestion: %s", nm, dn, what.empty() ? "none" : what.c_str()));
            if (sup) sup->Release();

            sup = nullptr;
            hr = query(nullptr, nullptr, &sup);
            c.Expect(FAILED(hr), Str("%s %s: a null requested format returned %s", nm, dn, HrText(hr).c_str()));
            if (sup) sup->Release();
        }
        f32->Release();
        i16->Release();
        f32b->Release();
        slow->Release();
    }
}

// ===================================================================================================
// H5  Lock and process across formats, with re-lock in between
// ===================================================================================================

// One run of H5 with objects created plain or through the outer object. Returns true when the loudness part ran.
bool CheckH5Once(Check& c, bool aggregated) {
    Apo lev, cap;
    std::string why;
    if (!lev.Create(sp::kClsidLeveler, aggregated, why) || !cap.Create(sp::kClsidSafetyCap, aggregated, why)) {
        c.Fail("create: " + why);
        return false;
    }
    if (FAILED(lev.Init(sp::kClsidLeveler, kModeDefault)) || FAILED(cap.Init(sp::kClsidSafetyCap, kModeDefault))) {
        c.Fail("Initialize failed");
        return false;
    }
    const Format formats[] = { { 48000, 2, 3 }, { 44100, 1, 4 }, { 96000, 8, 0x63F }, { 48000, 2, 3 } };
    const double ceilLin = CeilingLin(-8.0);
    bool loudnessChecked = false;
    for (const Format& f : formats) {
        const std::string fn = Str("%u Hz, %u ch, mask 0x%X", f.rate, f.ch, f.mask);
        HRESULT hr = lev.Lock(f, true, 480);
        if (FAILED(hr)) {
            c.Fail(Str("Leveler lock (%s) failed %s", fn.c_str(), HrText(hr).c_str()));
            continue;
        }
        hr = cap.Lock(f, true, 480);
        if (FAILED(hr)) {
            c.Fail(Str("SafetyCap lock (%s) failed %s", fn.c_str(), HrText(hr).c_str()));
            lev.Unlock();
            continue;
        }
        const uint32_t periods = (3 * f.rate + 479) / 480;
        const uint32_t windowStart = (2 * f.rate + 479) / 480;
        HostMeter meter(f.rate, f.ch, f.mask);
        const size_t n = static_cast<size_t>(480) * f.ch;
        double capMax = 0.0, levMax = 0.0;
        bool finite = true, wellFormed = true;
        uint64_t pos = 0;
        for (uint32_t p = 0; p < periods; ++p) {
            FillSine(lev.In(), 480, f.ch, f.rate, 0.708, 1000.0, pos);
            Period r = lev.Process(480, BUFFER_VALID);
            wellFormed = wellFormed && r.flags == static_cast<uint32_t>(BUFFER_VALID) && r.frames == 480;
            finite = finite && AllFinite(lev.Out(), n);
            levMax = std::max(levMax, MaxAbs(lev.Out(), n));
            meter.Add(lev.Out(), 480, p >= windowStart);

            FillSine(cap.In(), 480, f.ch, f.rate, 0.708, 1000.0, pos);
            r = cap.Process(480, BUFFER_VALID);
            wellFormed = wellFormed && r.flags == static_cast<uint32_t>(BUFFER_VALID) && r.frames == 480;
            finite = finite && AllFinite(cap.Out(), n);
            capMax = std::max(capMax, MaxAbs(cap.Out(), n));
            pos += 480;
        }
        c.Expect(wellFormed, Str("(%s) a period came back without BUFFER_VALID or with the wrong frame count", fn.c_str()));
        c.Expect(finite, Str("(%s) a non-finite output sample", fn.c_str()));
        c.Expect(capMax <= ceilLin + 1e-6, Str("(%s) SafetyCap output peak %.6f is above the ceiling %.6f", fn.c_str(), capMax, ceilLin));
        c.Expect(levMax <= 1.0 + 1e-6, Str("(%s) Leveler output peak %.6f is above 1.0", fn.c_str(), levMax));
        if (!kStub) {
            const double lufs = meter.Lufs();
            loudnessChecked = true;
            c.Expect(std::fabs(lufs + 20.0) <= 1.5, Str("(%s) Leveler output loudness %.2f LUFS (want -20 +- 1.5)", fn.c_str(), lufs));
        }
        lev.Unlock();
        cap.Unlock();
    }

    // A lock that is refused must leave the object unlocked and usable.
    {
        Apo bad;
        if (bad.Create(sp::kClsidLeveler, aggregated, why) && SUCCEEDED(bad.Init(sp::kClsidLeveler, kModeDefault))) {
            IAudioMediaType* i16 = MakeType(KSDATAFORMAT_SUBTYPE_PCM, 2, 2, 16, 48000.0f, 3);
            if (i16) {
                std::vector<int16_t> buf(480 * 2, 0);
                APO_CONNECTION_DESCRIPTOR d{};
                d.Type = APO_CONNECTION_BUFFER_TYPE_EXTERNAL;
                d.pBuffer = reinterpret_cast<UINT_PTR>(buf.data());
                d.u32MaxFrameCount = 480;
                d.pFormat = i16;
                d.u32Signature = APO_CONNECTION_DESCRIPTOR_SIGNATURE;
                APO_CONNECTION_DESCRIPTOR* pd = &d;
                const HRESULT hr = bad.cfg->LockForProcess(1, &pd, 1, &pd);
                c.Expect(FAILED(hr), "LockForProcess accepted an int16 connection");
                if (SUCCEEDED(hr)) bad.cfg->UnlockForProcess();
                i16->Release();
            }
            const HRESULT hr2 = bad.Lock(Format{ 48000, 2, 3 }, true, 480);
            c.Expect(SUCCEEDED(hr2), "a good lock after a refused lock failed " + HrText(hr2));
        }
    }
    return loudnessChecked;
}

// Both ways: plain objects (CreateInstance with no outer) and objects aggregated under the test outer object.
void CheckH5(Check& c) {
    bool loudness = false;
    for (int agg = 0; agg < 2; ++agg) {
        c.ctx = agg ? "[aggregated] " : "[plain] ";
        loudness = CheckH5Once(c, agg != 0) || loudness;
    }
    c.ctx.clear();
    if (StubBuild() && !loudness) c.Skip("Leveler output loudness -20 +- 1.5 (stub DSP does not level)");
}

// ===================================================================================================
// H6  Non-finite input and junk in silent buffers
// ===================================================================================================

void CheckH6Once(Check& c, bool aggregated) {
    for (int k = 0; k < 2; ++k) {
        const char* nm = kName[k];
        const double limit = (k == 0) ? 1.0 : CeilingLin(-8.0);
        for (int pass = 0; pass < 2; ++pass) {
            const bool inPlace = (pass == 0);
            Apo a;
            std::string why;
            if (!Bring(a, k, Format{ 48000, 2, 3 }, inPlace, 480, why, aggregated)) {
                c.Fail(Str("%s: %s", nm, why.c_str()));
                continue;
            }
            const size_t n = static_cast<size_t>(480) * 2;
            const float nan = std::nanf("");
            const float inf = INFINITY;
            bool finite = true, bounded = true, wellFormed = true;
            double worst = 0.0;
            for (int p = 0; p < 40; ++p) {
                FillSine(a.In(), 480, 2, 48000.0, 0.5, 1000.0, static_cast<uint64_t>(p) * 480);
                float* x = a.In();
                switch (p) {
                    case 0: for (size_t i = 0; i < n; ++i) x[i] = nan; break;
                    case 1: for (size_t i = 0; i < n; ++i) x[i] = (i & 1) ? -inf : inf; break;
                    case 2: for (size_t i = 0; i < n; ++i) x[i] = (i & 1) ? -1e30f : 1e30f; break;
                    case 3: x[0] = nan; x[100] = inf; x[200] = -1e30f; x[300] = 3.4e38f; x[301] = -3.4e38f; break;
                    case 4: for (size_t i = 0; i < n; ++i) x[i] = 1e-40f; break;  // denormals
                    case 5: for (size_t i = 0; i < n; i += 7) x[i] = 100.0f; break;
                    default: break;  // plain tone
                }
                const Period r = a.Process(480, BUFFER_VALID);
                wellFormed = wellFormed && r.flags == static_cast<uint32_t>(BUFFER_VALID) && r.frames == 480;
                finite = finite && AllFinite(a.Out(), n);
                const double m = MaxAbs(a.Out(), n);
                worst = std::max(worst, m);
                bounded = bounded && m <= limit + 1e-6;
            }
            const char* mode = inPlace ? "in place" : "separate buffers";
            c.Expect(wellFormed, Str("%s (%s): a period came back without BUFFER_VALID or with the wrong frame count", nm, mode));
            c.Expect(finite, Str("%s (%s): a non-finite output sample after NaN/Inf/1e30 input", nm, mode));
            c.Expect(bounded, Str("%s (%s): output peak %.6f above the limit %.6f", nm, mode, worst, limit));

            // Silent and invalid input flags with junk in the buffer: the output must be silence, flagged valid. Only a
            // drained object can give exact silence: the DSP's output for a silent period is whatever it still holds of
            // the sound before (H17 covers that tail), and the tone and the junk above just went through it.
            Drain(a);
            for (int flagKind = 0; flagKind < 2; ++flagKind) {
                const APO_BUFFER_FLAGS fl = flagKind == 0 ? BUFFER_SILENT : BUFFER_INVALID;
                const char* fn = flagKind == 0 ? "BUFFER_SILENT" : "BUFFER_INVALID";
                for (size_t i = 0; i < n; ++i) a.In()[i] = 1e30f;
                if (!inPlace) for (size_t i = 0; i < n; ++i) a.Out()[i] = 1e30f;
                const Period r = a.Process(480, fl);
                const double m = MaxAbs(a.Out(), n);
                c.Expect(r.flags == static_cast<uint32_t>(BUFFER_VALID) && r.frames == 480, Str("%s (%s): %s input: output flags %u frames %u (want BUFFER_VALID, 480)", nm, mode, fn, r.flags, r.frames));
                c.Expect(AllFinite(a.Out(), n) && m <= 1e-6, Str("%s (%s): %s input with junk gave output peak %g (want <= 1e-6)", nm, mode, fn, m));
            }
        }
    }
}

// Both ways: plain objects and objects aggregated under the test outer object.
void CheckH6(Check& c) {
    for (int agg = 0; agg < 2; ++agg) {
        c.ctx = agg ? "[aggregated] " : "[plain] ";
        CheckH6Once(c, agg != 0);
    }
    c.ctx.clear();
}

// ===================================================================================================
// H7  Separate input and output buffers give the same result as in place
// ===================================================================================================

void CheckH7(Check& c) {
    for (int k = 0; k < 2; ++k) {
        const char* nm = kName[k];
        Apo inp, sep;
        std::string why;
        if (!Bring(inp, k, Format{ 48000, 2, 3 }, true, 480, why) || !Bring(sep, k, Format{ 48000, 2, 3 }, false, 480, why)) {
            c.Fail(Str("%s: %s", nm, why.c_str()));
            continue;
        }
        const size_t n = static_cast<size_t>(480) * 2;
        double worst = 0.0;
        bool inputIntact = true;
        uint32_t lcg = 12345u;
        std::vector<float> keep(n);
        for (int p = 0; p < 30; ++p) {
            FillSine(inp.In(), 480, 2, 48000.0, p < 15 ? 0.9 : 0.2, 1000.0, static_cast<uint64_t>(p) * 480);
            for (size_t i = 0; i < n; ++i) {
                lcg = lcg * 1664525u + 1013904223u;
                inp.In()[i] += (static_cast<float>(lcg >> 8) / 16777216.0f - 0.5f) * 0.1f;
            }
            memcpy(sep.In(), inp.In(), n * sizeof(float));
            memcpy(keep.data(), sep.In(), n * sizeof(float));
            inp.Process(480, BUFFER_VALID);
            sep.Process(480, BUFFER_VALID);
            worst = std::max(worst, MaxDiff(inp.Out(), sep.Out(), n));
            if (memcmp(keep.data(), sep.In(), n * sizeof(float)) != 0) inputIntact = false;
        }
        c.Expect(worst <= 1e-6, Str("%s: in-place and out-of-place outputs differ by %g (want <= 1e-6)", nm, worst));
        c.Expect(inputIntact, Str("%s: the input buffer was modified when processing out of place", nm));
    }
}

// ===================================================================================================
// H8  Telemetry
// ===================================================================================================

void CheckH8(Check& c) {
    Apo lev, cap;
    std::string why;
    if (!Bring(lev, 0, Format{ 48000, 2, 3 }, true, 480, why)) {
        c.Fail("Leveler: " + why);
        return;
    }
    if (!Bring(cap, 1, Format{ 48000, 2, 3 }, true, 480, why)) {
        c.Fail("SafetyCap: " + why);
        return;
    }
    uint64_t pos = 0;
    for (int p = 0; p < 10; ++p) {
        FillSine(lev.In(), 480, 2, 48000.0, 0.7, 1000.0, pos);
        lev.Process(480, BUFFER_VALID);
        FillSine(cap.In(), 480, 2, 48000.0, 0.7, 1000.0, pos);
        cap.Process(480, BUFFER_VALID);
        pos += 480;
    }
    sp::SpGuard g{};
    c.Expect(ReadGuard(g) && g.uncleanStarts == 1, "guard.bin should read uncleanStarts = 1 after the first lock");

    Tel t;
    // The worker takes a snapshot every 50 ms, and one can fall in the middle of the 10 periods above (the first lock
    // starts the worker, so the timing is not random). Wait for the snapshot that follows them: all 4800 frames.
    const bool got = PollTel(500, t, [](const Tel& x) {
        const int li = x.FindActive(sp::kSlotLeveler);
        const int ci = x.FindActive(sp::kSlotSafetyCap);
        return li >= 0 && ci >= 0 && x.S(static_cast<uint32_t>(li)).framesProcessed >= 4800 && x.S(static_cast<uint32_t>(ci)).framesProcessed >= 4800;
    });
    if (!got) {
        c.Fail(t.Valid() ? "within 500 ms: no Leveler slot and Safety Cap slot with frames > 0" : "within 500 ms: apo.bin missing, too short, or wrong magic");
        return;
    }
    const sp::SpTelemetryHeader& h = t.H();
    c.Expect(h.version == sp::kTelemetryVersion && h.slotCount == sp::kTelemetrySlots && h.slotSize == 64, "header version, slotCount or slotSize is wrong");
    c.Expect(h.writerPid == GetCurrentProcessId(), Str("writerPid %u (want %lu)", h.writerPid, GetCurrentProcessId()));
    c.Expect((h.flags & (sp::kFlagConfigValid | sp::kFlagEnabled)) == (sp::kFlagConfigValid | sp::kFlagEnabled), Str("flags 0x%X lack ConfigValid|Enabled", h.flags));
    c.Expect((h.flags & sp::kFlagSafeMode) == 0, "SafeMode flag is set in a normal start");
    c.Expect(Near(h.capDb, -20.0f), Str("capDb %g (want -20)", h.capDb));
    c.Expect(Near(h.ceilingDb, -8.0f), Str("ceilingDb %g (want -8)", h.ceilingDb));
    c.Expect(h.configSequence == 1, Str("configSequence %u (want 1)", h.configSequence));

    const int li = t.FindActive(sp::kSlotLeveler), ci = t.FindActive(sp::kSlotSafetyCap);
    const sp::SpTelemetrySlot& ls = t.S(static_cast<uint32_t>(li));
    const sp::SpTelemetrySlot& cs = t.S(static_cast<uint32_t>(ci));
    c.Expect(t.CountKind(sp::kSlotLeveler) == 1 && t.CountKind(sp::kSlotSafetyCap) == 1, "expected exactly one Leveler slot and one Safety Cap slot");
    c.Expect(ls.mode == sp::kModeDefault && cs.mode == sp::kModeDefault, Str("slot modes %u/%u (want Default = %u)", ls.mode, cs.mode, static_cast<unsigned>(sp::kModeDefault)));
    c.Expect(ls.sampleRate == 48000 && ls.channels == 2 && cs.sampleRate == 48000 && cs.channels == 2, "slot sampleRate/channels are wrong");
    c.Expect(ls.instanceId != 0 && cs.instanceId != 0 && ls.instanceId != cs.instanceId, "instanceIds must be non-zero and unique");
    c.Expect(ls.framesProcessed == 4800 && cs.framesProcessed == 4800, Str("framesProcessed %llu/%llu (want 4800)", static_cast<unsigned long long>(ls.framesProcessed), static_cast<unsigned long long>(cs.framesProcessed)));
    FILETIME nowFt;
    GetSystemTimeAsFileTime(&nowFt);
    const int64_t now = (static_cast<int64_t>(nowFt.dwHighDateTime) << 32) | nowFt.dwLowDateTime;
    c.Expect(ls.lastProcessFileTime > 0 && std::llabs(now - ls.lastProcessFileTime) < 100000000LL, "Leveler lastProcessFileTime is not a recent FILETIME");
    c.Expect(cs.lastProcessFileTime > 0 && std::llabs(now - cs.lastProcessFileTime) < 100000000LL, "SafetyCap lastProcessFileTime is not a recent FILETIME");

    const uint64_t hb1 = h.heartbeat;
    Sleep(200);
    Tel t2;
    if (ReadTel(t2)) {
        c.Expect(t2.H().heartbeat > hb1, Str("heartbeat did not increase over 200 ms (%llu -> %llu)", static_cast<unsigned long long>(hb1), static_cast<unsigned long long>(t2.H().heartbeat)));
        c.Expect(t2.H().heartbeatFileTime > h.heartbeatFileTime, "heartbeatFileTime did not advance");
    } else {
        c.Fail("apo.bin unreadable on the second read");
    }

    // The log is how a problem inside audiodg gets diagnosed: UTF-8 lines, flushed by the worker. The first lock
    // started the worker and 200 ms have passed, so the start-up lines are in the file. A stub build says so there.
    {
        std::vector<uint8_t> raw;
        const bool have = ReadAll(DataPath(L"telemetry\\apo.log"), raw);
        const std::string text(raw.begin(), raw.end());
        c.Expect(have && text.find("worker started") != std::string::npos, "apo.log lacks the 'worker started' line");
        c.Expect(have && text.find("Leveler locked: 48000 Hz, 2 ch") != std::string::npos, "apo.log lacks the Leveler's 'locked' line");
        const bool saysStub = text.find("STUB DSP") != std::string::npos;
        c.Expect(saysStub == kStub, kStub ? "a stub build must say STUB DSP in apo.log" : "a real build must not say STUB DSP in apo.log");
    }
}

// ===================================================================================================
// H9  Config file
// ===================================================================================================

void CheckH9(Check& c) {
    Apo lev, cap;
    std::string why;
    if (!Bring(lev, 0, Format{ 48000, 2, 3 }, true, 480, why) || !Bring(cap, 1, Format{ 48000, 2, 3 }, true, 480, why)) {
        c.Fail(why);
        return;
    }
    uint64_t pos = 0;
    if (!kStub) {
        const ToneRun before = RunTone(lev, 200, 10, 0.8, pos);
        c.Expect(before.gainDb <= -6.0, Str("before the config change the Leveler gain is %.1f dB (want <= -6 on a loud tone)", before.gainDb));
    } else {
        RunTone(lev, 200, 10, 0.8, pos);
    }

    // golden 2: sequence 42, disabled, cap -35.5, margin 6
    c.Expect(WriteConfig(kGolden2), "cannot write config.bin");
    Tel t;
    bool ok = PollTel(600, t, [](const Tel& x) { return x.H().configSequence == 42; });
    if (!ok) {
        c.Fail("within 600 ms: telemetry never showed configSequence 42");
    } else {
        c.Expect(Near(t.H().capDb, -35.5f), Str("capDb %g (want -35.5)", t.H().capDb));
        c.Expect((t.H().flags & sp::kFlagEnabled) == 0, "the Enabled flag is still set after the disabling config");
        c.Expect((t.H().flags & sp::kFlagConfigValid) != 0, "ConfigValid is clear after a good config");
        c.Expect(Near(t.H().ceilingDb, 0.0f), Str("ceilingDb %g (want 0 while disabled)", t.H().ceilingDb));
    }
    Sleep(200);
    if (!kStub) {
        RunTone(lev, 30, 1, 0.8, pos);
        const ToneRun after = RunTone(lev, 10, 10, 0.8, pos);
        c.Expect(after.gainDb >= -0.5, Str("200 ms after the disabling config the Leveler still reduces the tone by %.1f dB", -after.gainDb));
    } else {
        c.Skip("'a loud tone is no longer reduced by the Leveler' (stub DSP never reduces)");
    }

    // a config whose checksum is wrong (sequence 43): the last good config stays, ConfigValid goes
    uint8_t bad[32];
    memcpy(bad, kGolden2, 32);
    bad[8] = 43;
    c.Expect(WriteConfig(bad), "cannot write the bad config");
    ok = PollTel(600, t, [](const Tel& x) { return (x.H().flags & sp::kFlagConfigValid) == 0; });
    if (!ok) {
        c.Fail("within 600 ms: ConfigValid stayed set after a config with a wrong checksum");
    } else {
        c.Expect(t.H().configSequence == 42, Str("configSequence %u after a bad config (want 42)", t.H().configSequence));
        c.Expect(Near(t.H().capDb, -35.5f), Str("capDb %g after a bad config (want the last good -35.5)", t.H().capDb));
    }

    // A torn file is never applied: the file empty, cut short, or one byte too long (the tray rewrites it in place, so
    // the plug-in can meet any of these). Each case starts from a good, applied config, so that "ConfigValid goes" is
    // a change the poll can see; the last good values must stay.
    {
        uint8_t longer[33];
        memcpy(longer, kGolden2, 32);
        longer[32] = 0;
        struct Torn {
            const char* what;
            const uint8_t* data;
            DWORD bytes;
        };
        const Torn torn[] = { { "an empty file", kGolden2, 0 },
                              { "a file cut after 16 bytes", kGolden2, 16 },
                              { "a file cut after 31 bytes", kGolden2, 31 },
                              { "a file with 33 bytes", longer, 33 } };
        for (const Torn& tc : torn) {
            c.Expect(WriteConfig(kGolden2), "cannot restore golden 2");
            if (!PollTel(600, t, [](const Tel& x) { return (x.H().flags & sp::kFlagConfigValid) != 0; })) {
                c.Fail(Str("within 600 ms: ConfigValid did not come back before the test of %s", tc.what));
                continue;
            }
            c.Expect(WriteAll(DataPath(L"config.bin"), tc.data, tc.bytes, true), Str("cannot write %s", tc.what));
            if (!PollTel(600, t, [](const Tel& x) { return (x.H().flags & sp::kFlagConfigValid) == 0; })) {
                c.Fail(Str("within 600 ms: ConfigValid stayed set with %s", tc.what));
                continue;
            }
            c.Expect(t.H().configSequence == 42 && Near(t.H().capDb, -35.5f),
                     Str("%s changed the settings in use: sequence %u, cap %g (want 42 and -35.5)", tc.what, t.H().configSequence,
                         t.H().capDb));
        }
    }

    // a valid config with out-of-range values is clamped: cap -100 -> -60, margin 99 -> 30, ceiling -30
    uint8_t wild[32];
    MakeConfig(wild, 44, 1, -100.0f, 99.0f);
    c.Expect(WriteConfig(wild), "cannot write the out-of-range config");
    ok = PollTel(600, t, [](const Tel& x) { return x.H().configSequence == 44; });
    if (!ok) {
        c.Fail("within 600 ms: an out-of-range but valid config was not applied");
    } else {
        c.Expect(Near(t.H().capDb, -60.0f) && Near(t.H().ceilingDb, -30.0f), Str("clamped cap/ceiling %g/%g (want -60/-30)", t.H().capDb, t.H().ceilingDb));
        c.Expect((t.H().flags & (sp::kFlagConfigValid | sp::kFlagEnabled)) == (sp::kFlagConfigValid | sp::kFlagEnabled), "flags after the out-of-range config");
    }

    // back to golden 1
    c.Expect(WriteConfig(kGolden1), "cannot restore golden 1");
    ok = PollTel(600, t, [](const Tel& x) { return x.H().configSequence == 1; });
    if (!ok) {
        c.Fail("within 600 ms: golden 1 was not applied again");
    } else {
        c.Expect(Near(t.H().capDb, -20.0f) && Near(t.H().ceilingDb, -8.0f) && (t.H().flags & sp::kFlagEnabled) && (t.H().flags & sp::kFlagConfigValid),
                 Str("state after golden 1: cap %g ceiling %g flags 0x%X", t.H().capDb, t.H().ceilingDb, t.H().flags));
    }
}

// ===================================================================================================
// H10  Crash-loop guard
// ===================================================================================================

void CheckH10(Check& c) {
    std::string why;
    c.Expect(WriteGuard(3), "cannot write guard.bin");
    c.Expect(WriteConfig(kGolden1), "cannot write config.bin");
    if (!UnloadDll(why)) {
        c.Fail("unload: " + why);
        return;
    }
    if (!LoadDll(why)) {
        c.Fail("reload: " + why);
        return;
    }
    {
        Apo lev, cap;
        if (!Bring(lev, 0, Format{ 48000, 2, 3 }, true, 480, why) || !Bring(cap, 1, Format{ 48000, 2, 3 }, true, 480, why)) {
            c.Fail(why);
            return;
        }
        sp::SpGuard g{};
        c.Expect(ReadGuard(g) && g.uncleanStarts == 4, Str("guard.bin uncleanStarts %u after the first lock (want 4 = 3 + 1)", g.uncleanStarts));
        Tel t;
        if (!PollTel(500, t, [](const Tel& x) { return (x.H().flags & sp::kFlagSafeMode) != 0; })) {
            c.Fail("within 500 ms: SafeMode flag not set after guard.bin said 3 unclean starts");
        }
        const double ceilLin = CeilingLin(-8.0);
        const size_t n = static_cast<size_t>(480) * 2;
        std::vector<float> keep(n);
        double levDiff = 0.0, capDiff = 0.0, capMax = 0.0, levMax = 0.0;
        for (int p = 0; p < 20; ++p) {
            const double amp = p < 10 ? 0.9 : 1.5;  // the second half is above full scale
            FillSine(lev.In(), 480, 2, 48000.0, amp, 1000.0, static_cast<uint64_t>(p) * 480);
            for (size_t i = 0; i < n; ++i) keep[i] = std::max(-1.0f, std::min(1.0f, lev.In()[i]));
            lev.Process(480, BUFFER_VALID);
            levDiff = std::max(levDiff, MaxDiff(keep.data(), lev.Out(), n));
            levMax = std::max(levMax, MaxAbs(lev.Out(), n));

            FillSine(cap.In(), 480, 2, 48000.0, amp, 1000.0, static_cast<uint64_t>(p) * 480);
            for (size_t i = 0; i < n; ++i) keep[i] = static_cast<float>(std::max(-ceilLin, std::min(ceilLin, static_cast<double>(cap.In()[i]))));
            cap.Process(480, BUFFER_VALID);
            capDiff = std::max(capDiff, MaxDiff(keep.data(), cap.Out(), n));
            capMax = std::max(capMax, MaxAbs(cap.Out(), n));
        }
        c.Expect(levDiff <= 1e-6, Str("safe mode: Leveler output differs from its (clamped) input by %g", levDiff));
        c.Expect(levMax <= 1.0 + 1e-6, Str("safe mode: Leveler output peak %g above 1.0", levMax));
        c.Expect(capMax <= ceilLin + 1e-6, Str("safe mode: Safety Cap output peak %g above the ceiling %g", capMax, ceilLin));
        c.Expect(capDiff <= 1e-6, Str("safe mode: Safety Cap output differs from a hard clamp of its input by %g", capDiff));

        lev.Unlock();
        c.Expect(ReadGuard(g) && g.uncleanStarts == 4, "guard.bin changed before the last effect unlocked");
        cap.Unlock();
        c.Expect(ReadGuard(g) && g.uncleanStarts == 0, Str("guard.bin uncleanStarts %u after the last unlock (want 0)", g.uncleanStarts));
    }
    // The safe-mode decision is for the life of the process. Start the next checks from a clean load.
    if (!UnloadDll(why)) {
        c.Fail("second unload: " + why);
        return;
    }
    if (!LoadDll(why)) c.Fail("second reload: " + why);
}

// ===================================================================================================
// H11  Many streams
// ===================================================================================================

void CheckH11(Check& c) {
    std::vector<std::unique_ptr<Apo>> all;
    std::string why;
    for (int i = 0; i < 40; ++i) {
        all.emplace_back(new Apo());
        if (!Bring(*all.back(), 0, Format{ 48000, 2, 3 }, true, 480, why)) {
            c.Fail(Str("object %d: %s", i, why.c_str()));
            return;
        }
    }
    bool finite = true, bounded = true;
    const size_t n = static_cast<size_t>(480) * 2;
    for (int p = 0; p < 5; ++p) {
        for (auto& a : all) {
            FillSine(a->In(), 480, 2, 48000.0, 0.9, 1000.0, static_cast<uint64_t>(p) * 480);
            a->Process(480, BUFFER_VALID);
            finite = finite && AllFinite(a->Out(), n);
            bounded = bounded && MaxAbs(a->Out(), n) <= 1.0 + 1e-6;
        }
    }
    c.Expect(finite, "a non-finite output sample with 40 streams");
    c.Expect(bounded, "an output above 1.0 with 40 streams");
    Tel t;
    // A snapshot can fall between the locks and the processing above (slots show up at lock time, with 0 frames), so
    // wait for the one that follows the 5 periods of 480 frames: 32 Leveler slots, each with all 2400 frames.
    const bool full = PollTel(500, t, [](const Tel& x) {
        if (x.CountKind(sp::kSlotLeveler) != 32) return false;
        for (uint32_t i = 0; i < sp::kTelemetrySlots; ++i)
            if (x.S(i).framesProcessed < 2400) return false;
        return true;
    });
    c.Expect(full, Str("within 500 ms: %d Leveler slots active (want exactly 32, each with 2400 frames)", t.Valid() ? t.CountKind(sp::kSlotLeveler) : -1));
    if (t.Valid()) {
        c.Expect(t.CountKind(sp::kSlotFree) == 0 && t.CountKind(sp::kSlotSafetyCap) == 0, "unexpected slot kinds with 40 Levelers");
        bool unique = true;
        for (uint32_t i = 0; i < sp::kTelemetrySlots; ++i)
            for (uint32_t j = i + 1; j < sp::kTelemetrySlots; ++j)
                if (t.S(i).instanceId == t.S(j).instanceId) unique = false;
        c.Expect(unique, "two slots share an instanceId");
        bool allFrames = true;
        for (uint32_t i = 0; i < sp::kTelemetrySlots; ++i) allFrames = allFrames && t.S(i).framesProcessed > 0;
        c.Expect(allFrames, "an active slot shows no processed frames");
    }
    all.clear();  // unlocks and releases all 40
    const bool freed = PollTel(500, t, [](const Tel& x) { return x.CountKind(sp::kSlotFree) == static_cast<int>(sp::kTelemetrySlots); });
    c.Expect(freed, "within 500 ms: the slots were not freed after all streams unlocked");
    sp::SpGuard g{};
    c.Expect(ReadGuard(g) && g.uncleanStarts == 0, Str("guard.bin uncleanStarts %u after all 40 streams unlocked (want 0)", g.uncleanStarts));
    {
        Apo again;
        if (Bring(again, 0, Format{ 48000, 2, 3 }, true, 480, why)) {
            FillSine(again.In(), 480, 2, 48000.0, 0.5, 1000.0, 0);
            again.Process(480, BUFFER_VALID);
            const bool seen = PollTel(500, t, [](const Tel& x) { return x.FindActive(sp::kSlotLeveler) >= 0; });
            c.Expect(seen, "a stream locked after the slots were freed got no slot");
        } else {
            c.Fail("lock after release failed: " + why);
        }
    }
}

// ===================================================================================================
// H12  Latency
// ===================================================================================================

void CheckH12(Check& c) {
    Apo cap, lev, idle;
    std::string why;
    if (!Bring(cap, 1, Format{ 48000, 2, 3 }, true, 480, why) || !Bring(lev, 0, Format{ 48000, 2, 3 }, true, 480, why)) {
        c.Fail(why);
        return;
    }
    HNSTIME t = -1;
    HRESULT hr = cap.apo->GetLatency(&t);
    const double want = (240.0 + 8.0) / 48000.0 * 1e7;
    c.Expect(SUCCEEDED(hr) && std::fabs(static_cast<double>(t) - want) <= 1.0 + 0.5, Str("Safety Cap latency %lld (100 ns units), want %.2f +- 1", static_cast<long long>(t), want));
    t = -1;
    hr = lev.apo->GetLatency(&t);
    c.Expect(SUCCEEDED(hr) && t == 0, Str("Leveler latency %lld (want 0)", static_cast<long long>(t)));
    if (idle.Create(sp::kClsidSafetyCap, true, why)) {
        t = -1;
        hr = idle.apo->GetLatency(&t);
        c.Expect(SUCCEEDED(hr) && t == 0, Str("latency before the lock %lld (want 0)", static_cast<long long>(t)));
    }
    c.Expect(cap.apo->GetLatency(nullptr) == E_POINTER, "GetLatency(nullptr) must return E_POINTER");
    // a different rate re-computes the latency
    cap.Unlock();
    hr = cap.Lock(Format{ 96000, 2, 3 }, true, 480);
    t = -1;
    if (SUCCEEDED(hr)) cap.apo->GetLatency(&t);
    const double want96 = (480.0 + 8.0) / 96000.0 * 1e7;
    c.Expect(SUCCEEDED(hr) && std::fabs(static_cast<double>(t) - want96) <= 1.5, Str("96 kHz latency %lld, want %.2f +- 1", static_cast<long long>(t), want96));
}

// ===================================================================================================
// H13  Crash-loop guard: ten seconds of healthy processing clear it (the brief puts this on the worker; H10 only
//      covers the trip and the reset at the last unlock). This check takes about 11 s.
// ===================================================================================================

void CheckH13(Check& c) {
    std::string why;
    c.Expect(WriteGuard(1), "cannot write guard.bin");
    c.Expect(WriteConfig(kGolden1), "cannot write config.bin");
    Apo lev;
    if (!Bring(lev, 0, Format{ 48000, 2, 3 }, true, 480, why)) {
        c.Fail(why);
        return;
    }
    sp::SpGuard g{};
    c.Expect(ReadGuard(g) && g.uncleanStarts == 2, Str("guard.bin uncleanStarts %u after the lock (want 2 = 1 + 1)", g.uncleanStarts));

    // 480 frames every 20 ms for 11 s: the frame counter moves at every worker tick and nothing is wrong.
    const ULONGLONG t0 = GetTickCount64();
    bool armedAtFiveSeconds = false;
    bool checked = false;
    uint64_t pos = 0;
    while (GetTickCount64() - t0 < 11000) {
        FillSine(lev.In(), 480, 2, 48000.0, 0.3, 1000.0, pos);
        pos += 480;
        lev.Process(480, BUFFER_VALID);
        if (!checked && GetTickCount64() - t0 >= 5000) {
            checked = true;
            for (int i = 0; i < 5 && !armedAtFiveSeconds; ++i) {
                armedAtFiveSeconds = ReadGuard(g) && g.uncleanStarts == 2;
                if (!armedAtFiveSeconds) Sleep(5);
            }
        }
        Sleep(20);
    }
    c.Expect(armedAtFiveSeconds, Str("guard.bin uncleanStarts %u after 5 s of processing (want 2: the guard clears only after 10 s)", g.uncleanStarts));
    bool cleared = false;
    for (int i = 0; i < 50 && !cleared; ++i) {
        cleared = ReadGuard(g) && g.uncleanStarts == 0;
        if (!cleared) Sleep(20);
    }
    c.Expect(cleared, Str("guard.bin uncleanStarts %u after 11 s of healthy processing (want 0, with the stream still locked)", g.uncleanStarts));
    lev.Unlock();
    c.Expect(ReadGuard(g) && g.uncleanStarts == 0, Str("guard.bin uncleanStarts %u after the unlock (want 0)", g.uncleanStarts));
}

// ===================================================================================================
// H14  Reset and a second Initialize (not in the brief's list; the engine makes both calls)
// ===================================================================================================

void CheckH14(Check& c) {
    for (int k = 0; k < 2; ++k) {
        const char* nm = kName[k];
        Apo a;
        std::string why;
        if (!Bring(a, k, Format{ 48000, 2, 3 }, true, 480, why)) {
            c.Fail(Str("%s: %s", nm, why.c_str()));
            continue;
        }
        const size_t n = static_cast<size_t>(480) * 2;
        uint64_t pos = 0;
        for (int p = 0; p < 100; ++p) {  // 1 s of a loud tone: the Safety Cap's look-ahead line holds real audio now
            FillSine(a.In(), 480, 2, 48000.0, 0.9, 1000.0, pos);
            a.Process(480, BUFFER_VALID);
            pos += 480;
        }

        // A second Initialize on a locked object is refused (and the lock stays usable, see the processing below).
        APOInitSystemEffects2 d{};
        d.APOInit.cbSize = sizeof(d);
        d.APOInit.clsid = kClsid[k];
        d.AudioProcessingMode = kModeDefault;
        const HRESULT hi = a.apo->Initialize(sizeof(d), reinterpret_cast<BYTE*>(&d));
        c.Expect(hi == APOERR_APO_LOCKED, Str("%s: Initialize on a locked object returned %s (want APOERR_APO_LOCKED)", nm, HrText(hi).c_str()));

        // Reset clears the DSP at the start of the next period: silence in is silence out at once. Without the reset
        // the Safety Cap's look-ahead line would let a tail of the loud tone out.
        const HRESULT hr = a.apo->Reset();
        c.Expect(hr == S_OK, Str("%s: Reset returned %s", nm, HrText(hr).c_str()));
        std::memset(a.In(), 0, n * sizeof(float));
        Period r = a.Process(480, BUFFER_VALID);
        c.Expect(r.flags == static_cast<uint32_t>(BUFFER_VALID) && r.frames == 480,
                 Str("%s: the first period after Reset came back without BUFFER_VALID or with the wrong frame count", nm));
        const double tail = MaxAbs(a.Out(), n);
        c.Expect(AllFinite(a.Out(), n) && tail <= 1e-6, Str("%s: the first period after Reset carries old audio (peak %g, want 0)", nm, tail));

        // The stream goes on afterwards and still obeys its limit.
        const double limit = (k == 0) ? 1.0 : CeilingLin(-8.0);
        double worst = 0.0;
        bool good = true;
        for (int p = 0; p < 20; ++p) {
            FillSine(a.In(), 480, 2, 48000.0, 0.9, 1000.0, pos);
            pos += 480;
            r = a.Process(480, BUFFER_VALID);
            good = good && AllFinite(a.Out(), n) && r.flags == static_cast<uint32_t>(BUFFER_VALID) && r.frames == 480;
            worst = std::max(worst, MaxAbs(a.Out(), n));
        }
        c.Expect(good, Str("%s: processing after Reset gave a non-finite sample, a bad flag or a wrong frame count", nm));
        c.Expect(worst <= limit + 1e-6, Str("%s: output peak %.6f after Reset is above the limit %.6f", nm, worst, limit));
    }
}

// ===================================================================================================
// H15  Guard: a reset that fails is kept and tried again, and logged once
// ===================================================================================================
//
// Someone else holds guard.bin open when the plug-in tries to reset it to 0. The reset is then still owed: the plug-in
// must not forget it, and tries again (on its worker thread every second, and at the last unlock) until it works. It
// writes one line to apo.log for the whole episode, not one per try. Four cases: the reset at the last unlock (the
// object lives on, unlocked, and the worker retries), the same in a session that is in safe mode, the one after 10 s of
// healthy processing while the stream stays locked (about 14 s), and a new session that starts while a reset is
// still owed.

// initial = what guard.bin holds before the session. 3 or more puts the session in safe mode, where the health logic
// returns early: the retry must still run there.
void CheckH15LastUnlock(Check& c, uint32_t initial) {
    std::string why;
    if (!ReloadDll(why)) {
        c.Fail("reload: " + why);
        return;
    }
    c.Expect(WriteGuard(initial), "cannot write guard.bin");
    c.Expect(WriteConfig(kGolden1), "cannot write config.bin");
    const size_t linesBefore = CountText(ReadLog(), "cannot reset guard.bin");
    Apo lev;
    if (!Bring(lev, 0, Format{ 48000, 2, 3 }, true, 480, why)) {
        c.Fail(why);
        return;
    }
    sp::SpGuard g{};
    c.Expect(ReadGuard(g) && g.uncleanStarts == initial + 1, Str("guard.bin uncleanStarts %u after the lock (want %u = %u + 1)", g.uncleanStarts, initial + 1, initial));
    if (initial >= sp::kGuardSafeModeAt) {
        Tel t;
        c.Expect(PollTel(500, t, [](const Tel& x) { return (x.H().flags & sp::kFlagSafeMode) != 0; }), "within 500 ms: the SafeMode flag is not set");
    }

    FileHold hold;
    c.Expect(hold.Take(DataPath(L"telemetry\\guard.bin"), 0), "cannot hold guard.bin");
    lev.Unlock();  // the last effect unlocks: its reset cannot open the file
    Sleep(2500);   // the file stays held through two more of the worker's tries
    hold.Release();
    bool cleared = false;
    for (int i = 0; i < 150 && !cleared; ++i) {  // the worker's next try is at most a second away
        cleared = ReadGuard(g) && g.uncleanStarts == 0;
        if (!cleared) Sleep(20);
    }
    c.Expect(cleared, Str("guard.bin uncleanStarts %u, 3 s after the hold ended (want 0: a failed reset is kept and tried again)", g.uncleanStarts));
    Sleep(200);
    const size_t lines = CountText(ReadLog(), "cannot reset guard.bin") - linesBefore;
    c.Expect(lines == 1, Str("apo.log has %zu 'cannot reset guard.bin' lines for one held file (want exactly 1: logged once, not at every try)", lines));
}

void CheckH15HealthMark(Check& c) {
    std::string why;
    if (!ReloadDll(why)) {
        c.Fail("reload: " + why);
        return;
    }
    c.Expect(WriteGuard(1), "cannot write guard.bin");
    c.Expect(WriteConfig(kGolden1), "cannot write config.bin");
    const size_t linesBefore = CountText(ReadLog(), "cannot reset guard.bin");
    Apo lev;
    if (!Bring(lev, 0, Format{ 48000, 2, 3 }, true, 480, why)) {
        c.Fail(why);
        return;
    }
    sp::SpGuard g{};
    c.Expect(ReadGuard(g) && g.uncleanStarts == 2, Str("guard.bin uncleanStarts %u after the lock (want 2 = 1 + 1)", g.uncleanStarts));

    // 480 frames every 20 ms, as in H13. The healthy mark falls about 10 s in. The file is held from 8 s to 12.5 s, so the
    // reset at the mark fails, and so does the first retry or two; the stream stays locked and keeps processing.
    FileHold hold;
    int stage = 0;  // 0 before the hold, 1 held, 2 let go
    bool cleared = false;
    uint64_t pos = 0;
    const ULONGLONG t0 = GetTickCount64();
    while (GetTickCount64() - t0 < 17000 && !cleared) {
        FillSine(lev.In(), 480, 2, 48000.0, 0.3, 1000.0, pos);
        pos += 480;
        lev.Process(480, BUFFER_VALID);
        const ULONGLONG t = GetTickCount64() - t0;
        if (stage == 0 && t >= 8000) {
            c.Expect(hold.Take(DataPath(L"telemetry\\guard.bin"), 0), "cannot hold guard.bin");
            stage = 1;
        } else if (stage == 1 && t >= 12500) {
            hold.Release();
            stage = 2;
        }
        if (stage == 2) cleared = ReadGuard(g) && g.uncleanStarts == 0;
        Sleep(20);
    }
    hold.Release();
    c.Expect(cleared, Str("guard.bin uncleanStarts %u, 4.5 s after the hold ended with the stream still locked (want 0: the reset at the 10 s mark failed and must be tried again)", g.uncleanStarts));
    Sleep(200);
    const size_t lines = CountText(ReadLog(), "cannot reset guard.bin") - linesBefore;
    c.Expect(lines == 1, Str("apo.log has %zu 'cannot reset guard.bin' lines for one held file (want exactly 1: logged once, not at every try)", lines));
    lev.Unlock();
    c.Expect(ReadGuard(g) && g.uncleanStarts == 0, Str("guard.bin uncleanStarts %u after the unlock (want 0)", g.uncleanStarts));
}

// A new session starts while the reset of the last one is still owed. The owed reset is part of the new session's +1
// from then on: it must not be tried again behind the new session's back (that would wipe the new +1 long before its
// healthy mark), and a failure after that is a new episode with its own log line.
void CheckH15NewSession(Check& c) {
    std::string why;
    if (!ReloadDll(why)) {
        c.Fail("reload: " + why);
        return;
    }
    c.Expect(WriteGuard(1), "cannot write guard.bin");
    c.Expect(WriteConfig(kGolden1), "cannot write config.bin");
    const size_t linesBefore = CountText(ReadLog(), "cannot reset guard.bin");
    Apo lev;
    if (!Bring(lev, 0, Format{ 48000, 2, 3 }, true, 480, why)) {
        c.Fail(why);
        return;
    }
    FileHold hold;
    c.Expect(hold.Take(DataPath(L"telemetry\\guard.bin"), 0), "cannot hold guard.bin");
    lev.Unlock();    // the reset fails and stays owed ...
    hold.Release();  // ... the file is free again at once, and the new session starts before the worker's first retry
    const HRESULT hr = lev.Lock(Format{ 48000, 2, 3 }, true, 480);
    if (FAILED(hr)) {
        c.Fail("second LockForProcess failed " + HrText(hr));
        return;
    }
    sp::SpGuard g{};
    c.Expect(ReadGuard(g) && g.uncleanStarts >= 1, Str("guard.bin uncleanStarts %u right after the new session started (want it raised)", g.uncleanStarts));

    // 2.5 s of processing: past the worker's first retry. guard.bin must stay raised: it holds the new session's +1.
    bool erased = false;
    uint64_t pos = 0;
    const ULONGLONG t0 = GetTickCount64();
    while (GetTickCount64() - t0 < 2500) {
        FillSine(lev.In(), 480, 2, 48000.0, 0.3, 1000.0, pos);
        pos += 480;
        lev.Process(480, BUFFER_VALID);
        if (!(ReadGuard(g) && g.uncleanStarts >= 1)) erased = true;
        Sleep(20);
    }
    c.Expect(!erased, "guard.bin went back to 0 while the new session was still running (the reset owed for the old one was tried again behind the new session's back)");

    // The new session ends while the file is held again: a second episode.
    c.Expect(hold.Take(DataPath(L"telemetry\\guard.bin"), 0), "cannot hold guard.bin the second time");
    lev.Unlock();
    hold.Release();
    bool cleared = false;
    for (int i = 0; i < 150 && !cleared; ++i) {
        cleared = ReadGuard(g) && g.uncleanStarts == 0;
        if (!cleared) Sleep(20);
    }
    c.Expect(cleared, Str("guard.bin uncleanStarts %u, 3 s after the second hold ended (want 0)", g.uncleanStarts));
    Sleep(200);
    const size_t lines = CountText(ReadLog(), "cannot reset guard.bin") - linesBefore;
    c.Expect(lines == 2, Str("apo.log has %zu 'cannot reset guard.bin' lines for two separate episodes (want exactly 2: one each)", lines));
}

void CheckH15(Check& c) {
    c.ctx = "[reset at the last unlock] ";
    CheckH15LastUnlock(c, 1);
    c.ctx = "[reset at the last unlock, in safe mode] ";
    CheckH15LastUnlock(c, 3);
    c.ctx = "[reset at the 10 s mark] ";
    CheckH15HealthMark(c);
    c.ctx = "[new session while a reset is owed] ";
    CheckH15NewSession(c);
    c.ctx.clear();
}

// ===================================================================================================
// H16  Guard: a guard.bin that cannot be read or written at the start of a session is left alone
// ===================================================================================================
//
// A failed open or read is not "no history": the plug-in must not write over a count it could not read, must not count
// this start or arm the guard for it (so the unlock does not reset it either), must not call it a crash loop, and
// says so once in apo.log ("crash guard unavailable: ..."). The stream works as usual. Three holders: one that blocks
// everything, one that blocks reading only (the plug-in's write would succeed: the case in which the old code
// erased a real count), and one that blocks writing only (a scanner). When the hold ends the guard works again and
// sees the real count (session 3), and a later outage gets its own line (session 4). The other side of the line: a
// guard.bin that reads fine but is not a guard (empty, short, foreign magic) is repaired, and nothing is reported.

void CheckH16(Check& c) {
    // One session: the object locks and runs five periods; the stream must work, and the telemetry must show safe mode
    // exactly when expected. The caller reads guard.bin and unlocks.
    auto begin = [&](Apo& lev, int session, bool expectSafe) {
        std::string w;
        if (!Bring(lev, 0, Format{ 48000, 2, 3 }, true, 480, w)) {
            c.Fail(Str("session %d: %s", session, w.c_str()));
            return false;
        }
        const size_t n = static_cast<size_t>(480) * 2;
        bool good = true;
        for (int p = 0; p < 5; ++p) {
            FillSine(lev.In(), 480, 2, 48000.0, 0.5, 1000.0, static_cast<uint64_t>(p) * 480);
            const Period r = lev.Process(480, BUFFER_VALID);
            good = good && r.flags == static_cast<uint32_t>(BUFFER_VALID) && r.frames == 480 && AllFinite(lev.Out(), n) &&
                   MaxAbs(lev.Out(), n) <= 1.0 + 1e-6;
        }
        c.Expect(good, Str("session %d: the stream does not work", session));
        Tel t;
        if (PollTel(500, t, [](const Tel& x) { return x.FindActive(sp::kSlotLeveler) >= 0; })) {
            const bool safe = (t.H().flags & sp::kFlagSafeMode) != 0;
            c.Expect(safe == expectSafe, Str("session %d: safe mode is %s (want %s)", session, safe ? "on" : "off", expectSafe ? "on" : "off"));
        } else {
            c.Fail(Str("session %d: no telemetry slot within 500 ms", session));
        }
        return true;
    };

    struct Kind {
        const char* what;
        DWORD share;         // what the holder lets others do
        uint32_t count;      // what guard.bin holds
        const char* logText; // what the log line must say
    };
    const Kind kinds[] = {
        { "held with no sharing", 0, 2, "cannot read guard.bin (error 32)" },
        { "held against reading", FILE_SHARE_WRITE, 5, "cannot read guard.bin (error 32)" },
        { "held against writing", FILE_SHARE_READ, 2, "cannot write guard.bin (error 32)" },
    };
    for (const Kind& kd : kinds) {
        c.ctx = Str("[%s] ", kd.what);
        std::string why;
        if (!ReloadDll(why)) {
            c.Fail("reload: " + why);
            break;
        }
        c.Expect(WriteGuard(kd.count), "cannot write guard.bin");
        c.Expect(WriteConfig(kGolden1), "cannot write config.bin");
        const size_t linesBefore = CountText(ReadLog(), "crash guard unavailable");
        const std::wstring guardPath = DataPath(L"telemetry\\guard.bin");
        // Once the count is readable again it is the real one: 3 or more is a crash loop and means safe mode.
        const bool crashLoop = kd.count >= sp::kGuardSafeModeAt;

        FileHold hold;
        if (!hold.Take(guardPath, kd.share)) {
            c.Fail("cannot hold guard.bin");
            continue;
        }
        for (int session = 1; session <= 2; ++session) {  // two sessions with the file held: the log line still comes once
            Apo lev;
            if (!begin(lev, session, false)) break;
            lev.Unlock();
        }
        hold.Release();
        sp::SpGuard g{};
        c.Expect(ReadGuard(g) && g.uncleanStarts == kd.count,
                 Str("guard.bin uncleanStarts %u after two sessions with the file held (want %u: untouched)", g.uncleanStarts, kd.count));
        Sleep(200);
        std::string log = ReadLog();
        size_t lines = CountText(log, "crash guard unavailable") - linesBefore;
        c.Expect(lines == 1, Str("apo.log has %zu 'crash guard unavailable' lines for two sessions (want exactly 1)", lines));
        std::string line = LastLineWith(log, "crash guard unavailable");
        c.Expect(line.find(kd.logText) != std::string::npos, Str("the log line is '%s' (want it to say '%s')", line.c_str(), kd.logText));

        // The hold is over and the guard works again (an outage is not sticky): the session counts what the file held,
        // and the unlock puts the file back to 0.
        {
            Apo lev;
            if (begin(lev, 3, crashLoop)) {
                c.Expect(ReadGuard(g) && g.uncleanStarts == kd.count + 1,
                         Str("session 3: guard.bin uncleanStarts %u while it runs (want %u: readable and writable again)", g.uncleanStarts, kd.count + 1));
                lev.Unlock();
                c.Expect(ReadGuard(g) && g.uncleanStarts == 0, Str("session 3: guard.bin uncleanStarts %u after the unlock (want 0)", g.uncleanStarts));
            }
        }

        // A second outage is a new episode: its own log line, and the file is left alone again.
        if (hold.Take(guardPath, kd.share)) {
            Apo lev;
            if (begin(lev, 4, crashLoop)) lev.Unlock();
        } else {
            c.Fail("cannot hold guard.bin the second time");
        }
        hold.Release();
        c.Expect(ReadGuard(g) && g.uncleanStarts == 0, Str("guard.bin uncleanStarts %u after session 4 with the file held (want 0: untouched)", g.uncleanStarts));
        Sleep(200);
        log = ReadLog();
        lines = CountText(log, "crash guard unavailable") - linesBefore;
        c.Expect(lines == 2, Str("apo.log has %zu 'crash guard unavailable' lines for two separate outages (want exactly 2: one each)", lines));
        line = LastLineWith(log, "crash guard unavailable");
        c.Expect(line.find(kd.logText) != std::string::npos, Str("the second log line is '%s' (want it to say '%s')", line.c_str(), kd.logText));
    }

    // The other side of the line: a guard.bin that opens and reads fine but is not a guard (empty, short, foreign) holds
    // no count to lose. It is repaired by the start, which counts as the first, and nothing is reported.
    struct Junk {
        const char* what;
        std::string bytes;
    };
    const Junk junk[] = {
        { "empty file", std::string() },
        { "5 bytes", std::string("\x01\x02\x03\x04\x05", 5) },
        { "16 bytes with a foreign magic", std::string("NOT A GUARD FILE", 16) },
    };
    for (const Junk& j : junk) {
        c.ctx = Str("[%s] ", j.what);
        std::string why;
        if (!ReloadDll(why)) {
            c.Fail("reload: " + why);
            break;
        }
        c.Expect(WriteAll(DataPath(L"telemetry\\guard.bin"), j.bytes.data(), static_cast<DWORD>(j.bytes.size()), true), "cannot write the junk guard.bin");
        c.Expect(WriteConfig(kGolden1), "cannot write config.bin");
        const size_t linesBefore = CountText(ReadLog(), "crash guard unavailable");
        {
            Apo lev;
            if (begin(lev, 1, false)) {
                sp::SpGuard g{};
                c.Expect(ReadGuard(g) && g.uncleanStarts == 1, Str("guard.bin uncleanStarts %u while it runs (want 1: repaired, and this start is the first)", g.uncleanStarts));
                lev.Unlock();
                c.Expect(ReadGuard(g) && g.uncleanStarts == 0, Str("guard.bin uncleanStarts %u after the unlock (want 0)", g.uncleanStarts));
            }
        }
        Sleep(200);
        const size_t lines = CountText(ReadLog(), "crash guard unavailable") - linesBefore;
        c.Expect(lines == 0, Str("apo.log has %zu 'crash guard unavailable' lines (want 0: the file was readable)", lines));
    }
    c.ctx.clear();
    // Leave a clean start for the next check.
    std::string why;
    c.Expect(WriteGuard(0), "cannot write guard.bin at the end");
    if (!ReloadDll(why)) c.Fail("final reload: " + why);
}

// ===================================================================================================
// H17  A silent-flagged period still delivers what the DSP holds (the tail), then zeros
// ===================================================================================================
//
// The input of a BUFFER_SILENT or BUFFER_INVALID period is zeros, whatever the buffer holds (the brief's APOProcess
// step 2). The output is what the DSP makes of those zeros: first the tail of the sound before it (the Safety Cap's
// look-ahead line still holds about 5 ms of it), then zeros. The plug-in must not cut the tail off, or a fade-out
// ends in a step; the finite check and the clamp still apply to it.

void CheckH17(Check& c) {
    c.Expect(WriteConfig(kGolden1), "cannot write config.bin");
    for (int k = 0; k < 2; ++k) {
        const double limit = (k == 0) ? 1.0 : CeilingLin(-8.0);
        for (int flagKind = 0; flagKind < 2; ++flagKind) {
            const APO_BUFFER_FLAGS fl = flagKind == 0 ? BUFFER_SILENT : BUFFER_INVALID;
            for (int pass = 0; pass < 2; ++pass) {
                const bool inPlace = (pass == 0);
                const std::string tag = Str("%s, %s input, %s", kName[k], flagKind == 0 ? "BUFFER_SILENT" : "BUFFER_INVALID",
                                            inPlace ? "in place" : "separate buffers");
                Apo a;
                std::string why;
                if (!Bring(a, k, Format{ 48000, 2, 3 }, inPlace, 480, why)) {
                    c.Fail(Str("%s: %s", tag.c_str(), why.c_str()));
                    continue;
                }
                const size_t n = static_cast<size_t>(480) * 2;

                // The look-ahead in frames, as the object reports it (the Leveler has none).
                HNSTIME lat = 0;
                a.apo->GetLatency(&lat);
                const uint32_t look = static_cast<uint32_t>(static_cast<double>(lat) * 48000.0 / 1e7 + 0.5);
                c.Expect((k == 0) ? look == 0 : look >= 8, Str("%s: reported look-ahead %u frames", tag.c_str(), look));

                // 300 ms of a loud tone: the Safety Cap's look-ahead line now holds real audio.
                uint64_t pos = 0;
                for (int p = 0; p < 30; ++p) {
                    FillSine(a.In(), 480, 2, 48000.0, 0.9, 1000.0, pos);
                    pos += 480;
                    a.Process(480, BUFFER_VALID);
                }

                // The sound stops: the next period is flagged silent, with junk in its buffers.
                for (size_t i = 0; i < n; ++i) a.In()[i] = 1e30f;
                if (!inPlace)
                    for (size_t i = 0; i < n; ++i) a.Out()[i] = 1e30f;
                const Period first = a.Process(480, fl);
                const size_t tailSamples = static_cast<size_t>(look) * 2;
                const size_t quietFrom = look > 0 ? (static_cast<size_t>(look) + 64) * 2 : 0;  // 64 frames of slack after the tail
                const double tail = MaxAbs(a.Out(), tailSamples);
                const double after = MaxAbs(a.Out() + quietFrom, n - quietFrom);
                c.Expect(first.flags == static_cast<uint32_t>(BUFFER_VALID) && first.frames == 480,
                         Str("%s: the period after the sound came back with flags %u, frames %u (want BUFFER_VALID, 480)", tag.c_str(), first.flags, first.frames));
                c.Expect(AllFinite(a.Out(), n) && MaxAbs(a.Out(), n) <= limit + 1e-6,
                         Str("%s: the period after the sound has a sample beyond the limit %.6f (peak %g)", tag.c_str(), limit, MaxAbs(a.Out(), n)));
                if (look > 0) {
                    c.Expect(tail > 0.05, Str("%s: the tail of the sound before was cut: the first %u frames of the silent period peak at %g (want > 0.05)",
                                              tag.c_str(), look, tail));
                }
                c.Expect(after <= 1e-6, Str("%s: the silent period is not silent after the tail: peak %g from frame %zu on (want <= 1e-6)", tag.c_str(),
                                            after, quietFrom / 2));

                // The next silent-flagged period is all zeros, flagged valid.
                for (size_t i = 0; i < n; ++i) a.In()[i] = 1e30f;
                if (!inPlace)
                    for (size_t i = 0; i < n; ++i) a.Out()[i] = 1e30f;
                const Period second = a.Process(480, fl);
                c.Expect(second.flags == static_cast<uint32_t>(BUFFER_VALID) && second.frames == 480,
                         Str("%s: the second silent period came back with flags %u, frames %u", tag.c_str(), second.flags, second.frames));
                c.Expect(AllFinite(a.Out(), n) && MaxAbs(a.Out(), n) <= 1e-6,
                         Str("%s: the second silent period is not silent: peak %g (want <= 1e-6)", tag.c_str(), MaxAbs(a.Out(), n)));
            }
        }
    }
}

// ===================================================================================================
// Driver
// ===================================================================================================

// Runs one check. An access violation or other SEH exception inside the DLL is reported as a failure of
// that check, so a crash cannot hide the other results.
int CallGuarded(void (*fn)(Check&), Check& c) {
    __try {
        fn(c);
        return 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return static_cast<int>(GetExceptionCode());
    }
}

void Run(const char* name, void (*fn)(Check&)) {
    Check c(name);
    try {
        const int crash = CallGuarded(fn, c);
        if (crash != 0) c.Fail(Str("crashed with exception 0x%08X", static_cast<unsigned>(crash)));
    } catch (const std::exception& e) {
        c.Fail(std::string("C++ exception: ") + e.what());
    } catch (...) {
        c.Fail("unknown C++ exception");
    }
    c.Done();
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) {
        printf("usage: ApoHost.exe <path to SoundProtection.Apo.dll>\n");
        return 2;
    }
    {
        wchar_t full[MAX_PATH * 2] = {};
        const DWORD n = GetFullPathNameW(argv[1], static_cast<DWORD>(sizeof(full) / sizeof(full[0])), full, nullptr);
        g_dllPath = (n > 0 && n < sizeof(full) / sizeof(full[0])) ? std::wstring(full) : std::wstring(argv[1]);
    }
    if (GetFileAttributesW(g_dllPath.c_str()) == INVALID_FILE_ATTRIBUTES) {
        printf("FAIL setup: the DLL does not exist\n");
        return 2;
    }
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    // Host self-check: the host's own FNV-1a must reproduce the golden bytes of the contract.
    {
        uint8_t g1[32], g2[32];
        MakeConfig(g1, 1, 1, -20.0f, 12.0f);
        MakeConfig(g2, 42, 0, -35.5f, 6.0f);
        if (memcmp(g1, kGolden1, 32) != 0 || memcmp(g2, kGolden2, 32) != 0) {
            printf("FAIL setup: the host's config encoder does not reproduce the golden bytes\n");
            return 2;
        }
    }

    wchar_t tmp[MAX_PATH] = {};
    const DWORD tn = GetTempPathW(MAX_PATH, tmp);
    if (tn == 0 || tn >= MAX_PATH) {
        printf("FAIL setup: no temp folder\n");
        return 2;
    }
    g_root = std::wstring(tmp);
    while (!g_root.empty() && (g_root.back() == L'\\' || g_root.back() == L'/')) g_root.pop_back();
    g_root += L"\\SoundProtectionHost-" + std::to_wstring(GetCurrentProcessId());
    RemoveTree(g_root);
    if (!CreateDirectoryW(g_root.c_str(), nullptr) || !CreateDirectoryW((g_root + L"\\telemetry").c_str(), nullptr)) {
        printf("FAIL setup: cannot create %ls\n", g_root.c_str());
        return 2;
    }
    SetEnvironmentVariableW(SP_DATA_DIR_ENV, g_root.c_str());
    if (!WriteConfig(kGolden1)) {
        printf("FAIL setup: cannot write config.bin\n");
        RemoveTree(g_root);
        return 2;
    }

    std::string why;
    if (!LoadDll(why)) {
        printf("FAIL setup: %s\n", why.c_str());
        RemoveTree(g_root);
        return 2;
    }
    printf("host: %ls, built %s, data dir %ls\n", g_dllPath.c_str(), kStub ? "with the STUB DSP" : "with the REAL DSP", g_root.c_str());

    Run("H1", CheckH1);
    Run("H2", CheckH2);
    Run("H3", CheckH3);
    Run("H4", CheckH4);
    Run("H5", CheckH5);
    Run("H6", CheckH6);
    Run("H7", CheckH7);
    Run("H8", CheckH8);
    Run("H9", CheckH9);
    Run("H10", CheckH10);
    Run("H11", CheckH11);
    Run("H12", CheckH12);
    Run("H13", CheckH13);
    Run("H14", CheckH14);
    Run("H15", CheckH15);
    Run("H16", CheckH16);
    Run("H17", CheckH17);

    // Clean-up: everything released, the DLL really unloads, the temp folder goes away.
    {
        Check c("cleanup");
        if (g_canUnload && g_canUnload() != S_OK) c.Fail("DllCanUnloadNow is not S_OK after every object was released");
        std::string w2;
        if (!UnloadDll(w2)) c.Fail(w2);
        RemoveTree(g_root);
        if (GetFileAttributesW(g_root.c_str()) != INVALID_FILE_ATTRIBUTES) c.Fail("the temp folder could not be deleted");
        c.Done();
    }
    CoUninitialize();
    printf("%s\n", g_failed == 0 ? "ALL CHECKS PASSED" : "SOME CHECKS FAILED");
    return g_failed == 0 ? 0 : 1;
}
