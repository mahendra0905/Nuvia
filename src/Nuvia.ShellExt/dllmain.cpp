// Nuvia.ShellExt — DLL entry point, class factory and COM exports.
#include "Common.h"
#include <initguid.h>   // makes DEFINE_GUID emit the actual CLSID definition in this TU
#include "Guids.h"
#include "NuviaCommands.h"

HINSTANCE g_hInst = nullptr;
long g_cDllRef = 0;

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_hInst = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

// Minimal IClassFactory for the single context-menu class.
class NuviaClassFactory : public IClassFactory
{
public:
    NuviaClassFactory() : _cRef(1) { InterlockedIncrement(&g_cDllRef); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        static const QITAB qit[] =
        {
            QITABENT(NuviaClassFactory, IClassFactory),
            { 0 },
        };
        return QISearch(this, qit, riid, ppv);
    }

    IFACEMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&_cRef); }

    IFACEMETHODIMP_(ULONG) Release()
    {
        long ref = InterlockedDecrement(&_cRef);
        if (ref == 0)
            delete this;
        return ref;
    }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv)
    {
        *ppv = nullptr;
        if (outer)
            return CLASS_E_NOAGGREGATION;
        NuviaRootCommand* cmd = new (std::nothrow) NuviaRootCommand();
        if (!cmd)
            return E_OUTOFMEMORY;
        HRESULT hr = cmd->QueryInterface(riid, ppv);
        cmd->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL lock)
    {
        if (lock)
            InterlockedIncrement(&g_cDllRef);
        else
            InterlockedDecrement(&g_cDllRef);
        return S_OK;
    }

private:
    ~NuviaClassFactory() { InterlockedDecrement(&g_cDllRef); }
    long _cRef;
};

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    *ppv = nullptr;
    if (!IsEqualCLSID(rclsid, CLSID_NuviaContextMenu))
        return CLASS_E_CLASSNOTAVAILABLE;

    NuviaClassFactory* factory = new (std::nothrow) NuviaClassFactory();
    if (!factory)
        return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return (g_cDllRef == 0) ? S_OK : S_FALSE;
}
