# Nuvia — Phases & Acceptance Gates

> **Product:** Nuvia — A native Windows desktop file vault backed by Telegram cloud storage.
> **Stack:** C# / .NET 8, WPF, WTelegramClient, SQLite, Inno Setup 6.

---

## Phase 0 — Scaffold & specification

**Goal:** Establish the repository layout, specification documents, and ignore rules. No application code.

**Deliverables:**
- `docs/SPEC.md` — product specification
- `AGENTS.md` — implementation rules
- `docs/PHASES.md` — this file
- `.gitignore` — Visual Studio / .NET / WPF / installer patterns

**Acceptance gates:**
- [ ] All four files exist at the correct paths
- [ ] `.gitignore` covers `bin`, `obj`, `.vs`, `publish/`, installer output, credential files, session files, SQLite databases (`.db`, `.db-wal`, `.db-shm`), logs, and local account data
- [ ] `docs/SPEC.md` matches the supplied product specification
- [ ] `AGENTS.md` matches the supplied implementation rules
- [ ] `docs/PHASES.md` lists all phases 0–9 with acceptance gates
- [ ] No application code, NuGet packages, CI config, or installer script present
- [ ] Linux editing note documented

---

## Phase 1 — Shell window & navigation layout

**Goal:** A WPF window titled "Nuvia" with the shell layout. No Telegram integration, no file operations.

**Deliverables:**
- .NET 8 WPF project (`Nuvia.csproj`)
- `MainWindow.xaml` / `.cs` with:
  - Title bar: "Nuvia"
  - Left sidebar with placeholder navigation (Files, Transfers, Trash, Settings)
  - Content area showing the selected page placeholder
  - Placeholder pages for Files, Transfers, Trash, Settings
- Unofficial-app disclosure banner (dismissible)

**Acceptance gates:**
- [ ] `dotnet build` succeeds with zero warnings
- [ ] Window launches on Windows, title reads "Nuvia"
- [ ] Sidebar renders four navigation items
- [ ] Clicking a nav item switches the content area
- [ ] Disclosure banner appears on first launch
- [ ] No Telegram packages referenced

---

## Phase 2 — Local file indexing & management

**Goal:** Local SQLite database indexes files on disk. Browse, search, rename and delete files locally. No Telegram uploads.

**Deliverables:**
- SQLite database created at `%LOCALAPPDATA%\Nuvia\index.db`
- Index service watches a configurable root folder
- Files page shows real indexed files (name, size, date, type)
- Search bar filters the file list
- Right-click context menu: Rename, Delete, Copy Path
- Folder tree in sidebar for navigation
- Persistence service project (`Nuvia.Persistence`)

**Acceptance gates:**
- [ ] Index service scans and records files on startup
- [ ] New files added outside the app are picked up (refresh or watcher)
- [ ] File list shows real files from disk
- [ ] Search filters in real time
- [ ] Rename changes the file on disk
- [ ] Delete moves file to system Recycle Bin
- [ ] Copy Path copies the absolute path to clipboard
- [ ] Folder tree matches the real directory structure
- [ ] Index survives app restart

---

## Phase 3 — Telegram login & session management

**Goal:** Authenticate the user via Telegram MTProto. No file upload/download yet.

**Deliverables:**
- WTelegramClient integration (`Nuvia.Telegram` service project)
- Login flow: phone number → code → 2FA password (if needed)
- Session persisted to `%LOCALAPPDATA%\Nuvia\session.dat`
- Session restored on restart; auto-reconnect on network loss
- Settings page shows login status, phone number, logout button
- Login/logout status reflected in sidebar (e.g. icon change)

**Acceptance gates:**
- [ ] Login with phone + code + optional 2FA succeeds against real Telegram
- [ ] Session survives app restart without re-authentication
- [ ] Logout clears session data and returns to logged-out state
- [ ] Settings page shows phone number when logged in
- [ ] Network disconnect does not crash; auto-reconnect works
- [ ] No credentials, phone numbers or codes appear in logs
- [ ] Unofficial-app disclosure visible at login screen
- [ ] No file upload, download, chat browsing, or contact export implemented

---

## Phase 4 — Upload & download engine

**Goal:** Transfer files between the local index and Telegram cloud storage. No vault organisation yet.

**Deliverables:**
- Upload: select file(s) from the local index → upload to Telegram as document messages in a private channel
- Download: select an uploaded file → download to a local folder
- Transfers page shows progress bars, speeds, ETA, status (queued/uploading/downloading/completed/failed)
- Concurrent transfers queue (configurable limit, default 3)
- Pause/resume/cancel individual transfers
- Retry failed transfers
- Transfer service project (`Nuvia.Transfer`)

**Acceptance gates:**
- [ ] Upload a file from the indexed folder → appears in Telegram private channel
- [ ] Download from the transfer list → file lands on disk with original name
- [ ] Progress bar updates during transfer
- [ ] Pause/resume works mid-transfer
- [ ] Cancel stops the transfer and cleans up partial files
- [ ] Queue limit is respected (3rd concurrent transfer waits)
- [ ] Retry resends a failed transfer
- [ ] Transfer list survives app restart (persisted in SQLite)
- [ ] No chat browsing or contact export

---

## Phase 5 — Vault organisation & browsing

**Goal:** Organise uploaded files into a virtual folder hierarchy. No local file system changes.

**Deliverables:**
- Virtual folders stored in SQLite (path → set of Telegram message IDs)
- Create, rename, delete virtual folders
- Move files between virtual folders
- Files page shows vault contents by default (instead of local disk)
- Folder tree in sidebar shows virtual folders
- Breadcrumb navigation
- Context menu: Move to Folder, Delete from Vault, Download

**Acceptance gates:**
- [ ] Create a virtual folder → appears in sidebar
- [ ] Upload a file → lands in current virtual folder (or root)
- [ ] Move file to another virtual folder → appears there
- [ ] Rename folder → all files still accessible
- [ ] Delete folder → files remain accessible from root
- [ ] Breadcrumb updates correctly as user navigates
- [ ] Sidebar folder tree reflects virtual hierarchy
- [ ] Delete from vault removes Telegram message (with confirmation)
- [ ] Local disk still browseable via a separate view/section

---

## Phase 6 — Search, tags & metadata

**Goal:** Full-text search across file names and user-added tags. File previews for common types.

**Deliverables:**
- Search bar searches vault files by name and tags
- Tags: add/remove tags on vault files; filter by tag
- Quick preview panel (side panel or overlay):
  - Images: thumbnail
  - Text/code: syntax-highlighted preview (first 100 lines)
  - PDF: first-page preview (if renderable)
  - Video/audio: file info only (no playback)
  - Other: icon + file info
- File detail panel: name, size, type, created, uploaded, tags, Telegram message ID

**Acceptance gates:**
- [ ] Search finds files by name and tag
- [ ] Tags can be added, removed and filtered
- [ ] Image preview shows a thumbnail
- [ ] Text preview renders with syntax highlighting
- [ ] File detail panel shows all expected fields
- [ ] Search is fast (< 1 s for 1000 files)
- [ ] Preview panel opens and closes without blocking UI

---

## Phase 7 — Settings, preferences & theme

**Goal:** Full settings experience. Persist user preferences.

**Deliverables:**
- Settings page sections:
  - **Account:** login status, phone, logout
  - **Storage:** vault root folder path, auto-index folders
  - **Transfers:** concurrent limit, default download folder
  - **Appearance:** light/dark/system theme
  - **Privacy:** clear local data, export data
  - **About:** version, unofficial-app disclosure, licenses
- Theme switching without restart
- Preferences persisted in SQLite

**Acceptance gates:**
- [ ] Theme toggle switches light/dark immediately
- [ ] Concurrent transfer limit saved and applied on restart
- [ ] Default download folder change respected by new downloads
- [ ] Clear local data wipes index, session and preferences (with confirmation)
- [ ] About section shows version and disclosure
- [ ] All settings survive restart

---

## Phase 8 — Installer & updates

**Goal:** Per-user installer built with Inno Setup 6. Automatic update checks.

**Deliverables:**
- `installer/Nuvia.iss` — Inno Setup script
- Per-user install (`%LOCALAPPDATA%\Programs\Nuvia`)
- Start menu shortcut
- Desktop shortcut (optional, toggled in installer)
- File association (`.nuvia` config files)
- Uninstaller preserves local data (index, session, preferences) unless user opts in to removal
- Update check on startup: ping a version URL, show "Update available" banner
- Version number in `Directory.Build.props`

**Acceptance gates:**
- [ ] Inno Setup compiles without errors
- [ ] Installer runs and places app in `%LOCALAPPDATA%\Programs\Nuvia`
- [ ] Start menu shortcut launches the app
- [ ] Uninstaller removes program files but preserves `%LOCALAPPDATA%\Nuvia\` data
- [ ] Uninstaller with "remove all data" checkbox wipes everything
- [ ] Update banner appears when version URL returns a newer version
- [ ] Version number is single-source in build props

---

## Phase 9 — Polish, edge cases & release readiness

**Goal:** Error handling, accessibility, performance, and release checklist.

**Deliverables:**
- Graceful error handling throughout (Telegram disconnects, disk full, file locked, network loss)
- Retry logic for transient failures
- Accessibility: keyboard navigation, screen reader labels, high-contrast theme support
- Performance: virtualised list for 10 000+ files, lazy-loaded thumbnails
- Crash reporting (write to `%LOCALAPPDATA%\Nuvia\crash.log`, offer to copy on next launch)
- Release checklist in `docs/RELEASE_CHECKLIST.md`
- Final audit against SPEC.md for compliance

**Acceptance gates:**
- [ ] App handles Telegram disconnect gracefully (shows status, retries)
- [ ] Disk-full during download shows meaningful error, does not crash
- [ ] Keyboard navigation covers all primary actions (Tab, Enter, Esc, arrows)
- [ ] Screen reader can read file names, sizes and status
- [ ] High-contrast theme renders all UI legibly
- [ ] List of 10 000 items scrolls smoothly (< 100 ms frame time)
- [ ] Crash log written and recoverable on next launch
- [ ] No credentials, sessions or account data in any log file
- [ ] SPEC.md compliance audit passed

---

## Phase ordering rationale

| Phase | Dependency | Reason |
|-------|-----------|--------|
| 0 | None | Foundation documents |
| 1 | 0 | Visual shell before any logic |
| 2 | 1 | File operations before cloud |
| 3 | 1 | Auth before cloud transfers |
| 4 | 2, 3 | Upload/download needs index + auth |
| 5 | 4 | Vault needs transfers working |
| 6 | 5 | Search/tags need vault populated |
| 7 | 1 | Settings UI independent of cloud |
| 8 | 7 | Installer needs stable settings |
| 9 | All | Polish requires everything built |

---

## Platform notes

- **Source editing and static analysis:** Can be performed on Linux.
- **Build verification (`dotnet build`):** Works on Linux for .NET console/library projects, but WPF projects require Windows.
- **UI and installer acceptance:** Requires Windows with .NET 8 SDK and Inno Setup 6 installed.
- **Windows CI:** No CI is configured in this phase. CI setup is not part of the current scope.