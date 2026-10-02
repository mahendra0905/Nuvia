#requires -Version 5.1
<#
.SYNOPSIS
  Build (and optionally sign) the Nuvia sparse package that gives the unpackaged Win32
  app a package identity, so the native shell extension (NuviaShellExt.dll) appears in
  the Windows 11 top-level right-click menu — "Upload to Nuvia" with a flyout.

.DESCRIPTION
  1. Builds NuviaShellExt.dll (MSVC, <Configuration>|x64) from src\Nuvia.ShellExt.
  2. Regenerates the MSIX logo assets from the app logo (n-logo.png).
  3. Packs packaging\sparse (AppxManifest.xml + Assets) into artifacts\packaging\Nuvia.msix.
  4. With -Sign: signs the .msix with a self-signed DEV cert (subject = -CertSubject,
     which MUST equal the manifest Publisher) and exports the public .cer so a tester
     can trust it once.

  The .msix contains ONLY the manifest + logos. Nuvia.exe and NuviaShellExt.dll are
  EXTERNAL content — shipped by the Inno installer into {app} and bound at register time
  via -ExternalLocation. See packaging\README.md. No product logic lives in the package.

.NOTES
  Windows-only. Needs Visual Studio C++ build tools + Windows SDK (MakeAppx / SignTool).
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [switch]$Sign,
    [string]$CertSubject = 'CN=Nuvia'
)

$ErrorActionPreference = 'Stop'
$repo   = Split-Path -Parent $PSScriptRoot            # repo root (packaging\..)
$sparse = Join-Path $PSScriptRoot 'sparse'
$assets = Join-Path $sparse 'Assets'
$logo   = Join-Path $repo 'src\Nuvia.App\Assets\n-logo.png'
$vcxproj= Join-Path $repo 'src\Nuvia.ShellExt\Nuvia.ShellExt.vcxproj'
$outDir = Join-Path $repo 'artifacts\packaging'
$stage  = Join-Path $outDir 'stage'
$msix   = Join-Path $outDir 'Nuvia.msix'
$cer    = Join-Path $outDir 'Nuvia.cer'
$dll    = Join-Path $repo "artifacts\shellext\$Configuration\NuviaShellExt.dll"

function Find-SdkTool([string]$name) {
    $bin = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (-not (Test-Path $bin)) { throw "Windows SDK bin not found: $bin" }
    $hit = Get-ChildItem $bin -Directory |
        Where-Object { $_.Name -match '^10\.' } |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$name" } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $hit) { throw "$name not found under $bin\**\x64" }
    return $hit
}

function Find-MSBuild {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $p = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null | Select-Object -First 1
        if ($p -and (Test-Path $p)) { return $p }
    }
    foreach ($c in @('D:\VISUAL STUDIO\MSBuild\Current\Bin\MSBuild.exe')) {
        if (Test-Path $c) { return $c }
    }
    throw 'MSBuild.exe not found (install the VS C++ build tools).'
}

$makeappx = Find-SdkTool 'makeappx.exe'
$signtool = Find-SdkTool 'signtool.exe'
$msbuild  = Find-MSBuild

Write-Host "==> Building NuviaShellExt.dll ($Configuration|x64)"
& $msbuild $vcxproj /p:Configuration=$Configuration /p:Platform=x64 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed ($LASTEXITCODE)" }
if (-not (Test-Path $dll)) { throw "DLL not produced: $dll" }

Write-Host '==> Regenerating logo assets'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$img = [System.Drawing.Image]::FromFile((Resolve-Path $logo))
foreach ($s in @(@{w=50;n='StoreLogo.png'},@{w=150;n='Square150x150Logo.png'},@{w=44;n='Square44x44Logo.png'})) {
    $bmp = New-Object System.Drawing.Bitmap $s.w, $s.w
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($img, 0, 0, $s.w, $s.w)
    $bmp.Save((Join-Path $assets $s.n), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
$img.Dispose()

Write-Host '==> Staging sparse package (manifest + Assets only)'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'Assets') | Out-Null
Copy-Item (Join-Path $sparse 'AppxManifest.xml') $stage -Force
Copy-Item (Join-Path $assets '*') (Join-Path $stage 'Assets') -Force

Write-Host "==> Packing $msix"
if (Test-Path $msix) { Remove-Item $msix -Force }
# /nv skips semantic validation: Nuvia.exe / NuviaShellExt.dll are EXTERNAL content and are
# not inside the package (they resolve against -ExternalLocation at register time).
& $makeappx pack /d $stage /p $msix /nv /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed ($LASTEXITCODE)" }

if ($Sign) {
    Write-Host "==> Signing with self-signed dev cert ($CertSubject)"
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $CertSubject } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $CertSubject `
            -KeyUsage DigitalSignature -FriendlyName 'Nuvia dev signing' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
        Write-Host "    created cert $($cert.Thumbprint)"
    } else {
        Write-Host "    reusing cert $($cert.Thumbprint)"
    }
    Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix
    if ($LASTEXITCODE -ne 0) { throw "SignTool failed ($LASTEXITCODE)" }
    Write-Host "    exported public cert -> $cer"
    Write-Host "    trust once (admin): Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
}

Write-Host ''
Write-Host 'Done.'
Write-Host "  msix : $msix"
Write-Host "  dll  : $dll   (ship into {app} as external content)"
if ($Sign) { Write-Host "  cer  : $cer" }
Write-Host ''
Write-Host 'Dev inner-loop (Developer Mode on, no signing):'
Write-Host '  powershell -File packaging\register-dev.ps1 -PublishDir <folder with Nuvia.exe + NuviaShellExt.dll> -RestartExplorer'
