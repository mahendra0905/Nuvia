; Nuvia — per-user installer (Inno Setup 6)
; -----------------------------------------------------------------------------
; Packages the SELF-CONTAINED win-x64 publish of Nuvia. Because the publish is
; self-contained, no .NET runtime installation is required on the target PC.
;
; This is a PER-USER installer:
;   * PrivilegesRequired=lowest  -> no administrator prompt, installs for the
;     current user only.
;   * Installs into {localappdata}\Programs\Nuvia.
;   * The uninstall entry is written under the current user's hive, so Nuvia
;     appears in Windows Settings > Apps for this user.
;
; Build (Windows PowerShell): see installer\BUILD.md
;   The publish folder can be overridden:  ISCC.exe /DPublishDir="...path..." Nuvia.iss
; -----------------------------------------------------------------------------

#define MyAppName "Nuvia"
#define MyAppVersion "1.0.0"
#define MyAppExeName "Nuvia.exe"
#define MyAppPublisher "Nuvia"
#define MyAppTagline "The cloud that never fills up."

; Publish folder to package. Relative paths resolve against this .iss file's
; directory (installer\). Override on the ISCC command line with /DPublishDir=...
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

; Windows 11 shell-integration inputs (Phase 2). Each is optional at compile time:
; if the shell extension has not been built, the Source lines below skip cleanly and
; the installer still produces a working app-only setup (Phase 1 behaviour).
;   ShellExtDir     -> NuviaShellExt.dll (native IExplorerCommand handler, external content)
;   PackagingOutDir -> Nuvia.msix (+ Nuvia.cer) produced by packaging\build-shellext.ps1
;   SparseDir       -> loose AppxManifest.xml + Assets (dev -Register fallback)
#ifndef ShellExtDir
  #define ShellExtDir "..\artifacts\shellext\Release"
#endif
#ifndef PackagingOutDir
  #define PackagingOutDir "..\artifacts\packaging"
#endif
#define SparseDir "..\packaging\sparse"
#define PackagingScriptsDir "..\packaging"

[Setup]
; A STABLE AppId keeps upgrades associated with the same product across versions.
; Do not change this GUID between releases.
AppId={{B4E1B0B2-6D3A-4C9E-9A21-7F0C2E8A5D14}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments={#MyAppTagline}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductVersion={#MyAppVersion}
; Version resources of the generated setup .exe.
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} - {#MyAppTagline}

; Per-user install, no elevation.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Nuvia
DefaultGroupName={#MyAppName}

; Only ever install the 64-bit self-contained build on 64-bit Windows.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Output artifact.
OutputDir=..\artifacts\installer
OutputBaseFilename=Nuvia-Setup-{#MyAppVersion}
SetupIconFile=..\src\Nuvia.App\Assets\Nuvia.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Show the expected wizard flow. In Inno 6 the Welcome page is hidden by
; default, so it is explicitly re-enabled here along with the directory page.
DisableWelcomePage=no
DisableDirPage=no
; The single Start Menu group is fixed (see DefaultGroupName); no page needed.
DisableProgramGroupPage=yes

; Add/Remove Programs (Settings > Apps) presentation.
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

; Detect and handle a running Nuvia instance BEFORE replacing files. Inno's
; Restart Manager integration finds any file under {app} that is in use (e.g.
; a running Nuvia.exe) and offers to close it, so an in-place upgrade does not
; fail on locked files. We close but do not attempt to auto-restart the app.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
; Lead the wizard's welcome page with Nuvia's tagline, then keep the standard guidance.
WelcomeLabel2=Nuvia - The cloud that never fills up.%n%nThis will install {#MyAppName} version {#MyAppVersion} on your computer.%n%nIt is recommended that you close all other applications before continuing.

[Tasks]
; Optional desktop shortcut, SELECTED BY DEFAULT (no "unchecked" flag).
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; Package the entire self-contained publish tree.
; Excludes keep developer/diagnostic and user-data artifacts out of the package:
;   *.pdb              debug symbols
;   *.log / logs\      logs
;   config.json        developer credential config (lives in %LOCALAPPDATA% at runtime)
;   settings.json      user preferences (lives in %LOCALAPPDATA% at runtime)
;   *.session*         Telegram session material (lives in %LOCALAPPDATA% at runtime)
;   index.db*          local SQLite index (lives in %LOCALAPPDATA% at runtime)
;   tests\ / fixtures\ test fixtures
; NOTE: Nuvia.deps.json and Nuvia.runtimeconfig.json are REQUIRED to run and are
; intentionally NOT excluded.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; \
  Excludes: "*.pdb,*.log,logs\*,config.json,settings.json,*.session,*.session-journal,*.session.dat,session.dat,index.db,index.db-wal,index.db-shm,account.dat,*.account.dat,tests\*,test\*,fixtures\*,testfixtures\*,NuviaShellExt.dll"

; --- Windows 11 shell integration (native handler + sparse package) ---------
; NuviaShellExt.dll is EXTERNAL content the sparse package binds to; it must sit next
; to Nuvia.exe because the package is registered with -ExternalLocation "{app}".
; All lines use skipifsourcedoesntexist so a build that has not produced the shell
; extension still compiles and installs the app itself unchanged.
Source: "{#ShellExtDir}\NuviaShellExt.dll";     DestDir: "{app}";        Flags: ignoreversion skipifsourcedoesntexist
Source: "{#PackagingOutDir}\Nuvia.msix";        DestDir: "{app}";        Flags: ignoreversion skipifsourcedoesntexist
Source: "{#PackagingOutDir}\Nuvia.cer";         DestDir: "{app}";        Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SparseDir}\AppxManifest.xml";        DestDir: "{app}";        Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SparseDir}\Assets\*";                DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "{#PackagingScriptsDir}\register-shellext.ps1"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
; Start Menu shortcut.
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
; Optional desktop shortcut (only when the task is selected).
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Classic ("Show more options") cascading right-click entry for the CURRENT user.
; This is the UNIVERSAL end-user path: it needs NO code-signing, NO package identity
; and NO administrator rights (everything is written under HKCU). On Windows 11 it
; appears under "Show more options" (Shift+F10); on Windows 10 it shows directly on
; the context menu. The signed sparse package (packaged separately) is the OPTIONAL
; upgrade that promotes the same entry to the modern top-level Windows 11 menu.
;
; Structure: a parent verb with MUIVerb + an empty SubCommands value makes the shell
; build a submenu from the child "shell" subkey. Each child leaf runs the same entry
; point the app already handles:  Nuvia.exe --upload "<file>" --to saved|group|ask
; ("%1" is the right-clicked file). uninsdeletekey on the parent removes the whole tree.
Root: HKCU; Subkey: "Software\Classes\*\shell\Nuvia"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Upload to Nuvia"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\Nuvia"; ValueType: string; ValueName: "SubCommands"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\*\shell\Nuvia"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\*\shell\Nuvia\shell\1saved"; ValueType: string; ValueData: "Upload to Saved Messages"
Root: HKCU; Subkey: "Software\Classes\*\shell\Nuvia\shell\1saved\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --upload ""%1"" --to saved"
Root: HKCU; Subkey: "Software\Classes\*\shell\Nuvia\shell\2group"; ValueType: string; ValueData: "Upload to private group"
Root: HKCU; Subkey: "Software\Classes\*\shell\Nuvia\shell\2group\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --upload ""%1"" --to group"
Root: HKCU; Subkey: "Software\Classes\*\shell\Nuvia\shell\3ask"; ValueType: string; ValueData: "Choose destination in Nuvia..."
Root: HKCU; Subkey: "Software\Classes\*\shell\Nuvia\shell\3ask\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --upload ""%1"" --to ask"

[Run]
; Register the Windows 11 shell integration for the current user (best-effort; a
; failure here — e.g. an untrusted dev cert with Developer Mode off — never fails the
; install, the app just runs without the right-click menu). Only runs when the shell
; extension was actually packaged (see Check).
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\register-shellext.ps1"""; \
  StatusMsg: "Registering Windows shell integration..."; \
  Flags: runhidden; Check: ShellIntegrationPresent

; Finish-page option to launch Nuvia. Not shown during a silent install.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; Unregister the sparse package before files are removed. Best-effort and hidden.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\register-shellext.ps1"" -Remove"; \
  Flags: runhidden; RunOnceId: "UnregNuviaShellExt"; Check: ShellIntegrationPresent

[Code]
var
  DeleteLocalData: Boolean;

{ True only when the Windows 11 shell integration was packaged into this build, i.e.
  the shipped registration script is present in the install folder. Used to gate the
  Run and UninstallRun registration steps so an app-only build never invokes them. }
function ShellIntegrationPresent(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\register-shellext.ps1'));
end;

{ The one and only local data folder Nuvia ever writes to. }
function NuviaDataDir(): String;
begin
  Result := ExpandConstant('{localappdata}\Nuvia');
end;

{ Guard so a deletion can ONLY ever target %LOCALAPPDATA%\Nuvia itself, never a
  broader LocalAppData path. Both the full path and its final segment must match. }
function IsSafeNuviaDataDir(const Dir: String): Boolean;
var
  Expected: String;
begin
  Expected := NuviaDataDir();
  Result := (Dir <> '') and
            (CompareText(Dir, Expected) = 0) and
            (CompareText(ExtractFileName(Dir), 'Nuvia') = 0);
end;

{ Native uninstall confirmation UI.
  Uses a built-in Windows dialog (MsgBox) — reliable across Inno versions and in
  the uninstaller context, where creating a form from scratch is not dependable.
  The default button is "No", so local data is PRESERVED unless the user
  explicitly chooses to delete it. A SILENT uninstall never prompts and always
  preserves local data. Returning False cancels the whole uninstall. }
function InitializeUninstall(): Boolean;
var
  Choice: Integer;
begin
  DeleteLocalData := False;

  { Silent/very-silent uninstall: do not show UI, and preserve local data. }
  if UninstallSilent() then
  begin
    Result := True;
    exit;
  end;

  Choice := MsgBox(
    'You are about to uninstall Nuvia. Its program files and shortcuts will be removed from this PC.' + #13#10#13#10 +
    'Do you also want to delete local Nuvia data on this PC?' + #13#10 +
    'That is the folder %LOCALAPPDATA%\Nuvia: the local file index, your preferences, logs, and the Telegram session stored on this PC.' + #13#10#13#10 +
    'Your files in Telegram and your Telegram account are NOT deleted — only local data on this PC is removed.' + #13#10#13#10 +
    'Note: deleting the local session only removes this PC''s stored login. It does NOT by itself confirm that the session was revoked on Telegram''s servers. To be sure, log out from within Nuvia while online, or revoke the session from Telegram > Settings > Devices. Uninstall never contacts Telegram.' + #13#10#13#10 +
    'Yes  — remove Nuvia and delete local data.' + #13#10 +
    'No   — remove Nuvia but keep local data (recommended).' + #13#10 +
    'Cancel — do not uninstall.',
    mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON2);

  if Choice = IDCANCEL then
  begin
    Result := False; { user cancelled the uninstall entirely }
    exit;
  end;

  DeleteLocalData := (Choice = IDYES);
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  { Program files and shortcuts are removed by the uninstaller itself. Only the
    optional local-data deletion is handled here, after files are gone. }
  if CurUninstallStep = usPostUninstall then
  begin
    if DeleteLocalData then
    begin
      DataDir := NuviaDataDir();
      { Only ever delete the specific Nuvia data folder. }
      if IsSafeNuviaDataDir(DataDir) and DirExists(DataDir) then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;

{ ---------------------------------------------------------------------------
  OPTIONAL build-time credential injection — RELEASE BUILDS ONLY.

  This block is emitted ONLY when the installer is compiled with BOTH MyApiId and
  MyApiHash defined on the ISCC command line, e.g.:

      ISCC /DMyApiId=12345 /DMyApiHash=abcdef0123... installer\Nuvia.iss

  A normal pull-request or developer compile passes NEITHER define, so none of
  this code exists in the resulting installer and it behaves exactly as in
  Phase 7 — it writes no config.json and leaves credential setup to the user /
  docs\CREDENTIALS.md.

  When present, it writes the publisher api_id/api_hash to the per-user config at
  %LOCALAPPDATA%\Nuvia\config.json after files are installed, as a JSON object with
  a numeric api_id and a string api_hash, exactly as NuviaConfig.Load expects.

  SECURITY: api_id/api_hash are PUBLISHER credentials (from my.telegram.org), not
  end-user secrets. Once embedded here they ship inside the distributed installer
  .exe and are EXTRACTABLE from any shipped binary — this is documented in
  docs\CREDENTIALS.md and docs\CI_RELEASE.md. Rotate them if leaked. The release
  workflow supplies these defines from protected environment secrets and never
  prints their values.
  --------------------------------------------------------------------------- }
#if defined(MyApiId) && defined(MyApiHash)
procedure WritePublisherConfig();
var
  ConfigDir, ConfigPath, Json: String;
begin
  ConfigDir := ExpandConstant('{localappdata}\Nuvia');
  if not DirExists(ConfigDir) then
    ForceDirectories(ConfigDir);

  ConfigPath := ConfigDir + '\config.json';
  Json :=
    '{' + #13#10 +
    '  "api_id": {#MyApiId},' + #13#10 +
    '  "api_hash": "{#MyApiHash}"' + #13#10 +
    '}' + #13#10;

  { Overwrite=False: never clobber a config the user may already have placed. If a
    prior config exists, the app uses it; the release build does not force its own. }
  if not FileExists(ConfigPath) then
    SaveStringToFile(ConfigPath, Json, False);
end;
#endif

{ Emitted always, but the injection call is compiled in only for release builds. A
  developer/PR compile leaves this an empty no-op, so Phase 7 behaviour is unchanged. }
procedure CurStepChanged(CurStep: TSetupStep);
begin
#if defined(MyApiId) && defined(MyApiHash)
  if CurStep = ssPostInstall then
    WritePublisherConfig();
#endif
end;
