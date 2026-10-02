# Nuvia — UI / Flow / Feature Placement Plan

Status: **Approved** (2026-09-27). This plan covers **structure, placement, and flow only**.
The visual restyle (colours, typography) is a **separate later pass** — the current look stays
until then. Nothing here expands product scope beyond `docs/SPEC.md`.

---

## Approved decisions

1. **Shell** — Settings and About open as an **in-window side panel** (slide in from the right),
   not as separate popup windows. Splash and Login stay as their own windows (protected startup
   flow is untouched).
2. **Account + Logout** — a **top-right account menu** (account label + dropdown). Logout lives
   here (its only home). Account identity is shown in the chip.
3. **"Upload to" destination** — control lives **only in Settings**. The toolbar ComboBox is
   removed (kills the duplicate-preference drift).
4. **Disclosure** — **no** banner in the main window. Login + About keep the existing disclosure
   (satisfies SPEC's "startup + settings" requirement).

---

## Target main window (native WPF)

```
+------------------------------------------------------------------+
|  Nuvia                                     [ (o) +91 ....34   v ] |  top bar: brand left, account menu right
+------------------------------------------------------------------+
|  [ ^ Upload ] [ v Download ] [ / Rename ]     (search...   x)  ^  |  action bar + search + refresh icon
+------------------------------------------------------------------+
|  Name                 Size     Uploaded            Where          |
|  -------------------------------------------------------------    |
|  report.pdf           2.1 MB   2026-09-20 14:03    Saved Msgs     |
|  ...                                                              |
+------------------------------------------------------------------+
|  4 indexed files.     Uploads -> Saved Messages · Index: ... [Open]|  status bar
+------------------------------------------------------------------+
```

### Placement
- **Top bar (new):** "Nuvia" left; **account chip** right → dropdown: account label (header),
  Settings…, About Nuvia, ──, **Log out and delete local session…**
- **Action bar:** Upload (primary), Download + Rename (enabled on selection), Refresh (icon, right),
  Search (right).
- **Removed from toolbar:** "Upload to" ComboBox and "Create private group" button.
- **Status bar:** file count (left); "Uploads → <target>" + "Index: <path> [Open]" (right, Open is
  actionable).
- **Transfer strip:** unchanged; appears under the action bar during a transfer.

### Settings side panel
1. **Upload destination** — radios Saved Messages / <group>. If no group exists, a
   **"Create private group"** button + explanatory note.
2. **About your data** — local index explanation; rename changes only the local label.
   (Logout removed from here — it now lives in the account menu, so no duplication.)

### About side panel
"Nuvia", version, unofficial-app disclosure, "Built with .NET 8 · WPF · WTelegramClient", Close.

---

## Flow
- **Startup (protected, unchanged):** Splash → silent resume → Main, or Login → Main.
  `App.xaml.cs` startup/resume/lifecycle logic is not altered.
- **In-window (new):** account menu → Settings/About panel opens/closes inside the same window.
  Rename and Create-group stay as small modal confirm dialogs.

---

## Out of scope (now)
Virtual folders, tags, file previews, theme switching, multi-account, concurrent transfers.
(Present in `docs/PHASES.md` but outside shipped scope — future phases.)

---

## Work order
- **UI-1 + UI-2 delivered together** (they are interdependent — the account menu opens the
  Settings panel, and the destination combo can only be removed once Settings hosts it, so an
  intermediate step would strand a feature). One coherent, working result:
  - Top bar + account menu (Settings/About/Logout), account identity in chip.
  - Settings & About converted from separate windows to in-window side panels.
  - "Create private group" + destination moved into the Settings panel; toolbar combo/button removed.
  - Refresh → icon; status bar shows upload target + actionable index path.
  - Every existing `x:Name`, event handler, binding, and enable/visibility rule preserved.
- **UI-3 (separate, later):** visual restyle (new look).

## Verification
- `dotnet build src/Nuvia.App/Nuvia.App.csproj -c Debug` — 0 errors.
- `dotnet test tests/Nuvia.Tests/Nuvia.Tests.csproj -c Debug` — 38 pass.
- Windows manual check (run by user): sign-in → list → upload/download/rename/refresh, account
  menu (Settings/About panels, logout), destination switch + create-group from Settings.
