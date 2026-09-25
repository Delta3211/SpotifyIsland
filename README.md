# SpotifyIsland 🏝️

<div align="center">

![SpotifyIsland Banner](https://img.shields.io/badge/SpotifyIsland-Dynamic%20Island%20for%20Windows-1ED760?style=for-the-badge&logo=spotify&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%2010%2B-0078D4?style=for-the-badge&logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)

**A macOS Dynamic Island–inspired Spotify companion widget for Windows.**  
Sits quietly in your system tray. Springs to life the moment you open or minimize Spotify.

</div>

---

## ✨ Features

| | Feature | Description |
|---|---|---|
| 🏝️ | **Dynamic Island UI** | Floating pill that expands into a full media card on hover |
| 🎨 | **Ambient Album Glow** | Extracts album colors — neon auras, glass tints, border reflections |
| 📊 | **Audio Visualizer** | 14-bar real-time equalizer with spring physics and accent colors |
| 🔗 | **Zero-Setup Integration** | Uses Windows GSMTC — no API keys, no OAuth, no accounts |
| ⌨️ | **Global Hotkeys** | Control Spotify from anywhere on your desktop |
| 🔔 | **Auto-Bloom** | Island slides in when a new track starts |
| 🖱️ | **Edge Docking** | Drag to screen edges for left/right arrow peek tabs |
| 🔁 | **Shuffle / Repeat / Favorite** | Full playback control row in the expanded view |
| 🔇 | **Volume HUD** | Translucent volume percentage overlay on key press |
| 💾 | **Persistent Preferences** | Remembers placement, pin state, animation settings, lyric mode, and hotkeys |
| 🎧 | **Output Device Picker** | Switch the active Windows playback device from the Settings panel |
| 🌙 | **Focus Mode** | Removes visualizers for a calmer, low-distraction player |
| ✍ | **Compact Lyrics** | Keeps the current and upcoming lyric line in view |
| 📴 | **Offline State** | Clear Spotify-unavailable state with a one-click launch action |
| 🕘 | **Listening Trail** | Keeps your last 12 locally observed tracks one click away |
| 💿 | **Album Gestures** | Double-click album art to play or pause; scroll it to adjust volume |
| 🖥️ | **Display-Safe Placement** | Keeps the island on-screen after monitor or resolution changes |
| 🪟 | **System Tray** | Runs passively in background — activates when Spotify opens/minimizes |

---

## 🚀 Quick Start

### Option A — Download the EXE (No installation needed)

1. Go to [**Releases**](../../releases/latest)
2. Download `SpotifyIsland.exe`
3. Double-click to run — the island opens centered at the top of your display
4. Open Spotify — it switches from its offline state to live playback automatically ✨

> **Tip:** Right-click the tray icon → **Run on Windows Startup** to launch it automatically with Windows.

### Option B — Build from Source

**Requirements:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```bash
git clone https://github.com/Delta3211/SpotifyIsland.git
cd SpotifyIsland
dotnet run
```

### Option C — Publish self-contained EXE

```bash
dotnet publish SpotifyIsland.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./dist
# Output: ./dist/SpotifyIsland.exe
```

---

## ⌨️ Global Hotkeys

| Hotkey | Action |
|---|---|
| `Ctrl + Alt + Space` | Play / Pause |
| `Ctrl + Alt + →` | Next track |
| `Ctrl + Alt + ←` | Previous track |
| `Ctrl + Alt + ↑` | Volume Up (+4%) |
| `Ctrl + Alt + ↓` | Volume Down (-4%) |

Choose alternate keys for each action from Settings. The `Ctrl + Alt` modifier stays reserved so shortcuts remain available from any app.

---

## 🖱️ Interaction Guide

| Action | Result |
|---|---|
| **Hover** the pill | Expands into full media card |
| **Move mouse away** | Auto-collapses after 1.6s |
| **Drag to left/right edge** | Docks as an arrow peek tab |
| **Drag to top** | Snaps back to top-center |
| **Pin button (📌)** | Keep expanded permanently |
| **Album art double-click** | Play or pause the current track |
| **Scroll album art** | Adjust system volume |
| **Listening trail button** | Reopen up to 12 locally observed tracks in Spotify |
| **Settings** | Choose output device, focus mode, compact lyrics, collapse delay, hotkeys, or reset placement |
| **Right-click tray icon** | Open Spotify, toggle startup, quit |
| **Double-click tray icon** | Show island immediately |

---

## 🪟 System Tray Behavior

SpotifyIsland stays available from the **system tray**:

- Opens centered so it is always discoverable, even when Spotify is offline
- **Slides in** when Spotify opens or is minimized
- **Slides out** when Spotify is fully closed
- **Tray tooltip** updates with "Now Playing 🎵" / "Waiting for Spotify…"
- **Auto-startup** via right-click → "Run on Windows Startup" (writes to `HKCU\...\Run`)

---

## 📁 Project Structure

```
SpotifyIsland/
├── App.xaml / App.xaml.cs          # Tray-first app host + startup logic
├── MainWindow.xaml                 # Dynamic Island XAML layout
├── MainWindow.xaml.cs              # All interaction logic
├── Controls/
│   └── VisualizerControl.xaml.cs   # Custom 14-bar audio visualizer
├── Models/
│   ├── MediaTrackInfo.cs            # Track data model
│   └── IslandSettings.cs            # Persistent widget preferences
├── Services/
│   ├── SpotifyMediaService.cs      # GSMTC media session integration
│   ├── AudioCaptureService.cs      # WASAPI real-time audio capture
│   ├── SystemAudioService.cs       # Volume control + device detection
│   ├── ColorExtractor.cs           # Album art dominant color extraction
│   ├── SettingsService.cs           # Local JSON preference storage
│   └── NativeMethods.cs            # Win32 P/Invokes + Spotify window finder
└── dist/
    └── SpotifyIsland.exe           # Self-contained build output
```

---

## 🔧 Requirements

- **OS:** Windows 10 (build 19041+) or Windows 11
- **Spotify:** Desktop app (any version — including the new Chromium-based one)
- **Runtime:** None (self-contained EXE includes .NET 8 runtime)
- **Permissions:** No admin rights required

## 🔐 Spotify API queue access

SpotifyIsland intentionally works without an account or API key through Windows GSMTC. A true queue preview requires a Spotify Developer app and a user-authorized OAuth connection, so it is not enabled in the public build yet. The current release keeps playback, lyrics, device selection, and track actions fully local.

---

## 📜 License

[MIT License](LICENSE) — free to use, modify, and distribute.

---

<div align="center">

Made with ❤️ and way too much caffeine  
**If you like it, drop a ⭐ — it helps a lot!**

</div>
