// SpDll.cpp - the DLL's entry points: DllMain, the class factory, the unload check, and the registration code.
//
// Registration is code only. Nothing in this DLL registers anything by itself: DllRegisterServer and
// DllUnregisterServer run only when somebody (the installer, through regsvr32) calls them.
#include "SpApoBase.h"

#include <strsafe.h>

namespace {

HMODULE g_module = nullptr;

typedef HRESULT (*CreateFn)(IUnknown*, REFIID, void**);

// ---- the class factory (one small heap object per DllGetClassObject call) ---------------------------------------

class ClassFactory final : public IClassFactory {
public:
    explicit ClassFactory(CreateFn create) : create_(create) { sp::apo::GetRuntime().FactoryCreated(); }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (IsEqualIID(riid, __uuidof(IUnknown)) || IsEqualIID(riid, __uuidof(IClassFactory))) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&refs_)); }

    ULONG STDMETHODCALLTYPE Release() override {
        const LONG r = InterlockedDecrement(&refs_);
        if (r == 0) {
            delete this;
            sp::apo::GetRuntime().FactoryDestroyed();  // last: after this the DLL may unload
        }
        return static_cast<ULONG>(r);
    }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        // Aggregation: the caller must ask for IUnknown (it gets the inner, non-delegating one).
        if (outer && !IsEqualIID(riid, __uuidof(IUnknown))) return CLASS_E_NOAGGREGATION;
        return create_(outer, riid, ppv);
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {
        sp::apo::GetRuntime().ServerLock(lock != FALSE);
        return S_OK;
    }

private:
    LONG refs_ = 1;
    CreateFn create_;
};

// ---- registration (code only; see the header comment) ----------------------------------------------------------------

struct ClassInfo {
    const wchar_t* clsid;
    const wchar_t* friendlyName;
    const GUID* guid;
    const APO_REG_PROPERTIES* (*props)();
};

const ClassInfo kClasses[] = {
    { SP_CLSID_LEVELER_STR, SP_LEVELER_FRIENDLY_NAME, &sp::kClsidLeveler, &sp::apo::LevelerRegProps },
    { SP_CLSID_SAFETYCAP_STR, SP_SAFETYCAP_FRIENDLY_NAME, &sp::kClsidSafetyCap, &sp::apo::SafetyCapRegProps },
};

HRESULT SetStringValue(HKEY key, const wchar_t* name, const wchar_t* value) {
    size_t chars = 0;
    HRESULT hr = StringCchLengthW(value, 1024, &chars);
    if (FAILED(hr)) return hr;
    const LONG rc = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value), static_cast<DWORD>((chars + 1) * sizeof(wchar_t)));
    return HRESULT_FROM_WIN32(rc);
}

// HKLM\SOFTWARE\Classes\CLSID\{clsid}  (default = friendly name)
//   InprocServer32                     (default = path of this DLL, ThreadingModel = Both)
HRESULT WriteClsidKeys(const ClassInfo& c, const wchar_t* dllPath) {
    wchar_t path[160];
    HRESULT hr = StringCchPrintfW(path, ARRAYSIZE(path), L"SOFTWARE\\Classes\\CLSID\\%s", c.clsid);
    if (FAILED(hr)) return hr;
    HKEY key = nullptr;
    LONG rc = RegCreateKeyExW(HKEY_LOCAL_MACHINE, path, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (rc != ERROR_SUCCESS) return HRESULT_FROM_WIN32(rc);
    hr = SetStringValue(key, nullptr, c.friendlyName);
    if (SUCCEEDED(hr)) {
        HKEY server = nullptr;
        rc = RegCreateKeyExW(key, L"InprocServer32", 0, nullptr, 0, KEY_WRITE, nullptr, &server, nullptr);
        if (rc != ERROR_SUCCESS) {
            hr = HRESULT_FROM_WIN32(rc);
        } else {
            hr = SetStringValue(server, nullptr, dllPath);
            if (SUCCEEDED(hr)) hr = SetStringValue(server, L"ThreadingModel", L"Both");
            RegCloseKey(server);
        }
    }
    RegCloseKey(key);
    return hr;
}

HRESULT DeleteClsidKeys(const ClassInfo& c) {
    wchar_t path[160];
    HRESULT hr = StringCchPrintfW(path, ARRAYSIZE(path), L"SOFTWARE\\Classes\\CLSID\\%s", c.clsid);
    if (FAILED(hr)) return hr;
    const LONG rc = RegDeleteTreeW(HKEY_LOCAL_MACHINE, path);
    if (rc == ERROR_SUCCESS || rc == ERROR_FILE_NOT_FOUND) return S_OK;
    return HRESULT_FROM_WIN32(rc);
}

}  // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID /*reserved*/) {
    // Nothing else happens here: no threads, no files, no COM. (Thread notifications stay on: the static CRT needs them.)
    if (reason == DLL_PROCESS_ATTACH) g_module = instance;
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    CreateFn create = nullptr;
    if (IsEqualCLSID(rclsid, sp::kClsidLeveler)) {
        create = &sp::apo::CreateLevelerApo;
    } else if (IsEqualCLSID(rclsid, sp::kClsidSafetyCap)) {
        create = &sp::apo::CreateSafetyCapApo;
    } else {
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    if (!IsEqualIID(riid, __uuidof(IClassFactory)) && !IsEqualIID(riid, __uuidof(IUnknown))) return E_NOINTERFACE;
    ClassFactory* factory = new (std::nothrow) ClassFactory(create);
    if (!factory) return E_OUTOFMEMORY;
    const HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow() { return sp::apo::GetRuntime().CanUnloadNow() ? S_OK : S_FALSE; }

// Called by regsvr32 (the installer), never by the test host and never by an agent.
STDAPI DllRegisterServer() {
    wchar_t dllPath[MAX_PATH] = {};
    const DWORD n = GetModuleFileNameW(g_module, dllPath, ARRAYSIZE(dllPath));
    if (n == 0) return HRESULT_FROM_WIN32(GetLastError());
    if (n >= ARRAYSIZE(dllPath)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);

    for (const ClassInfo& c : kClasses) {
        const HRESULT hr = WriteClsidKeys(c, dllPath);
        if (FAILED(hr)) return hr;
    }
    for (const ClassInfo& c : kClasses) {
        const HRESULT hr = RegisterAPO(c.props());
        if (FAILED(hr)) return hr;
    }
    return S_OK;
}

STDAPI DllUnregisterServer() {
    HRESULT result = S_OK;
    for (const ClassInfo& c : kClasses) {
        const HRESULT hr = UnregisterAPO(*c.guid);
        if (FAILED(hr) && hr != HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) && SUCCEEDED(result)) result = hr;
    }
    for (const ClassInfo& c : kClasses) {
        const HRESULT hr = DeleteClsidKeys(c);
        if (FAILED(hr) && SUCCEEDED(result)) result = hr;
    }
    return result;
}
