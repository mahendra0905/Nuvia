// Nuvia.ShellExt — shared includes for the Explorer context-menu handler.
// This is a plain (non-precompiled) shared header; every translation unit includes it first.
#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>   // IExplorerCommand, IEnumExplorerCommand, IShellItemArray, EXPCMD*, SIGDN_*
#include <shlwapi.h>  // QISearch / QITAB, SHStrDupW, PathCombineW, PathRemoveFileSpecW
#include <strsafe.h>  // StringCchPrintfW
#include <new>        // std::nothrow
#include <string>
#include <vector>

// Defined in dllmain.cpp. The module handle is used to resolve the sibling Nuvia.exe,
// and the DLL reference count keeps the handler loaded while explorer.exe holds objects.
extern HINSTANCE g_hInst;
extern long g_cDllRef;
