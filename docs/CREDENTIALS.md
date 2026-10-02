# Nuvia — Build-Time Credential Injection

## Overview

Nuvia uses **WTelegramClient** for Telegram user authentication. The library requires two publisher-owned credentials:

- **api_id** — a positive integer (e.g., `123456`)
- **api_hash** — a string (e.g., `abcdef1234567890abcdef1234567890`)

These are obtained from <https://my.telegram.org> by the **application publisher** (you). They are **not** obtained from end users.

## Why Not Ask Users for Credentials?

- `api_id`/`api_hash` identify **the application**, not the user.
- Users sign in with their **phone number** + verification code + optional 2FA password.
- Asking users for `api_id`/`api_hash` would expose your developer credentials and violate Telegram's terms.

## Runtime Configuration Location

At runtime, Nuvia reads a JSON configuration file from:

```
%LOCALAPPDATA%\Nuvia\config.json
```

**This file is never committed to Git.** It is created by the publisher during build/packaging.

## Config File Format

```json
{
  "api_id": 123456,
  "api_hash": "abcdef1234567890abcdef1234567890"
}
```

Both fields are required. `api_id` must be a positive integer. `api_hash` must be a non-empty string.

## Build-Time Injection Approaches

### Option 1: Inno Setup Installer (Recommended for releases)

In your Inno Setup script (`installer.iss`), embed the credentials into the generated installer:

```iss
[Files]
; Your compiled Nuvia.exe and dependencies
Source: "..\src\Nuvia.App\bin\Release\net8.0-windows\publish\*"; DestDir: "{app}"; Flags: ignoreversion

; Generate config.json at install time from compile-time constants
; Define MyApiId and MyApiHash on the command line:
;   iscc /DMyApiId=123456 /DMyApiHash=abcdef1234567890 installer.iss

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigPath: string;
  ConfigContent: string;
begin
  if CurStep = ssPostInstall then
  begin
    ConfigPath := ExpandConstant('{localappdata}\Nuvia\config.json');
    if not DirExists(ExtractFilePath(ConfigPath)) then
      ForceDirectories(ExtractFilePath(ConfigPath));
    
    ConfigContent := '{' + #13#10 +
      '  "api_id": ' + '{{#MyApiId}}' + ',' + #13#10 +
      '  "api_hash": "' + '{{#MyApiHash}}' + '"' + #13#10 +
      '}';
    SaveStringToFile(ConfigPath, ConfigContent, False);
  end;
end;
```

**Build command:**
```cmd
iscc /DMyApiId=123456 /DMyApiHash=abcdef1234567890 installer.iss
```

The credentials end up **only in the compiled installer binary**, not in source control.

### Option 2: MSBuild / dotnet publish (CI/CD)

In your CI pipeline (GitHub Actions, Azure DevOps, etc.), inject during publish:

```yaml
# .github/workflows/release.yml
- name: Create config.json
  run: |
    mkdir -p "$env:LOCALAPPDATA\Nuvia"
    echo '{"api_id": "${{ secrets.NUVIA_API_ID }}", "api_hash": "${{ secrets.NUVIA_API_HASH }}"}' > "$env:LOCALAPPDATA\Nuvia\config.json"
```

Then run `dotnet publish` — the config file will be in the correct location when the app runs on the build machine. For distribution, package the published folder along with a script that writes config.json on first run.

### Option 3: Post-Build Script (Development)

For local development, create a simple script:

```cmd
REM dev-config.cmd
mkdir "%LOCALAPPDATA%\Nuvia" 2>nul
echo {"api_id": 123456, "api_hash": "abcdef1234567890abcdef1234567890"} > "%LOCALAPPDATA%\Nuvia\config.json"
```

Run once per machine. **Never commit this script with real credentials.**

## Session Storage

Sessions are stored at:

```
%LOCALAPPDATA%\Nuvia\session\Nuvia.session
```

This is managed by WTelegramClient via the `session_pathname` config callback. The session file contains the encrypted auth key and is tied to your `api_id`/`api_hash`. If credentials change, the session becomes invalid.

**Account scoping:** the session path is fixed by the application and is always under
`%LOCALAPPDATA%\Nuvia\session\` — it is *not* configurable, so a session can never be written
into the repository or a working directory. One session file means **one signed-in account at a
time**; signing in as a different account requires clearing the stored session first.

**Revoked or expired session:** if the stored session is no longer valid, Nuvia discards the
session file and falls back to a normal phone + code sign-in instead of retrying a dead session.

## Security Notes

### Embedded Credentials Are Extractable

**Any desktop binary can be reverse-engineered.** If you embed `api_id`/`api_hash` directly in the .exe (e.g., as constants), they **will** be extracted.

**Mitigations:**
1. **Do not hardcode credentials in source code.** Use the config file approach above.
2. **Inno Setup compiles credentials into the installer**, not the .exe. The .exe reads them from the config file at runtime.
3. **Rotate credentials if compromised.** Telegram allows revoking `api_hash` at my.telegram.org.
4. **Use separate credentials per environment** (dev, staging, prod) to limit blast radius.
5. **App-level restrictions:** In my.telegram.org, set "App type" appropriately and monitor usage.

### What the App Does NOT Do

- ❌ Does not log `api_id` or `api_hash`
- ❌ Does not print them to console/debug output
- ❌ Does not send them anywhere except to Telegram's API during login
- ❌ Does not store them in any other file

### What You Should Do

- ✅ Store credentials in CI secrets (GitHub Actions secrets, Azure Key Vault, etc.)
- ✅ Inject at build/publish time only
- ✅ Use different `api_id`/`api_hash` for dev vs. production
- ✅ Monitor your Telegram API dashboard for unusual activity
- ✅ Document your injection process for future maintainers

## Fallback Behavior

If `config.json` is missing or malformed, Nuvia does **not** sign anyone in and does **not**
fall back to a demo login on its own:

- The app shows a **safe configuration error** (no stack trace, no credential values, no
  credential entry form) pointing at `%LOCALAPPDATA%\Nuvia\config.json`.
- The **Retry** button re-runs startup, so the publisher can fix the file and retry without
  restarting the app.
- Sign-in is unavailable until the configuration is valid — a misconfigured install can never
  present a working login form.

This ensures the app never shows an "enter your developer credentials" form to end users, and
never fakes a successful sign-in.

### Demo Mode (development only, explicit opt-in)

The deterministic auth simulator used during UI development is **opt-in only**:

```cmd
set NUVIA_DEMO=1
Nuvia.exe
```

When enabled, the login window is titled `Nuvia — Sign In (Demo Mode)` and carries a visible
demo notice. Without `NUVIA_DEMO=1` the demo path is unreachable, so it cannot be mistaken for
a real session. Demo credentials: `+999…` (no 2FA) and `+888…` (2FA password `demo`).