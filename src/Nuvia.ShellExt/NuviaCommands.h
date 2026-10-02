// Nuvia.ShellExt — IExplorerCommand implementations.
//
// The handler is a DUMB LAUNCHER. It contains NO Telegram, session, credential or product
// logic: every command does exactly one thing — start "Nuvia.exe --upload <file> --to <dest>"
// and let the C# app do the real work (single-instance coordination, auth, upload). See the
// approved plan (noble-churning-sunbeam.md).
#pragma once

#include "Common.h"

// Leaf command shown inside the "Upload to Nuvia" flyout. Each instance launches the app with a
// fixed destination hint (saved | group | ask).
class NuviaVerbCommand : public IExplorerCommand
{
public:
    NuviaVerbCommand(PCWSTR title, PCWSTR destinationToken);

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv);
    IFACEMETHODIMP_(ULONG) AddRef();
    IFACEMETHODIMP_(ULONG) Release();

    // IExplorerCommand
    IFACEMETHODIMP GetTitle(IShellItemArray* items, PWSTR* name);
    IFACEMETHODIMP GetIcon(IShellItemArray* items, PWSTR* icon);
    IFACEMETHODIMP GetToolTip(IShellItemArray* items, PWSTR* infoTip);
    IFACEMETHODIMP GetCanonicalName(GUID* guidCommandName);
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL okToBeSlow, EXPCMDSTATE* cmdState);
    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx* bindCtx);
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags);
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** enumCommands);

private:
    ~NuviaVerbCommand();

    long _cRef;
    PCWSTR _title;             // static literal, not owned
    PCWSTR _destinationToken;  // static literal, not owned ("saved" | "group" | "ask")
};

// Top-level "Upload to Nuvia" entry. It never runs anything itself; it reports
// ECF_HASSUBCOMMANDS so Explorer opens the flyout of NuviaVerbCommand items.
class NuviaRootCommand : public IExplorerCommand
{
public:
    NuviaRootCommand();

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv);
    IFACEMETHODIMP_(ULONG) AddRef();
    IFACEMETHODIMP_(ULONG) Release();

    // IExplorerCommand
    IFACEMETHODIMP GetTitle(IShellItemArray* items, PWSTR* name);
    IFACEMETHODIMP GetIcon(IShellItemArray* items, PWSTR* icon);
    IFACEMETHODIMP GetToolTip(IShellItemArray* items, PWSTR* infoTip);
    IFACEMETHODIMP GetCanonicalName(GUID* guidCommandName);
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL okToBeSlow, EXPCMDSTATE* cmdState);
    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx* bindCtx);
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags);
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** enumCommands);

private:
    ~NuviaRootCommand();

    long _cRef;
};

// Enumerator that hands Explorer the three flyout sub-commands.
class NuviaEnumCommands : public IEnumExplorerCommand
{
public:
    NuviaEnumCommands();

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv);
    IFACEMETHODIMP_(ULONG) AddRef();
    IFACEMETHODIMP_(ULONG) Release();

    // IEnumExplorerCommand
    IFACEMETHODIMP Next(ULONG celt, IExplorerCommand** commands, ULONG* fetched);
    IFACEMETHODIMP Skip(ULONG celt);
    IFACEMETHODIMP Reset();
    IFACEMETHODIMP Clone(IEnumExplorerCommand** enumCommands);

private:
    ~NuviaEnumCommands();

    static const ULONG kCount = 3;
    long _cRef;
    ULONG _index;
    IExplorerCommand* _commands[kCount];
};
