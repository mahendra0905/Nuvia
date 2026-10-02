# Building the Nuvia installer (Windows PowerShell)

This document describes how to produce `Nuvia-Setup-1.0.0.exe` from source and how
to test it on Windows. Every command here is **Windows PowerShell** — there are no
`.cmd` or `.vbs` helper scripts, by design. Run the commands from the repository
root (`D:\PROJECT NUVIA` in development) unless noted otherwise.

## Prerequisites

- **.NET 8 SDK** — to produce the self-contained publish.
  <https://dotnet.microsoft.com/en-us/download/dotnet/8.0>
- **Inno Setup 6** — provides the `ISCC.exe` command-line compiler.
  <https://jrsoftware.org/isinfo.php>
  The default install path is `C:\Program Files (x86)\Inno Setup 6\ISCC.exe`.
- **Windows 10/11 x64.** Both the publish and the installer must be built on
  Windows: Nuvia is a WPF app and the target is win-x64.

## Step 1 — Publish the self-contained app

The installer packages a **self-contained** win-x64 publish, so the target PC needs
no .NET runtime installed.

```powershell
dotnet publish .\src\Nuvia.App\Nuvia.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:PublishTrimmed=false `
  -o .\artifacts\publish\win-x64
```

After this completes, confirm the key runtime files exist — the app will not start
without them:

```powershell
'Nuvia.exe','Nuvia.dll','Nuvia.deps.json','Nuvia.runtimeconfig.json' |
  ForEach-Object {
    $p = Join-Path .\artifacts\publish\win-x64 $_
    '{0,-28} {1}' -f $_, (Test-Path $p)
  }
```

All four must report `True`. (`Nuvia.pdb` may also be present; the installer
excludes it, so it does not need to be removed by hand.)

## Step 2 — Compile the installer

```powershell
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' .\installer\Nuvia.iss
```

To package a publish folder in a non-default location, override `PublishDir`
(quote the path if it contains spaces):

```powershell
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' `
  /DPublishDir="D:\PROJECT NUVIA\artifacts\publish\win-x64" `
  .\installer\Nuvia.iss
```

On success the installer is written to:

```
.\artifacts\installer\Nuvia-Setup-1.0.0.exe
```

## Step 3 — Clean-machine test

Goal: verify a first-time install works on a PC that has never seen Nuvia and has
**no .NET runtime installed**. Use a spare user account, a fresh Windows VM, or a
Windows Sandbox instance.

1. **Pre-check (should be absent).** In the clean environment:

   ```powershell
   Test-Path "$env:LOCALAPPDATA\Programs\Nuvia"   # expect False
   Test-Path "$env:LOCALAPPDATA\Nuvia"            # expect False
   ```

2. **Run the installer** by double-clicking `Nuvia-Setup-1.0.0.exe`, or:

   ```powershell
   .\artifacts\installer\Nuvia-Setup-1.0.0.exe
   ```

   Confirm the wizard shows a **Welcome** page, then a **directory** page
   defaulting to `%LOCALAPPDATA%\Programs\Nuvia`, and that **no administrator/UAC
   prompt** appears (per-user install). The desktop-shortcut checkbox should be
   **checked by default**.

3. **Post-install checks:**

   ```powershell
   Test-Path "$env:LOCALAPPDATA\Programs\Nuvia\Nuvia.exe"          # expect True
   Test-Path "$env:USERPROFILE\Desktop\Nuvia.lnk"                  # expect True (if left checked)
   Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Nuvia\Nuvia.lnk"  # expect True
   ```

4. **Launch** Nuvia (finish-page checkbox, Start Menu, or desktop shortcut) and
   confirm it opens as a normal window with **no console/terminal window** and **no
   browser window**, on a machine with no .NET installed.

5. **Settings > Apps** — confirm **Nuvia** is listed and shows an **Uninstall**
   button.

6. **Uninstall — preserve data (default).** First, so there is local data to
   preserve, sign in once (or just launch so `%LOCALAPPDATA%\Nuvia` is created),
   then uninstall from Settings > Apps. In the confirmation dialog, click **No**
   ("remove Nuvia but keep local data" — the default button).

   ```powershell
   Test-Path "$env:LOCALAPPDATA\Programs\Nuvia"   # expect False (program files gone)
   Test-Path "$env:LOCALAPPDATA\Nuvia"            # expect True  (local data preserved)
   ```

7. **Uninstall — delete data (opt-in).** Reinstall, create local data again, then
   uninstall and click **Yes** ("remove Nuvia and delete local data") in the
   confirmation dialog.

   ```powershell
   Test-Path "$env:LOCALAPPDATA\Nuvia"            # expect False (data removed by opt-in)
   ```

8. **Silent uninstall preserves data.** Reinstall, create local data, then run the
   uninstaller silently. The per-user uninstaller registration is under HKCU:

   ```powershell
   $u = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' |
         Where-Object { $_.DisplayName -eq 'Nuvia' }).UninstallString
   # UninstallString is the unins000.exe path; append /SILENT
   Start-Process -FilePath $u -ArgumentList '/SILENT' -Wait
   Test-Path "$env:LOCALAPPDATA\Nuvia"            # expect True (silent never deletes data)
   ```

## Step 4 — Upgrade-in-place test

Goal: verify a newer build installs over an existing one without producing a second
entry and without failing on a running instance. Because the `AppId` GUID in
`Nuvia.iss` is stable, Inno treats a second install as an upgrade of the same
product.

1. Install the current `Nuvia-Setup-1.0.0.exe` as in Step 3.

2. **Leave Nuvia running.** This exercises the running-instance handling
   (`CloseApplications=yes`): the wizard should detect the open app and offer to
   close it via Restart Manager rather than failing on a locked `Nuvia.exe`.

3. Produce a second installer to represent the upgrade. Either rebuild after a code
   change, or bump `MyAppVersion` in `Nuvia.iss` (e.g. to `1.0.1`) and recompile.
   Run the new installer.

4. **Expected during upgrade:**
   - It installs into the **same** directory (`%LOCALAPPDATA%\Programs\Nuvia`)
     without asking again in a default flow.
   - The running instance is closed by the installer before files are replaced.
   - No UAC prompt.

5. **Post-upgrade checks:**

   ```powershell
   # Exactly one Nuvia entry in Add/Remove Programs (HKCU, per-user install):
   Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' |
     Where-Object { $_.DisplayName -eq 'Nuvia' } |
     Select-Object DisplayName, DisplayVersion

   # Local data survives an upgrade untouched:
   Test-Path "$env:LOCALAPPDATA\Nuvia"            # expect True
   ```

   There must be **one** entry (its `DisplayVersion` reflecting the newer build),
   not two, and `%LOCALAPPDATA%\Nuvia` must be unchanged — an upgrade never deletes
   local data.

## Notes

- The installer never contacts Telegram. Nothing in `Nuvia.iss` performs any
  network call; uninstall only removes local files (and only the specific
  `%LOCALAPPDATA%\Nuvia` folder when the user opts in).
- Deleting the local session during uninstall removes only this PC's stored login.
  It does not by itself revoke the session on Telegram's servers — to be certain,
  log out from within Nuvia while online, or revoke it from
  Telegram > Settings > Devices.
