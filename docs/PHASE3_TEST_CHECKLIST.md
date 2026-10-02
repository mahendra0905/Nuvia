# Phase 3 — Manual Windows Test Checklist

**Prerequisites:**
- Windows 10/11 with .NET 8 Desktop Runtime
- Valid `api_id`/`api_hash` from <https://my.telegram.org> (developer-controlled test account)
- Config file at `%LOCALAPPDATA%\Nuvia\config.json` with credentials
- Nuvia built: `dotnet build -c Release` or `dotnet publish -c Release`

---

## Test Scenarios

### 1. Fresh Login (No Existing Session)

**Setup:** Delete `%LOCALAPPDATA%\Nuvia\session\Nuvia.session` if exists.

**Steps:**
1. Launch `Nuvia.exe`
2. Observe: LoginWindow opens, demo notice NOT shown (real config loaded)
3. Enter valid phone number in international format (e.g., `+1 555 123 4567`)
4. Click **Continue**
5. Observe: "Sending code..." busy overlay, UI disabled
6. Receive code via Telegram (official app / SMS / call — **do not assume SMS**)
7. Enter the code you received (4–8 digits — length depends on delivery method)
8. Click **Verify**
9. If no 2FA: Observe → MainWindow opens, title shows user display name
10. If 2FA enabled on account: Observe → PasswordEntry state, enter 2FA password, click **Sign In** → MainWindow opens

**Expected:**
- Phone input accepts only `+` + digits + spaces
- Code input accepts only 6 digits
- Busy overlay disables all interaction
- Errors shown inline (invalid phone, invalid code, network error)
- Back button returns to previous state, clears sensitive fields
- Cancel closes app cleanly (no orphan process)

---

### 2. 2FA Login

**Setup:** Use a test account with 2FA enabled. Delete session file.

**Steps:**
1. Launch app
2. Enter phone → Continue
3. Enter code → Verify
4. Observe: PasswordEntry state shown
5. Enter correct 2FA password → Sign In
5. Observe: MainWindow opens

**Expected:**
- PasswordBox used (masked input)
- Wrong password shows inline error, allows retry
- Back button returns to code entry, clears password

---

### 3. Invalid Code

**Setup:** Fresh login, any account.

**Steps:**
1. Enter phone → Continue
2. Enter wrong 6-digit code (e.g., `000000`)
3. Click **Verify**

**Expected:**
- Inline error: "Invalid code or network error: ..."
- Stays in CodeEntry state
- Can retry with correct code
- Back button works

---

### 4. Cancellation

**Test at each state:**

**PhoneEntry:**
1. Launch app
2. Click **Cancel** (or close window via ×)
3. Observe: App exits cleanly, no process remains

**CodeEntry:**
1. Enter phone → Continue
2. Click **Back** → returns to PhoneEntry, code cleared
3. Click **Cancel** → app exits

**PasswordEntry:**
1. Enter phone → code → (if 2FA) → PasswordEntry
2. Click **Back** → returns to CodeEntry, password cleared
3. Click **Cancel** → app exits

**Busy state:**
- Cannot cancel during async operation (by design — waits for completion/timeout)
- After error returns, Cancel works

---

### 5. Restart with Valid Session

**Setup:** Complete a successful login (Test 1 or 2). Do NOT delete session file.

**Steps:**
1. Close app (MainWindow → ×)
2. Launch `Nuvia.exe` again

**Expected:**
- LoginWindow **does not appear** (or appears briefly then closes)
- MainWindow opens directly
- Session reused — no phone/code prompt

---

### 6. Revoked / Expired Session

**Setup:** 
1. Complete successful login
2. Go to Telegram Settings → Devices → Terminate the Nuvia session
   OR delete `%LOCALAPPDATA%\Nuvia\session\Nuvia.session`
   OR revoke `api_hash` at my.telegram.org (creates new hash)

**Steps:**
1. Launch `Nuvia.exe`

**Expected:**
- App detects invalid session
- LoginWindow appears fresh (phone entry)
- No crash, no orphan process
- User can log in again normally

---

### 7. Network Failure

**Setup:** 
1. Disconnect network (Wi-Fi off / airplane mode)
2. Launch app
3. Enter phone → Continue

**Expected:**
- Busy overlay shows
- After timeout: inline error "Network error or invalid phone: ..."
- Returns to PhoneEntry state
- Retry works when network restored

**Alternative:** 
- Login successfully
- Disconnect network
- Try any action that needs network (later phases)
- For Phase 3: just verify app doesn't crash on startup with no network

---

### 8. Missing / Invalid Config

**Setup:** 
1. Delete or rename `%LOCALAPPDATA%\Nuvia\config.json`
2. Launch `Nuvia.exe`

**Expected:**
- LoginWindow opens showing a **safe configuration error** naming `%LOCALAPPDATA%\Nuvia\config.json`
- **No** credential-entry form is shown; no stack trace; no credential values in the message
- **No** demo login is offered — the demo path is opt-in only (`NUVIA_DEMO=1`)
- **Retry** re-runs startup, so fixing the file and pressing Retry proceeds without a restart

**Invalid JSON:**
1. Write invalid JSON to config.json
2. Launch app
3. Same safe configuration error (message reports line/position, never file contents)

**Invalid api_id/api_hash:**
1. Put valid JSON but a wrong api_hash
2. Launch app
3. Config loads (shape is valid); sign-in fails with a network error at the code-request step
4. Error text must not contain the credential value

**Demo mode (development only):**
1. `set NUVIA_DEMO=1` then launch `Nuvia.exe`
2. LoginWindow title reads "Nuvia — Sign In (Demo Mode)" and the demo notice banner is visible
3. +999... (no 2FA), +888... (2FA password "demo"); real phone numbers rejected

---

## Pass Criteria

| Scenario | Must Pass |
|----------|-----------|
| Fresh login (no 2FA) | ✅ |
| Fresh login (with 2FA) | ✅ |
| Invalid code handling | ✅ |
| Cancellation at all states | ✅ |
| Session resume on restart | ✅ |
| Revoked session handling | ✅ |
| Network failure handling | ✅ |
| Missing/invalid config fallback | ✅ |
| No credentials in logs/console | ✅ |
| No orphan processes after close | ✅ |

---

## Test Account

Use a **developer-controlled test account** (not your personal main account).
- Create a separate Telegram account for testing
- Enable 2FA on it for Test 2
- Keep `api_id`/`api_hash` in your password manager / CI secrets

**Never ask for login codes or passwords in chat.** The tester enters them directly in the app.