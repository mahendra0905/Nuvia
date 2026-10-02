#requires -Version 5.1
<#
  register-shellext.ps1 — installed ALONGSIDE Nuvia in {app} and invoked by the Inno
  installer/uninstaller. Registers (or with -Remove, unregisters) the Nuvia sparse
  package for the CURRENT user, binding the external content in this same folder
  (Nuvia.exe + NuviaShellExt.dll) so the Windows 11 top-level right-click
  "Upload to Nuvia" menu appears. Per-user, no elevation.

  Best-effort by design: if registration cannot succeed (e.g. the package is signed
  with a dev certificate that isn't trusted and Windows Developer Mode is off), the
  script exits non-zero but the app itself is unaffected — only the right-click menu
  is absent. This is why the installer never fails the install on our account.

  This is the SHIPPED registration script. packaging\register-dev.ps1 is the separate
  developer inner-loop helper that registers straight from the repo.
#>
[CmdletBinding()]
param([switch]$Remove)

$ErrorActionPreference = 'SilentlyContinue'
$app     = $PSScriptRoot
$pkgName = 'Nuvia.ShellExtension'   # must equal Identity/@Name in AppxManifest.xml

if ($Remove) {
    Get-AppxPackage -Name $pkgName | Remove-AppxPackage
    exit 0
}

$msix     = Join-Path $app 'Nuvia.msix'
$manifest = Join-Path $app 'AppxManifest.xml'
$ok = $false

# Primary path: register the signed sparse package (works with no elevation when the
# signing certificate chains to a trusted root — e.g. a purchased/production cert).
if (Test-Path $msix) {
    try { Add-AppxPackage -Path $msix -ExternalLocation $app -ForceApplicationShutdown -ErrorAction Stop; $ok = $true } catch {}
}

# Fallback: register the loose manifest. No signing needed, but requires Windows
# Developer Mode to be ON (Settings > Privacy & security > For developers).
if (-not $ok -and (Test-Path $manifest)) {
    try { Add-AppxPackage -Register $manifest -ExternalLocation $app -ForceApplicationShutdown -ErrorAction Stop; $ok = $true } catch {}
}

if ($ok) { exit 0 } else { exit 1 }
