// Nuvia.ShellExt — IExplorerCommand implementations. See NuviaCommands.h.
#include "NuviaCommands.h"

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------

// Full path to the sibling Nuvia.exe (the app is installed next to this DLL).
static HRESULT GetAppExePath(PWSTR buffer, size_t cchBuffer)
{
    WCHAR module[MAX_PATH];
    if (GetModuleFileNameW(g_hInst, module, ARRAYSIZE(module)) == 0)
        return HRESULT_FROM_WIN32(GetLastError());
    PathRemoveFileSpecW(module);
    WCHAR combined[MAX_PATH];
    if (!PathCombineW(combined, module, L"Nuvia.exe"))
        return E_FAIL;
    return StringCchCopyW(buffer, cchBuffer, combined);
}

// "<Nuvia.exe>,0" — the app icon used for every menu entry.
static HRESULT DupAppIcon(PWSTR* icon)
{
    *icon = nullptr;
    WCHAR exe[MAX_PATH];
    HRESULT hr = GetAppExePath(exe, ARRAYSIZE(exe));
    if (FAILED(hr))
        return hr;
    WCHAR spec[MAX_PATH + 4];
    hr = StringCchPrintfW(spec, ARRAYSIZE(spec), L"%s,0", exe);
    if (FAILED(hr))
        return hr;
    return SHStrDupW(spec, icon);
}
// Launch Nuvia.exe --upload "<first selected file>" --to <token>. The C# app enforces
// single-instance and does all product logic; this DLL only starts the process.
static HRESULT LaunchUpload(IShellItemArray* items, PCWSTR destinationToken)
{
    if (!items)
        return E_INVALIDARG;

    IShellItem* item = nullptr;
    HRESULT hr = items->GetItemAt(0, &item);   // single-file: only the first item is used
    if (FAILED(hr))
        return hr;

    PWSTR path = nullptr;
    hr = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
    item->Release();
    if (FAILED(hr))
        return hr;   // e.g. a virtual item with no filesystem path

    WCHAR exe[MAX_PATH];
    hr = GetAppExePath(exe, ARRAYSIZE(exe));
    if (SUCCEEDED(hr))
    {
        // File names cannot contain '"' on Windows, so simple quoting is safe.
        std::wstring cmd;
        cmd.reserve(MAX_PATH * 2);
        cmd.append(L"\"").append(exe).append(L"\" --upload \"")
           .append(path).append(L"\" --to ").append(destinationToken);

        std::vector<wchar_t> mutableCmd(cmd.begin(), cmd.end());
        mutableCmd.push_back(L'\0');

        WCHAR workingDir[MAX_PATH];
        if (SUCCEEDED(StringCchCopyW(workingDir, ARRAYSIZE(workingDir), exe)))
            PathRemoveFileSpecW(workingDir);

        STARTUPINFOW si = { sizeof(si) };
        PROCESS_INFORMATION pi = {};
        if (CreateProcessW(exe, mutableCmd.data(), nullptr, nullptr, FALSE,
                           0, nullptr, workingDir, &si, &pi))
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
        else
        {
            hr = HRESULT_FROM_WIN32(GetLastError());
        }
    }

    CoTaskMemFree(path);
    return hr;
}
// ---------------------------------------------------------------------------
// NuviaVerbCommand — a single flyout entry
// ---------------------------------------------------------------------------

NuviaVerbCommand::NuviaVerbCommand(PCWSTR title, PCWSTR destinationToken)
    : _cRef(1), _title(title), _destinationToken(destinationToken)
{
    InterlockedIncrement(&g_cDllRef);
}

NuviaVerbCommand::~NuviaVerbCommand()
{
    InterlockedDecrement(&g_cDllRef);
}

IFACEMETHODIMP NuviaVerbCommand::QueryInterface(REFIID riid, void** ppv)
{
    static const QITAB qit[] =
    {
        QITABENT(NuviaVerbCommand, IExplorerCommand),
        { 0 },
    };
    return QISearch(this, qit, riid, ppv);
}

IFACEMETHODIMP_(ULONG) NuviaVerbCommand::AddRef()
{
    return InterlockedIncrement(&_cRef);
}

IFACEMETHODIMP_(ULONG) NuviaVerbCommand::Release()
{
    long ref = InterlockedDecrement(&_cRef);
    if (ref == 0)
        delete this;
    return ref;
}

IFACEMETHODIMP NuviaVerbCommand::GetTitle(IShellItemArray*, PWSTR* name)
{
    return SHStrDupW(_title, name);
}

IFACEMETHODIMP NuviaVerbCommand::GetIcon(IShellItemArray*, PWSTR* icon)
{
    return DupAppIcon(icon);
}

IFACEMETHODIMP NuviaVerbCommand::GetToolTip(IShellItemArray*, PWSTR* infoTip)
{
    *infoTip = nullptr;
    return E_NOTIMPL;
}
IFACEMETHODIMP NuviaVerbCommand::GetCanonicalName(GUID* guidCommandName)
{
    if (guidCommandName)
        *guidCommandName = GUID{};
    return E_NOTIMPL;
}

IFACEMETHODIMP NuviaVerbCommand::GetState(IShellItemArray*, BOOL, EXPCMDSTATE* cmdState)
{
    *cmdState = ECS_ENABLED;
    return S_OK;
}

IFACEMETHODIMP NuviaVerbCommand::Invoke(IShellItemArray* items, IBindCtx*)
{
    return LaunchUpload(items, _destinationToken);
}

IFACEMETHODIMP NuviaVerbCommand::GetFlags(EXPCMDFLAGS* flags)
{
    *flags = ECF_DEFAULT;
    return S_OK;
}

IFACEMETHODIMP NuviaVerbCommand::EnumSubCommands(IEnumExplorerCommand** enumCommands)
{
    *enumCommands = nullptr;
    return E_NOTIMPL;
}

// ---------------------------------------------------------------------------
// NuviaRootCommand — the top-level "Upload to Nuvia" entry with a flyout
// ---------------------------------------------------------------------------

NuviaRootCommand::NuviaRootCommand() : _cRef(1)
{
    InterlockedIncrement(&g_cDllRef);
}

NuviaRootCommand::~NuviaRootCommand()
{
    InterlockedDecrement(&g_cDllRef);
}

IFACEMETHODIMP NuviaRootCommand::QueryInterface(REFIID riid, void** ppv)
{
    static const QITAB qit[] =
    {
        QITABENT(NuviaRootCommand, IExplorerCommand),
        { 0 },
    };
    return QISearch(this, qit, riid, ppv);
}
IFACEMETHODIMP_(ULONG) NuviaRootCommand::AddRef()
{
    return InterlockedIncrement(&_cRef);
}

IFACEMETHODIMP_(ULONG) NuviaRootCommand::Release()
{
    long ref = InterlockedDecrement(&_cRef);
    if (ref == 0)
        delete this;
    return ref;
}

IFACEMETHODIMP NuviaRootCommand::GetTitle(IShellItemArray*, PWSTR* name)
{
    return SHStrDupW(L"Upload to Nuvia", name);
}

IFACEMETHODIMP NuviaRootCommand::GetIcon(IShellItemArray*, PWSTR* icon)
{
    return DupAppIcon(icon);
}

IFACEMETHODIMP NuviaRootCommand::GetToolTip(IShellItemArray*, PWSTR* infoTip)
{
    *infoTip = nullptr;
    return E_NOTIMPL;
}

IFACEMETHODIMP NuviaRootCommand::GetCanonicalName(GUID* guidCommandName)
{
    if (guidCommandName)
        *guidCommandName = GUID{};
    return E_NOTIMPL;
}

IFACEMETHODIMP NuviaRootCommand::GetState(IShellItemArray*, BOOL, EXPCMDSTATE* cmdState)
{
    *cmdState = ECS_ENABLED;
    return S_OK;
}

IFACEMETHODIMP NuviaRootCommand::Invoke(IShellItemArray*, IBindCtx*)
{
    // The root only opens the flyout; the individual sub-commands do the work.
    return S_OK;
}

IFACEMETHODIMP NuviaRootCommand::GetFlags(EXPCMDFLAGS* flags)
{
    *flags = ECF_HASSUBCOMMANDS;
    return S_OK;
}
IFACEMETHODIMP NuviaRootCommand::EnumSubCommands(IEnumExplorerCommand** enumCommands)
{
    *enumCommands = nullptr;
    NuviaEnumCommands* e = new (std::nothrow) NuviaEnumCommands();
    if (!e)
        return E_OUTOFMEMORY;
    HRESULT hr = e->QueryInterface(IID_PPV_ARGS(enumCommands));
    e->Release();
    return hr;
}

// ---------------------------------------------------------------------------
// NuviaEnumCommands — hands out the three flyout entries
// ---------------------------------------------------------------------------

NuviaEnumCommands::NuviaEnumCommands() : _cRef(1), _index(0)
{
    InterlockedIncrement(&g_cDllRef);
    _commands[0] = new (std::nothrow) NuviaVerbCommand(L"Upload to Saved Messages", L"saved");
    _commands[1] = new (std::nothrow) NuviaVerbCommand(L"Upload to private group", L"group");
    _commands[2] = new (std::nothrow) NuviaVerbCommand(L"Choose destination in Nuvia…", L"ask");
}

NuviaEnumCommands::~NuviaEnumCommands()
{
    for (ULONG i = 0; i < kCount; ++i)
        if (_commands[i])
            _commands[i]->Release();
    InterlockedDecrement(&g_cDllRef);
}

IFACEMETHODIMP NuviaEnumCommands::QueryInterface(REFIID riid, void** ppv)
{
    static const QITAB qit[] =
    {
        QITABENT(NuviaEnumCommands, IEnumExplorerCommand),
        { 0 },
    };
    return QISearch(this, qit, riid, ppv);
}

IFACEMETHODIMP_(ULONG) NuviaEnumCommands::AddRef()
{
    return InterlockedIncrement(&_cRef);
}

IFACEMETHODIMP_(ULONG) NuviaEnumCommands::Release()
{
    long ref = InterlockedDecrement(&_cRef);
    if (ref == 0)
        delete this;
    return ref;
}
IFACEMETHODIMP NuviaEnumCommands::Next(ULONG celt, IExplorerCommand** commands, ULONG* fetched)
{
    ULONG count = 0;
    for (; count < celt && _index < kCount; ++count, ++_index)
    {
        IExplorerCommand* c = _commands[_index];
        if (!c)
            return E_OUTOFMEMORY;   // a sub-command failed to construct
        c->AddRef();
        commands[count] = c;
    }
    if (fetched)
        *fetched = count;
    return (count == celt) ? S_OK : S_FALSE;
}

IFACEMETHODIMP NuviaEnumCommands::Skip(ULONG celt)
{
    _index += celt;
    if (_index > kCount)
        _index = kCount;
    return S_OK;
}

IFACEMETHODIMP NuviaEnumCommands::Reset()
{
    _index = 0;
    return S_OK;
}

IFACEMETHODIMP NuviaEnumCommands::Clone(IEnumExplorerCommand** enumCommands)
{
    *enumCommands = nullptr;
    return E_NOTIMPL;
}
