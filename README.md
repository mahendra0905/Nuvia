# Nuvia

**A native Windows desktop file vault backed by your own Telegram cloud storage.**

Nuvia lets you upload, organise, search and download files using your Telegram account as the storage backend — through a clean WPF desktop app, with a local index and no third-party servers in between.

> ⚠️ Nuvia is an unofficial, community project. It uses the Telegram API and is **not affiliated with or endorsed by Telegram**.

---

## Features

- **Telegram-backed storage** — files are stored as documents in your own *Saved Messages* or in a private group you control.
- **Private storage groups** — optionally create private supergroups (only you as a member, no invite link, no public username) to keep files separated by purpose. Create as many as Telegram allows.
- **Local folders** — organise any location into folders. Folders live in the local index; Nuvia also embeds an invisible marker in the upload caption so folder structure can be restored after a reinstall.
- **Fast local search** — filter your indexed files by name as you type (`Ctrl+F`). Search never scans your Telegram history.
- **Local rename** — change a file's display name locally (`F2`) without touching the remote message.
- **Reliable transfers** — progress reporting, mid-transfer cancellation, automatic `FLOOD_WAIT` handling, and clear error messages for network, disk, permission and size problems.
- **Safe downloads** — files are downloaded to a temporary partial file first; existing files are never overwritten without confirmation; remote filenames are sanitised before touching disk.
- **Clean logout** — revokes the server-side session when online, then removes the local session. Never deletes your remote files, groups, or account.
- **Per-user installer** — no admin rights and no .NET runtime required on the target PC.

## Screens & workflow

1. Sign in with your phone number → login code → optional 2FA password.
2. Pick a location (Saved Messages or a private group).
3. Upload a file, organise it into folders, search, rename or download it whenever you need.

## Requirements

- Windows 10 / 11
- To build from source: [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) or later
- A Telegram account
- Telegram API credentials (`api_id` / `api_hash`) from <https://my.telegram.org> when building from source

> WPF is Windows-only. You can edit the source on Linux/macOS, but the app can only be built and run on Windows.

## Getting started

### Install (end users)

Download the latest `Nuvia-Setup-x.y.z.exe` from the [Releases](../../releases) page, verify its SHA-256 checksum, and run it. It installs per user into `%LOCALAPPDATA%\Programs\Nuvia`.

### Build from source

```bash
# Restore dependencies
dotnet restore Nuvia.sln

# Build
dotnet build Nuvia.sln -c Debug

# Run
dotnet run --project src/Nuvia.App/Nuvia.App.csproj
```

Or open `Nuvia.sln` in **Visual Studio 2022**, set `src/Nuvia.App` as the startup project, and press **F5**.

### Telegram API credentials

Nuvia needs an `api_id` and `api_hash`. Create your own at <https://my.telegram.org> → *API development tools*, then provide them via a local `config.json` (see `Services/NuviaConfig.cs` for the exact lookup). **Never commit your credentials.**

### Run the tests

```bash
dotnet test Nuvia.sln -c Release
```

The unit tests are network-free (no login, no filesystem).

## Project structure

```
Nuvia.sln
src/Nuvia.App/
  App.xaml(.cs)            Application entry point
  MainWindow.xaml(.cs)     File list, toolbar, transfers, settings
  LoginWindow.xaml(.cs)    Phone → code → 2FA sign-in
  Services/
    TelegramAuthService    Sign-in and logout (WTelegramClient)
    TelegramStorageService Create group, upload, download, refresh
    FileTransferCoordinator Transfer ordering rules
    IndexStore             Local SQLite index
    SettingsStore          Atomic JSON preferences (no secrets)
    FileNameSafety         Sanitises untrusted remote filenames
tests/Nuvia.Tests/         xUnit unit tests
installer/                 Inno Setup 6 per-user installer
.github/workflows/         CI and release pipelines
docs/                      CI / release documentation
```

## Privacy & security

- Your session is stored locally under `%LOCALAPPDATA%\Nuvia\session`. Preferences contain **no** credentials, login codes, passwords or session data.
- The local index (`index.db`) and settings are scoped per Telegram account — switching accounts never shows another account's data.
- Nuvia talks only to Telegram. The installer never contacts Telegram or any other server.
- Uninstalling keeps your local data by default; deleting it is an explicit opt-in. Uninstalling does **not** delete remote files or revoke your Telegram session — use *Log out* in the app, or Telegram → *Settings → Devices*.

## Good to know

- **The index is local, not a sync of your Telegram history.** `index.db` only contains files uploaded through Nuvia on that machine. Files added from other clients won't appear, and deleting the index never deletes anything from Telegram.
- **One transfer at a time.** No bulk queue or pause/resume yet.
- **Size limits** follow Telegram's user-account limits (2 GB free / 4 GB Premium). Nuvia only blocks what no account could store and reports the server's answer otherwise.
- **If an upload succeeds but indexing fails,** Nuvia tells you the file may already exist in your storage and does not re-upload, to avoid duplicates.
- **Folder renames** rewrite the caption marker on Nuvia-uploaded files one by one, best-effort; the status line reports exactly how many succeeded. Files added outside Nuvia can be moved locally but return to the location root after a reinstall.

## CI / Releases

- `ci.yml` — on pull requests and pushes to `main`: restore, build (Release), run unit tests. No secrets, read-only permissions.
- `release.yml` — on `v*.*.*` tags, in a protected `release` environment: publishes a self-contained win-x64 build, compiles the installer, generates a SHA-256 checksum and creates a draft release.

See [`docs/CI_RELEASE.md`](docs/CI_RELEASE.md) for details.

> Builds that embed `api_id` / `api_hash` in the installer make those values extractable from the binary. Treat them as rotatable publisher credentials.

## Contributing

Issues and pull requests are welcome. Please keep changes focused, run the tests before submitting, and never include credentials, session files or `index.db` in a PR.

## License

_Add your license here (e.g. MIT) and include a `LICENSE` file in the repository root._

Nuvia is an unofficial application that uses the Telegram API and is not affiliated with Telegram.
