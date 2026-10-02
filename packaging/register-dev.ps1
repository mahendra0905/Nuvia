#requires -Version 5.1
<#
.SYNOPSIS
  Dev inner-loop: register (or remove) the Nuvia sparse package for the CURRENT user
  from the LOOSE manifest — no signing needed, but Windows "Developer Mode" must be ON
  (Settings > Privacy & security > For developers).

.DESCRIPTION
  Registers packaging\sparse\AppxManifest.xml with -ExternalLocation pointing at the
  folder that actually holds Nuvia.exe + NuviaShellExt.dll (your build/publish output).
  After (un)registering, restart Explorer for the menu change to take effect
  (pass -RestartExplorer to do it automatically).

.PARAMETER PublishDir
  Folder containing Nuvia.exe AND NuviaShellExt.dll. If omitted, auto-detected from
  artifacts\publish\win-x64 then artifacts\shellext\Release.

.PARAMETER Unregister
  Remove the registered package instead of adding it.

.PARAMETER RestartExplorer
  Restart Explorer after the change so the menu appears/disappears immediately.
#>
[CmdletBinding()]
param(
    [string]$PublishDir,
    [switch]$Unregister,
    [switch]$RestartExplorer
)
$ErrorActionPreference = 'Stop'
$repo     = Split-Path -Parent $PSScriptRoot
$sparse   = Join-Path $PSScriptRoot 'sparse'
$manifest = Join-Path $sparse 'AppxManifest.xml'
$pkgName  = 'Nuvia.ShellExtension'   # must equal Identity/@Name in AppxManifest.xml

function Restart-Explorer {
    Write-Host '==> Restarting Explorer'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
}

if ($Unregister) {
    $pkgs = Get-AppxPackage -Name $pkgName
    if (-not $pkgs) { Write-Host "Not registered ($pkgName). Nothing to do."; return }
    foreach ($p in $pkgs) {
        Write-Host "==> Removing $($p.PackageFullName)"
        Remove-AppxPackage -Package $p.PackageFullName
    }
    if ($RestartExplorer) { Restart-Explorer }
    Write-Host 'Unregistered.'
    return
}

if (-not $PublishDir) {
    $candidates = @(
        (Join-Path $repo 'artifacts\publish\win-x64'),
        (Join-Path $repo 'artifacts\shellext\Release')
    )
    $PublishDir = $candidates | Where-Object { Test-Path (Join-Path $_ 'NuviaShellExt.dll') } | Select-Object -First 1
    if (-not $PublishDir) { $PublishDir = $candidates[0] }
}
$PublishDir = (Resolve-Path $PublishDir).Path
Write-Host "==> External location: $PublishDir"

# The external location must contain BOTH Nuvia.exe and NuviaShellExt.dll. A fresh
# publish has only the exe, so copy the built handler DLL in next to it (dev convenience).
if ((Test-Path (Join-Path $PublishDir 'Nuvia.exe')) -and -not (Test-Path (Join-Path $PublishDir 'NuviaShellExt.dll'))) {
    $builtDll = @(
        (Join-Path $repo 'artifacts\shellext\Release\NuviaShellExt.dll'),
        (Join-Path $repo 'artifacts\shellext\Debug\NuviaShellExt.dll')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($builtDll) {
        Copy-Item $builtDll $PublishDir -Force
        Write-Host "    copied NuviaShellExt.dll into the external location"
    }
}

$missing = @()
foreach ($f in @('Nuvia.exe','NuviaShellExt.dll')) {
    if (-not (Test-Path (Join-Path $PublishDir $f))) { $missing += $f }
}
if ($missing.Count) {
    Write-Warning "External location is missing: $($missing -join ', ')"
    Write-Warning 'The menu may appear but launching/handling will fail until both files are present.'
    Write-Warning 'Build them first:  powershell -File packaging\build-shellext.ps1  and  dotnet publish ...'
}

$devKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
$devOn  = (Get-ItemProperty -Path $devKey -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($devOn -ne 1) {
    Write-Warning 'Developer Mode appears OFF. Registering a loose manifest needs it:'
    Write-Warning '  Settings > Privacy & security > For developers > Developer Mode = On'
    Write-Warning '  (Or use the signed msix: packaging\build-shellext.ps1 -Sign, then Add-AppxPackage.)'
}

Write-Host "==> Registering $manifest"
Add-AppxPackage -Register $manifest -ExternalLocation $PublishDir
if ($RestartExplorer) { Restart-Explorer }
Write-Host ''
Write-Host "Registered. Right-click any file -> 'Upload to Nuvia'."
Write-Host 'If it does not show yet, restart Explorer:  Stop-Process -Name explorer -Force'
