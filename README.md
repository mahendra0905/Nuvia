# Nuvia

A native Windows desktop file vault backed by Telegram cloud storage.

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) or later
- Windows 10/11 (WPF runtime is Windows-only)
- [Visual Studio 2022](https://visualstudio.microsoft.com/vs/) (optional — for GUI editing)

## Opening the solution

### Visual Studio 2022

1. Open `Nuvia.sln` from the repository root.
2. Ensure `src/Nuvia.App` is set as the startup project.
3. Press **F5** to build and run.

### Command line

```bash
# Restore dependencies
dotnet restore Nuvia.sln

# Build
dotnet build Nuvia.sln -c Debug

# Run
dotnet run --project src/Nuvia.App/Nuvia.App.csproj
```

> **Note:** WPF applications must be built and run on Windows. Linux and macOS can be used for source editing, static analysis and some lint checks, but native WPF execution requires the Windows Desktop runtime.

## Project structure

```
Nuvia.sln
src/
  Nuvia.App/
    Nuvia.App.csproj    — .NET 8 WPF project
    App.xaml(.cs)        — Application entry point; wires the signed-in account into the window
    MainWindow.xaml(.cs) — Toolbar, indexed file list, empty state, transfer progress, status bar, full-window Settings page (destination, Saved Messages, your data, About + disclosure)
    LoginWindow.xaml(.cs)— Phone → code → optional 2FA password state machine
    AboutWindow.xaml(.cs)— Modal About dialog with disclosure
    CreateGroupWindow.xaml(.cs) — Native confirm dialog (editable name) for a private storage group
    RenameWindow.xaml(.cs) — Native confirm dialog for renaming a file's local display name only
    Styles.xaml           — Shared styles and font defaults
    Services/
      TelegramAuthService.cs   — WTelegramClient sign-in + logout; session under %LOCALAPPDATA%\Nuvia\session
      ITelegramStorage.cs      — Create group, upload, download, refresh metadata
      TelegramStorageService.cs— WTelegramClient implementation of the above
      UnavailableStorageService.cs — Refuses everything when no account is signed in
      FileTransferCoordinator.cs   — Ordering rules for upload/download (index and destination last)
      StorageDestination.cs    — Destination model: Saved Messages or a managed private group
      SettingsStore.cs         — Atomic JSON preferences (%LOCALAPPDATA%\Nuvia\settings.json); no secrets
      IndexStore.cs            — SQLite local index (%LOCALAPPDATA%\Nuvia\index.db)
      IndexedFile.cs           — Indexed row + saved-message reference
      FileNameSafety.cs        — Sanitises remote filenames (untrusted path input)
      NuviaConfig.cs           — Credential loading and local paths
    Assets/
      Nuvia.ico           — Application icon
tests/
  Nuvia.Tests/            — xUnit network-free unit tests (FileNameSafety, StorageDestination)
installer/
  Nuvia.iss               — Inno Setup 6 per-user installer (optional release credential injection)
  BUILD.md                — PowerShell build + clean-machine/upgrade test procedures
.github/workflows/
  ci.yml                  — PR/push: restore, build Release, run tests (no secrets)
  release.yml             — tag v*.*.*: publish, compile installer, checksum, draft release
docs/
  CI_RELEASE.md           — CI/release pipeline, secret setup, and manual-only checks
```

## Local folders

Nuvia lets you organise a location (Saved Messages, or one managed group) into **folders**.
A right-click on empty space in the file area shows **New folder…** next to *New group…*;
you can navigate into a folder (double-click, the **Up** button, or the breadcrumb), rename
or delete it, and move files into it with **Move to folder ▸**. Folders are disabled on
*Home / All files* — they belong to a single location.

Folders are a **Nuvia-local** concept. Telegram itself has no folders, so the folder tree
lives entirely in the local SQLite index (`index.db`); there are no on-disk paths and nothing
new is created on Telegram. To make folders survive an uninstall/reinstall, each **Nuvia
upload** carries its folder path inside an **invisible, zero-width marker in the message
caption** — the caption still renders empty in Telegram, and a root-level file is encoded
exactly as before, byte-for-byte. On a fresh install, the rebuild reads that marker and
re-creates each file's folder; on a later rebuild over an existing index, a file you have
already moved locally **stays where you put it** (the marker only seeds a file's folder when
the row is first created, never on a refresh).

Honest limits:

- **Renaming a folder rewrites the marker on every Nuvia-uploaded file in that subtree**,
  one message at a time and best-effort. If some edits fail (offline, a group out of reach),
  the status line reports exactly how many markers were updated versus how many files were in
  the tree — it is never reported as a clean success.
- **External files** (anything added to the chat outside Nuvia) can be moved into a folder
  locally, but their caption is **not** tagged — writing Nuvia's marker onto a file Nuvia did
  not upload would make a reinstall falsely claim it. Those files therefore return to the
  location root after a reinstall.
- The folder path must fit the caption budget (encoded path ≤ 200 UTF-8 bytes, each name ≤ 60
  chars, depth ≤ 8). A move or rename that would exceed it is **refused up front** with a
  message, never silently truncated.

## Current phase

**Phase 8** — Windows CI and release automation with GitHub Actions on `windows-latest`, split
into two workflows with deliberately separated trust. `.github/workflows/ci.yml` runs on pull
requests and pushes to `main`: it installs the .NET 8 SDK, restores, builds Release, and runs the
network-free unit tests in `tests/Nuvia.Tests` (which cover `FileNameSafety` and
`StorageDestination` — no account login, no network, no filesystem). CI requests only
`contents: read` and holds **no secrets**, so it is safe against untrusted fork pull requests.

`.github/workflows/release.yml` is the only workflow that touches publisher credentials, and it is
isolated: it triggers **only** on `v*.*.*` tags, runs in the protected `release` GitHub Environment
(configure required reviewers there — that approval is the "explicitly approved version tag" gate),
and requests only `contents: write`. It validates that the git tag, the csproj `<Version>`, and the
installer `MyAppVersion` all agree; publishes a **self-contained win-x64** build (**not**
single-file) with **no PDBs**; installs a **pinned Inno Setup 6** toolchain; compiles
`Nuvia-Setup-1.0.0.exe` while injecting `api_id`/`api_hash` from environment secrets **without
printing them**; generates a **SHA-256** checksum; uploads the installer and checksum; and creates a
**draft** release pending manual Windows acceptance. Third-party actions are pinned to full commit
SHAs. A real user's Telegram **session is never used as a CI secret**.

The credential injection lives in `installer/Nuvia.iss` behind
`#if defined(MyApiId) && defined(MyApiHash)`, so a normal PR/developer compile (no defines) behaves
exactly as in Phase 7 and writes no `config.json`; only the release compile activates it. Because
`api_id`/`api_hash` are embedded in the shipped installer, they are **extractable from the
distributed binary** — treat them as rotate-able publisher credentials. See `docs/CI_RELEASE.md`
for setup, the required secrets/environment, the pinned-SHA verification list, and the full list of
checks that remain **manual** (WPF UI/UX, real upload/download, installer/clean-machine/upgrade/
uninstall UX, and that injected credentials actually sign in).

> **A green build is not acceptance.** CI/release prove the code compiles, the pure-logic tests
> pass, and the artifacts are produced. They do **not** prove the WPF UX, the installer UX, or a
> clean-machine install work. Development happens in a Linux sandbox with no .NET, no `ISCC.exe`,
> and no way to run WPF, so **nothing in Phase 8 was compiled or executed by the author** — the
> workflows are authored, not verified on this machine.

### Phase 7 recap — per-user Windows installer

**Phase 7** — a per-user Windows installer built with **Inno Setup 6**
(`installer\Nuvia.iss`). It packages the self-contained win-x64 publish, so the
target PC needs **no .NET runtime**, and installs **per user** into
`%LOCALAPPDATA%\Programs\Nuvia` with `PrivilegesRequired=lowest` (no
administrator/UAC prompt). The wizard shows the Welcome and directory pages, offers
a desktop shortcut **checked by default** alongside a Start Menu shortcut, and can
launch Nuvia from the finish page. A **stable `AppId`** makes a later build install
in place as an upgrade rather than a second entry, and `CloseApplications=yes` uses
the Restart Manager to close a running Nuvia before replacing files. The output is
`artifacts\installer\Nuvia-Setup-1.0.0.exe`, its version resources name the product
**Nuvia**, and it appears in **Settings > Apps**. The package excludes `*.pdb`,
logs, developer `config.json`, user `settings.json`, `*.session*`, `index.db*` and
test fixtures, while keeping the required `Nuvia.deps.json` and
`Nuvia.runtimeconfig.json`.

Uninstall shows a **native confirmation dialog** (Yes / No / Cancel) whose default
button is **No** — so local data is **preserved unless the user explicitly opts in**.
**No** removes only program files and shortcuts and keeps `%LOCALAPPDATA%\Nuvia`
(local index, preferences, logs, session); **Yes** additionally deletes that one
folder — and only that folder, guarded so a deletion can never target a broader
LocalAppData path; **Cancel** aborts the uninstall. A **silent** uninstall never
prompts and always preserves local data. The dialog states plainly that remote
files and the Telegram account are **not** deleted, and that deleting the local
session does **not** by itself confirm server-side revocation (log out in-app while
online, or revoke from Telegram > Settings > Devices). The installer **never
contacts Telegram**. There are no `.cmd`/`.vbs` scripts; PowerShell build,
clean-machine and upgrade-in-place test procedures live in `installer\BUILD.md`.

> **Uninstall UI note:** the confirmation is a native Windows dialog rather than a
> single checkbox form. Inno Setup 6.7.3 rejected the `CreateCustomForm` factory at
> compile time in this project and a from-scratch `TSetupForm` needs a form resource
> that is not present in the uninstaller, so the reliable built-in dialog is used
> instead. It carries the same choice (default = keep local data, explicit opt-in to
> delete) and the same disclosures.

> **Not verified on this machine:** the installer was authored but **not compiled
> or run** in the development sandbox (Linux, no `ISCC.exe`, WPF/Windows-only). The
> tests in `installer\BUILD.md` are procedures to run on Windows, not passed
> results. Do not treat installer success as confirmed from source inspection.

### Phase 6C recap — settings and complete logout

**Phase 6C** — a native Settings window and a complete, honest logout. Settings lets you pick the
default upload destination (Saved Messages, or a managed private group when one exists),
explains plainly that the file list is stored only on this PC and that a local rename does not rename
the remote file, and offers a **"Log out and delete local session"** action behind its own
confirmation. The default destination is now persisted by a dedicated `SettingsStore` as atomic JSON
under `%LOCALAPPDATA%\Nuvia\settings.json`, scoped per account and holding **no** credentials, login
codes, 2FA passwords or session material; a missing or corrupt file recovers silently to the safe
default (Saved Messages). Logout uses the supported server-side revocation
(`Auth_LogOut`) when online, disposes the client, clears in-memory secrets, and deletes the local
session file — and **only** the session file: it never deletes remote messages, files, groups or the
account, and never deletes the local index. When offline it still removes the local session but says
clearly that the server-side session could not be revoked. A logout returns to the sign-in screen
**in the same process** (no second app launch), and a logout is refused while a transfer is running.
See the Phase 6C recap below.

### Phase 6C recap — settings and complete logout

- **`SettingsStore` (atomic, safe, non-sensitive):** preferences serialize to a temp file in the same
  directory, flush to disk, then swap into place (`File.Replace`, else `File.Move`), so a crash
  mid-write leaves either the old file or the new one — never a truncated one. A missing, empty or
  corrupt file is treated as "no preferences" and returns Saved Messages rather than throwing. The
  on-disk shape holds only a per-account `defaultDestination` of `"self"` or `"group"` — never a
  credential, code, password or session.
- **Account-scoped by design:** preferences are keyed by Telegram account id, so one account's default
  can never be read for another. This complements the existing index scoping — a different account
  still sees an empty list, never the previous account's rows.
- **Settings window is a pure value dialog:** like `CreateGroupWindow`/`RenameWindow` it holds no
  services and performs no network or disk writes itself; it returns the chosen destination or a
  logout request, and `MainWindow` (destination) or `App` (logout) acts on it. The managed-group
  option is only offered when a usable group exists; otherwise it is shown disabled with a note.
- **Logout is honest about what it does and does not touch:** server-side revocation via `Auth_LogOut`
  when reachable, then dispose + clear secrets + delete the session file. It never deletes remote
  messages, files, groups or the account, and it never deletes `index.db`. The confirmation dialog
  and the post-logout message both state this explicitly.
- **Offline is distinguished, not hidden:** `LogOutAsync` returns `ServerConfirmed` or
  `LocalOnlyServerUnconfirmed`; when offline the user is told the local session was removed but the
  server-side session may remain until revoked elsewhere or it expires.
- **In-process return to login:** `App` brings up a fresh sign-in window *before* closing the old main
  window, so `ShutdownMode=OnLastWindowClose` cannot terminate the app — no second process is spawned.
- **Active-transfer guard:** if an upload or download is in flight, logout is refused with a clear
  message rather than tearing down the transfer.

**Phase 6B** — local search, local rename, and Refresh, all working entirely against the local index
with no new Telegram calls. Search filters the indexed list by display name as you type
(case-insensitive, ordinary-filename matching); it never scans Telegram history, and it shows both a
filtered count ("*3 of 12 files match …*") and a distinct "No matching files" empty state when a
search hides every row. Rename changes **only** the local display name of the selected file — via an
explicit Rename button, a right-click menu item, or **F2** — through a native confirmation dialog
that validates empty and over-length names. The original filename and every remote identifier are
preserved, and nothing is sent to Telegram: the message, its caption and the stored document are
untouched. Refresh simply re-reads the local SQLite list (**F5**); its wording makes clear it does
not import files from Telegram. Keyboard shortcuts were added only for these implemented actions
(**F2** rename, **F5** refresh, **Ctrl+F** focus search, **Esc** clear search while the box is
focused). See the Phase 6B recap below.

### Phase 6B recap — local search, rename, and refresh

- **Search is local only:** it filters rows already read from `index.db` by `DisplayName`
  (case-insensitive, `OrdinalIgnoreCase`). No `messages.search` or history scan is ever issued. The
  full set is kept in memory (`_allRows`) and the visible list (`_rows`) is that set passed through
  the filter, so clearing the search restores everything without a re-query.
- **Filtered count + empty state:** the status bar shows "*N of M file(s) match …*", and when a filter
  hides every row the list shows "No matching files" (distinct from the "nothing indexed yet" state).
- **Rename touches one local column:** `IndexStore.UpdateDisplayName` updates only `display_name`,
  scoped by `id` **and** `owner_id`. It never writes `original_file_name` or any remote column, and it
  never contacts Telegram — captions, messages and documents are left exactly as they are.
- **Validation, twice:** the native `RenameWindow` rejects empty and over-length names (cap =
  `IndexStore.MaxDisplayNameLength`, 255); the store re-checks the same bounds as a backstop.
- **Download filename stays separate:** the save dialog proposes the (possibly renamed) display name
  but sanitises it through `FileNameSafety` independently, falling back to the sanitised original
  filename — a local label is never trusted directly as a filesystem path.
- **Refresh is a local reload:** it re-reads the account's rows from SQLite and re-applies the current
  filter. No upload, message send, or group creation happens.
- **Incidental fix:** the "Uploaded (UTC)" column previously bound to a non-existent `DateDisplay`
  property (so it rendered blank); it now binds `UploadedDisplay`, and that value is formatted in UTC
  to match the column header.

**Phase 6A** — optional private storage groups, as an alternative upload destination to Saved
Messages. Nuvia can create **private supergroups** on the signed-in account (via
`channels.createChannel` with `megagroup: true`), each only after an explicit confirmation dialog with
an editable name. The account is each group's only member: no contacts are read, no members are added,
no public username is set, and no invite link is generated. After a successful create, the group's
identifiers (channel id + access hash, account-scoped) are saved locally and the group appears in the
left "Locations" pane alongside Saved Messages. **Uploads go wherever the open location is**
(Explorer-style): inside a group they go to that group, inside Saved Messages to Saved Messages, and at
Home / All files to the fallback default chosen in Settings. An account may keep as many groups as
Telegram allows — Nuvia imposes no cap of its own. If a group later becomes unreachable, uploads and
downloads report a clear "storage group unavailable" error and Nuvia **never silently recreates it** —
the user opens another location or creates a new group. See the Phase 6A recap below.

### Phase 6A recap — optional private storage groups

- **Why a private supergroup:** it is the only group type a user account can create *solo*. A basic
  group (`messages.createChat`) requires a non-empty users list (i.e. adding contacts), and a
  broadcast channel is forbidden by the product. The choice is documented in
  `Services/StorageDestination.cs` and `Services/TelegramStorageService.cs`.
- **Creation is explicit only:** the "New group…" entry (left pane, Settings, and the file-area
  right-click menu) opens a native dialog with an editable, pre-filled name; nothing is created unless
  the user confirms. Never on startup, login, refresh, or as a retry of another action.
- **Multiple groups per account:** `storage_groups` is keyed by `(account_id, channel_id)`, so each
  create adds a row instead of replacing the previous one. The left pane lists every group, each with
  its own Open / Rename / Delete menu; deleting one group removes only that group's row and files.
- **Destination selection:** the upload target follows the open location. A separate **fallback
  default** (used at Home / All files) persists per account and is stored in `SettingsStore`
  (`settings.json`); a group preference is only honoured while a usable group still exists, otherwise
  it falls back to Saved Messages and is never recreated. *(Before Phase 6C this preference lived in
  the index's `account_prefs` table; a preference set before 6C resets once to Saved Messages.)*
- **Account scope preserved:** group identifiers and the preference are both keyed by account id, so
  another account can never see or use these groups.
- **No scope creep:** no chat browsing, contact export, or member management was added — only the
  destinations (Saved Messages, any managed group).

**Phase 5** — reliable transfer state and error handling on top of the single-file Saved Messages
upload/download from Phase 4. One transfer runs at a time (no bulk processing); it moves through
explicit states (Idle, Preparing, Uploading, Downloading, WaitingForFloodLimit, Cancelling,
Completed, Failed, Cancelled) reported over a single progress channel that never blocks the UI
thread. Cancellation works mid-transfer and during a flood-wait, a long `FLOOD_WAIT` is shown in the
status bar and waited out for exactly the server-provided duration before a single retry, and every
handled failure (network, revoked session, file too large, missing/changed local file, access
denied, disk full, deleted remote message, index failure) is surfaced from a fixed catalog with no
credentials, paths or message content in the message or the log.

Upload file-size handling uses the documented **user-account** ceilings (2 GB free / 4 GB Premium,
verified 2026-09-25 — see `Services/TelegramFileLimits.cs`). Nuvia only refuses up front what no
account could store; between the tiers it lets the server decide and reports its answer honestly.
Bot API limits do not apply here.

> **Note on phase numbering:** `docs/PHASES.md` sketched a broader Phase 4/5 (private-channel storage,
> a concurrent queue, pause/resume). Nuvia deliberately took the narrower, safer path — Saved
> Messages, one transfer at a time. The roadmap in `docs/PHASES.md` still needs updating to match.

### Phase 4 recap — single-file Saved Messages upload/download

- **Upload:** native file picker, one file, explicit Upload click. The file is sent as a *document*
  to Saved Messages, and the index row is written **only after** the remote upload succeeded.
- **Download:** one selected row, native save picker, download to a temporary partial file, then
  move to the final name. An existing destination is never overwritten without confirmation, and an
  expired remote file reference is refreshed once and retried.
- **Index:** SQLite at `%LOCALAPPDATA%\Nuvia\index.db`, versioned migrations, parameterised SQL,
  account-scoped rows, UTC timestamps. Original filename is stored separately from the local
  display name.

### This is a local index, not Telegram-history sync

`index.db` contains **only** rows for files Nuvia itself uploaded from this machine. It is not a
mirror of your Telegram history, so:

- files sent from other devices or from other Telegram clients will not appear;
- deleting `index.db` does not delete anything from Telegram;
- clearing a row does not remove the remote copy.

### When the upload succeeds but indexing fails

If the remote upload succeeds and the local index write then fails, Nuvia reports exactly that —
the file **may already exist in your Saved Messages**. It deliberately does **not** re-upload,
because the first copy is already stored server-side. Re-uploading would silently create duplicates.

### Account switching

Every row records the owning account id, and reads are scoped to it. Signing into a different
account shows an empty list rather than the previous account's rows.

## License

Nuvia is an unofficial local application. It uses the Telegram API and is not affiliated with Telegram.