# CI and release automation (Phase 8)

Nuvia has two GitHub Actions workflows with deliberately separated trust levels:

| Workflow | File | Trigger | Secrets | Purpose |
| --- | --- | --- | --- | --- |
| **CI** | `.github/workflows/ci.yml` | pull requests + pushes to `main` | none | Restore, build Release, run network-free unit tests |
| **Release** | `.github/workflows/release.yml` | version tags `v*.*.*` (+ manual approval) | publisher `api_id`/`api_hash` from a protected environment | Publish, compile the installer with injected credentials, checksum, draft release |

Both run on `windows-latest` because Nuvia is a WPF app and the target is win-x64.

> **A green build is not proof the app works.** CI proves the code compiles and the
> pure-logic unit tests pass. It does **not** exercise the WPF UI, the installer, sign-in,
> uploads, or any Windows UX. Those checks are manual and are listed at the end of this
> document. Do not treat a passing workflow as acceptance.

## CI workflow (untrusted-safe)

`ci.yml` runs on every pull request and every push to `main`. It:

1. checks out the source,
2. installs the .NET 8 SDK,
3. `dotnet restore`,
4. `dotnet build -c Release`,
5. `dotnet test -c Release` — the network-free tests in `tests/Nuvia.Tests`.

It requests only `contents: read` and holds **no secrets**, so it is safe to run against
pull-request code from forks. There is **no Telegram login and no network upload test** here
by design: nothing in CI may require production credentials or a real account.

The tests cover pure, deterministic logic only — `FileNameSafety` (untrusted-filename
sanitisation) and the `StorageDestination` model. They construct no WPF UI and touch neither
the network nor the filesystem.

## Release workflow (trusted, approval-gated)

`release.yml` is the only workflow that handles production credentials, and it is isolated
from untrusted code three ways:

- **Tag-only trigger.** It runs only when a `v*.*.*` tag is pushed to this repository. Pull
  requests — including from forks — cannot trigger it and cannot read its secrets.
- **Protected environment.** The job runs in the `release` GitHub Environment. Configure that
  environment with **required reviewers** so a human must approve each run before the job
  starts and before any secret is exposed. That approval is the "explicitly approved version
  tag" gate.
- **Least privilege.** The workflow requests only `contents: write` (needed to create the
  draft release).

### What it does

1. **Validate versions agree.** The git tag (`v1.0.0` → `1.0.0`), `<Version>` in
   `src/Nuvia.App/Nuvia.App.csproj`, and `MyAppVersion` in `installer/Nuvia.iss` must all
   match, or the run fails. After publish it also confirms the built `Nuvia.dll`
   ProductVersion matches the tag.
2. **Build and test** in Release (same network-free tests as CI).
3. **Publish** a **self-contained win-x64** build — **not** single-file — with
   `DebugType=none`/`DebugSymbols=false` so **no PDBs** are produced.
4. **Install a pinned Inno Setup 6** toolchain (`choco install innosetup --version=6.2.2`)
   and locate `ISCC.exe`.
5. **Compile the installer** with credential injection (see below), producing
   `artifacts/installer/Nuvia-Setup-<version>.exe`.
6. **Generate a SHA-256 checksum** (`<name>.sha256`).
7. **Upload** the installer and checksum as a build artifact.
8. **Create a draft GitHub release** with both files attached.

<!-- APPEND_MARKER -->

## Credentials

`api_id` and `api_hash` are **publisher** credentials obtained from
[my.telegram.org](https://my.telegram.org). They are **not** end-user secrets — Nuvia never
asks the person installing it for an api_id/api_hash. At runtime the app reads them from
`%LOCALAPPDATA%\Nuvia\config.json` (see `Services/NuviaConfig.cs`).

For a release, the installer writes that `config.json` at install time from values baked in at
compile time. The release workflow supplies them to Inno Setup as preprocessor defines
(`/DMyApiId=… /DMyApiHash=…`) sourced from the `release` environment secrets
`NUVIA_API_ID` and `NUVIA_API_HASH`. In the workflow the values are passed through environment
variables and referenced by name — never written into a visible command line or printed — and
GitHub additionally masks secret values in logs.

The optional injection lives in `installer/Nuvia.iss` behind
`#if defined(MyApiId) && defined(MyApiHash)`. A normal PR or developer compile passes **neither**
define, so that code is not emitted and the installer behaves exactly as before (it writes no
`config.json`). Only the credential-carrying release compile activates it.

> **`api_hash` embedded in a distributed binary is extractable.** Once compiled into the
> installer, the `api_id`/`api_hash` ship inside the `.exe` and can be recovered from any copy of
> it. This is inherent to embedding a credential in client software, not a defect in Nuvia. Treat
> these as rotate-able publisher credentials: keep them in the protected environment, and rotate
> them at my.telegram.org if they are leaked or misused. This is also noted in
> `docs/CREDENTIALS.md`.

### How credentials are kept out of source, logs, caches and PDBs

- **Source:** `config.json` is git-ignored and is also in the installer's `[Files]` `Excludes`,
  so it is never packaged from a developer machine. Credentials exist only as environment
  secrets and as the transient defines during the release compile.
- **Logs:** secrets are passed by env-var reference, never echoed; GitHub masks their values.
- **PDBs:** the release publish sets `DebugType=none`/`DebugSymbols=false`, and the workflow
  fails if any `.pdb` is present in the publish output. (The credentials are not in the app's
  code in any case — they are written to `config.json` at install time, not compiled into
  `Nuvia.dll`.)
- **Uploaded artifacts / release:** only the installer `.exe` and its `.sha256` are uploaded —
  never the publish tree, source, or any `config.json`.
- **Not a CI secret:** a real user's **Telegram session** is never used as a CI secret. CI and
  release use only the publisher `api_id`/`api_hash`; no account session material is stored in
  GitHub.

## Required repository / environment setup

Before the release workflow can succeed, a maintainer must configure (one-time):

1. **Environment `release`** (Settings → Environments): add **required reviewers** so runs pause
   for manual approval.
2. **Environment secrets** on `release`:
   - `NUVIA_API_ID` — the numeric publisher api_id.
   - `NUVIA_API_HASH` — the publisher api_hash string.
3. **Verify the pinned action SHAs.** The workflows pin third-party actions to full commit SHAs
   (with the intended version in a trailing comment). Because these were authored offline,
   confirm each SHA resolves to the stated release tag before trusting it:
   - `actions/checkout` → `11bd71901bbe5b1630ceea73d27597364c9af683` (v4.2.2)
   - `actions/setup-dotnet` → `87b7050bc53ea08284295505d98d2aa94301e852` (v4.0.1)
   - `actions/upload-artifact` → `b4b15b8c7c6ac21ea08fcf65892d2ee8f75cf882` (v4.4.3)

   If any SHA does not match on review, re-pin it to the correct commit for the intended
   release before enabling the workflow.

## Cutting a release

1. Ensure `<Version>` in `Nuvia.App.csproj` and `MyAppVersion` in `installer/Nuvia.iss` match
   the version you intend to ship (e.g. `1.0.0`).
2. Tag and push: `git tag v1.0.0 && git push origin v1.0.0`.
3. Approve the run when the `release` environment requests a reviewer.
4. When it finishes, a **draft** release exists with `Nuvia-Setup-1.0.0.exe` and its `.sha256`.
5. **Run the manual acceptance checks below on Windows.** Only then publish the draft.

## Checks that remain MANUAL (not covered by any workflow)

None of the following is verified by CI or the release workflow. A green run says nothing about
them, and this project's development happens in a Linux sandbox with no .NET, no `ISCC.exe`, and
no way to run WPF — so nothing here has been compiled or executed by the author.

- **WPF UI / UX:** the app actually launches and renders; login (phone → code → optional 2FA);
  file list, search, rename, refresh, settings, logout; no console or browser window appears.
- **Real upload / download** to Saved Messages and to the managed private group, including
  flood-wait handling and cancellation — these require a real account and are never run in CI.
- **Installer UX on Windows:** Welcome + directory pages; no UAC prompt (per-user); default-checked
  desktop shortcut; Start Menu shortcut; finish-page launch; appears in Settings → Apps.
- **Clean-machine install** on a PC with no .NET runtime; **upgrade-in-place** over a prior
  version with a running instance; the full uninstall flow (**No** = keep data, **Yes** = delete
  `%LOCALAPPDATA%\Nuvia`, **Cancel** = abort; silent = preserve).
- **Credential injection result:** that the release installer actually writes a working
  `config.json` and the app signs in with it.

The PowerShell procedures for the installer/clean-machine/upgrade checks are in
`installer/BUILD.md`.

