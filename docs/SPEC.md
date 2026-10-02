# Nuvia — Product Specification

## Overview

Nuvia is a native Windows desktop application that provides a private, Telegram-backed file vault. Users store, browse, search and manage files using their Telegram account's free cloud storage — without the Telegram Desktop app.

---

## Non-negotiable stack

- C# / .NET 8
- WPF with native XAML controls
- OutputType WinExe
- WTelegramClient for Telegram user API integration, only from phase 3
- SQLite for the local index, only when the persistence phase begins
- Inno Setup 6 for a per-user installer
- One application process, no local HTTP server

---

## Forbidden

- HTML UI, index.html, CSS UI, React, Vue
- Electron, Tauri, WebView2
- Python, Telethon, PyInstaller
- FastAPI, uvicorn, localhost UI
- .cmd or .vbs launchers
- Official Telegram Desktop API credentials
- Asking end users for api_id or api_hash
- Browser or console windows as the product
- Credentials, sessions, phone numbers, passwords or login codes in logs, repository files, screenshots, test fixtures or chat output

---

## Product rules

- Product name and window title: **Nuvia**
- English UI
- Prominent unofficial-app disclosure displayed at startup and accessible from the settings
- No upload, message or group creation without an explicit user action
- No chat browsing, contact export, auto-reply or bulk messaging
- Local rename must not rename or edit remote messages
- No fake success states in production integrations
- Preserve local data on uninstall unless the user explicitly opts in
- Do not silently expand the scope

---

## Engineering rules

- Implement only the requested phase.
- Use asynchronous network and file operations without blocking WPF.
- Keep UI code separate from Telegram and persistence services.
- Do not invent WTelegramClient APIs.
- Inspect the documentation/source for the exact pinned package version.
- Never claim that Windows UI tests passed on a Linux sandbox.
- Report exactly which checks ran and which require Windows.
- If an API or product requirement is incompatible, explain it before changing it.