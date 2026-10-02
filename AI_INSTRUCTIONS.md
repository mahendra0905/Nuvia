# Nuvia — AI Working Instructions

These rules apply to ANY AI assistant working on this project. Read them fully before
making a single change. If a request ever conflicts with these rules, stop and ask.

---

## 0. The Golden Rule

**Do ONLY what the current request explicitly asks for. Nothing more.**

- Do not refactor, "clean up", rename, reformat, or "improve" code that the request did not
  mention.
- Do not delete or rewrite anything that already works just because you are nearby.
- Do not add features, options, libraries, or abstractions that were not requested.
- A UI request means UI changes only. A bug fix means that one bug only.

If you believe a requested change *requires* touching working code, **explain why and ask
first** — do not silently modify it.

---

## 1. Working Systems That Must NOT Break

The following already work and are verified. Treat them as protected. Do not change their
behavior, logic, or wiring unless the request is explicitly about them.

- **Authentication & persistent login** — `src/Nuvia.App/Services/TelegramAuthService.cs`.
  Especially the session resume path: `ConfigCallback` returns `"-1"` for `user_id` and the
  `LoginUserIfNeeded(reloginOnFailedResume: false)` call. This is what keeps the user logged
  in across restarts. Do not touch it.
- **App startup & window lifecycle** — `src/Nuvia.App/App.xaml.cs`. The splash → silent
  resume → main window flow, the `_completed` single-window guard, and the fact that a window
  is always shown BEFORE the previous one closes (because `ShutdownMode=OnLastWindowClose`).
- **Splash screen** — `SplashWindow.xaml` / `.xaml.cs`.
- **Upload / download / storage** — `Services/TelegramStorageService.cs`.
- **Local file index** — `IndexStore` and its migrations.
- **Config loading** — `NuviaConfig` (`%LOCALAPPDATA%\Nuvia\config.json`).

If unsure whether something is "protected", assume it is and ask.

---

## 2. Before You Edit

1. Read the relevant file(s) **fully** first. Understand the existing pattern.
2. Match the surrounding code: same naming, same style, same comment density, same libraries.
3. Do not introduce a new framework, NuGet package, or architectural pattern without asking.
4. Make the smallest change that satisfies the request.

---

## 3. UI / Feature Work (specific rules)

- Change only the XAML / view / styling that the request names.
- Do NOT alter service classes, business logic, event wiring, or data flow to make a UI change,
  unless it is truly required — and if so, say so and confirm first.
- Keep existing event handlers and their signatures intact unless the task is to change them.
- New UI must be accessible and consistent with the existing look.

---

## 4. Security (never violate)

- `api_id` / `api_hash` are publisher secrets. **Never** log, echo, print, or commit them.
- `config.json`, the session file, and `index.db` are gitignored — keep it that way.
- Never build or show a form that asks the end user for developer credentials.
- Redact secrets (hex tokens, long digit runs) in any log output, matching existing log helpers.
- Do not send project code or secrets to any external service unless explicitly asked.

---

## 5. Verify Before Saying "Done"

After ANY code change:

1. Build: `dotnet build src/Nuvia.App/Nuvia.App.csproj -c Debug` — must be **0 errors**.
2. Test: `dotnet test tests/Nuvia.Tests/Nuvia.Tests.csproj -c Debug` — all tests must pass
   (currently 38). If your change should add coverage, add a test.
3. If a build or test fails, fix it before reporting. Never claim success without verifying.
4. Report honestly: what changed, what was verified, and anything you could not check.

Do not rebuild the installer/publish unless asked.

---

## 6. When In Doubt

Ask a short, specific question instead of guessing. One clarifying question is always cheaper
than breaking a working feature.
