# Nuvia — Windows 11 shell integration (`packaging/`)

This folder builds the **modern Windows 11 top-level right-click menu** entry for Nuvia —
the same class of entry PDFelement / WinRAR / PowerToys use, with a flyout:

```
Right-click any file
   └─ Upload to Nuvia ▸
         ├─ Upload to Saved Messages
         ├─ Upload to private group
         └─ Choose destination in Nuvia…
```

Pressing a leaf runs `Nuvia.exe --upload "<file>" --to saved|group|ask`; the C# app
(single-instance) does all the real work and shows only a transfer window.

## Why two pieces

Windows 11's top-level menu only accepts a native **`IExplorerCommand` COM handler**
registered under a **package identity**. Plain `HKCU` verbs land under "Show more
options" only. So there are exactly two moving parts:

| Piece | What it is | Where it lives at runtime |
|-------|------------|---------------------------|
| `NuviaShellExt.dll` | native C++ in-proc COM handler (dumb launcher — no Telegram/session/secrets) | next to `Nuvia.exe` in the install folder |
| `Nuvia.msix` (sparse package) | app-identity + COM/menu registration only (manifest + logos) | registered per-user, points back at the install folder via `-ExternalLocation` |

The `.msix` contains **only** the manifest + logos. `Nuvia.exe` and `NuviaShellExt.dll`
are *external content* — shipped by the Inno installer, bound at register time. Source:
`src/Nuvia.ShellExt/` (handler) and `packaging/sparse/AppxManifest.xml` (manifest).

CLSID (stable): `{32687C4B-2D15-4FB1-8F7D-54C3CC3A423D}`.

## Build

```powershell
# Build the handler DLL + pack the sparse package (unsigned):
powershell -File packaging\build-shellext.ps1

# …or also sign it with a self-signed DEV certificate (subject must match the
# manifest Publisher, CN=Nuvia) and export the public .cer:
powershell -File packaging\build-shellext.ps1 -Sign
```

Outputs (under `artifacts\`):
- `shellext\Release\NuviaShellExt.dll` — the handler.
- `packaging\Nuvia.msix` — the sparse package (+ `Nuvia.cer` when `-Sign`).

Requires Visual Studio C++ build tools + the Windows SDK (MakeAppx/SignTool). The `.msix`
is packed with `/nv` (skip semantic validation) because the exe/dll are external.

## Test it on your machine (no full install)

The registered package needs an **external location that contains both `Nuvia.exe` and
`NuviaShellExt.dll`**. Two supported paths:

### A. Developer Mode + loose manifest (no signing)
Turn on **Settings ▸ Privacy & security ▸ For developers ▸ Developer Mode**, then:

```powershell
# Publishes to artifacts\publish\win-x64 and copies the DLL in next to Nuvia.exe:
dotnet publish src\Nuvia.App\Nuvia.App.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\win-x64
powershell -File packaging\register-dev.ps1 -RestartExplorer
# remove again:
powershell -File packaging\register-dev.ps1 -Unregister -RestartExplorer
```

### B. Signed package (mirrors production; needs the cert trusted once)
```powershell
powershell -File packaging\build-shellext.ps1 -Sign
# one-time trust (ELEVATED PowerShell) — self-signed only:
Import-Certificate -FilePath artifacts\packaging\Nuvia.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
# then, as the normal user, point -ExternalLocation at a folder holding BOTH files:
Add-AppxPackage -Path artifacts\packaging\Nuvia.msix -ExternalLocation <folder with Nuvia.exe + NuviaShellExt.dll>
```

After (un)registering, restart Explorer (`Stop-Process -Name explorer -Force`) so the menu
refreshes.

## Installer behavior (`installer/Nuvia.iss`)

The installer packages `NuviaShellExt.dll`, `Nuvia.msix`, `Nuvia.cer`, the loose
`AppxManifest.xml` + `Assets\`, and `register-shellext.ps1` into `{app}`, then runs
`register-shellext.ps1` for the current user. That script:
1. tries the **signed msix** (`Add-AppxPackage -Path Nuvia.msix -ExternalLocation {app}`), then
2. falls back to the **loose manifest** (`-Register`, needs Developer Mode).

It is **best-effort**: if neither succeeds the install still completes and the app runs —
only the right-click menu is absent. Uninstall runs `register-shellext.ps1 -Remove`.

## End-user menu: two tiers

The installer delivers the right-click entry in **two independent tiers** so every end
user gets *something* with no signing, and a trusted cert only *upgrades* the experience:

| Tier | Mechanism | Where it shows | Needs |
|------|-----------|----------------|-------|
| **Universal (always)** | classic cascading verbs under `HKCU\Software\Classes\*\shell\Nuvia` (Inno `[Registry]`) | Win11 **"Show more options"** (Shift+F10); directly on Win10 | nothing — no signing, no package, no admin |
| **Top-level (optional)** | signed sparse MSIX + `IExplorerCommand` handler | modern **top-level** Win11 menu (like PDFelement/WinRAR) | a **trusted** code-signing cert (see below) |

The classic verbs run the same entry point as the handler — `Nuvia.exe --upload "%1"
--to saved|group|ask` — so both tiers behave identically once clicked. `uninsdeletekey`
removes the classic tree on uninstall.

> If you later ship a trusted-signed handler, the same "Show more options" list would show
> **two** "Upload to Nuvia" entries (handler + HKCU verbs). Drop the `[Registry]` block then.

## Signing decision (pending — only affects the top-level tier)

- **Dev/test:** self-signed `CN=Nuvia` (this folder's default). Registering it requires
  either Developer Mode (loose manifest) or trusting the cert once as admin.
- **Production:** a **purchased/trusted code-signing certificate** whose subject is set as
  the manifest `Publisher` (replace `CN=Nuvia`). With a trusted cert the per-user installer
  registers the msix **with no elevation and no Developer Mode** — the intended shipping
  experience. The self-signed vs. per-user/no-admin tension only exists for dev testing.
  Concrete production routes (as of 2026):
  - **Azure Artifact Signing** (formerly "Trusted Signing"), MS-managed, from ~$9.99/mo —
    the recommended MSIX route, *but* Public-Trust eligibility is limited to individual
    developers in the USA/Canada and organizations with 3+ years of tax history.
  - **Purchased OV code-signing cert** (DigiCert/Sectigo/etc.) — globally available; the
    private key must live on a hardware token or cloud HSM, and validity is now capped at
    ~459 days (Feb 2026 CA/Browser Forum rule).

## Known limitations

- **Files only** (`ItemType Type="*"` for the handler; `*\shell` for the classic verbs).
  Directories and the folder background are out of scope.
- **Multi-select:** the classic verbs run once per selected file (Explorer's default for
  `%1` verbs), the handler takes the first file; either way the app processes uploads per
  its single-flight design.
- The right-click menu appearing/launching is a **manual, Windows-only acceptance step** —
  it cannot be verified in a Linux sandbox or by a build alone. The classic "Show more
  options" tier needs no signing, so it can be verified by simply running the installer.
