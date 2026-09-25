# Changelog

All notable changes to SpotifyIsland are documented here.  
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

---

## [1.1.0] — 2026-09-25

### Added
- Persistent preferences for island placement, pin state, collapse delay, auto-bloom, focus mode, compact lyrics, and hotkey bindings
- Output-device picker for changing the Windows default playback device from the island
- Configurable `Ctrl + Alt` hotkeys for playback, navigation, and volume
- Focus mode that hides visualizers for a calmer playback surface
- Compact synced-lyrics view with the active and next line
- Clear Spotify-offline state with a direct launch action
- Album-art actions for opening the current artist or album and copying a Spotify search link

### Fixed
- Startup crash caused by the collapse-delay slider firing before its timers were initialized
- Hidden-instance wake-up now replays the island entrance instead of leaving it off-screen
- Single-instance event listener is retained for the full app lifetime

---

## [1.0.0] — 2026-09-24

### Added
- 🏝️ Dynamic Island floating pill — compact & expanded states with smooth transitions
- 🎨 Ambient album glow engine — real-time dominant color extraction from artwork
- 📊 14-bar WASAPI audio visualizer with spring physics
- 🪟 **System tray integration** — runs passively until Spotify opens/minimizes
- 🔔 **Auto-Bloom** — island slides in when Spotify launches or is minimized
- ⌨️ Global hotkeys: Play/Pause, Next, Prev, Volume Up/Down
- 🔁 Shuffle / Repeat / Favorite controls in expanded view
- 🔇 Volume HUD overlay with percentage bar
- 🖱️ Edge docking — snap to left/right screen edges as arrow peek tabs
- 📌 Pin button to keep island expanded permanently
- 🎛️ Windows GSMTC integration (no API keys needed)
- 🏷️ Demo mode with built-in preview tracks
- 💾 Run on Windows Startup toggle (right-click tray menu)
- 🎯 Self-contained single-file EXE — no .NET runtime installation required
