# LiteConnect

**LiteConnect** is a lightweight, fast, portable remote connection manager for Windows (`.NET 8` / WPF), supporting **RDP**, **SSH**, and **VNC** sessions in a clean dark-themed tabbed interface.

---

## Quick Start — How & Where to Run

### Option 1: Run directly from source (Development)
From the repository root (`C:\dev\LiteConnect`), run:

```powershell
dotnet run --project src/LiteConnect.App
```

### Option 2: Run the compiled Debug executable directly
After running `dotnet build`, the executable is located at:

- **Windows ARM64**:
  ```text
  src\LiteConnect.App\bin\Debug\net8.0-windows\win-arm64\LiteConnect.exe
  ```
- **Windows x64**:
  ```text
  src\LiteConnect.App\bin\Debug\net8.0-windows\win-x64\LiteConnect.exe
  ```

### Option 3: Build a standalone Portable `.exe` (No .NET runtime required)
Run the included PowerShell publish script from the repository root:

```powershell
.\build.ps1
```

This produces self-contained, single-file executables and `.zip` archives for both **x64** and **ARM64** in the `publish/` folder:
- `publish\win-x64\LiteConnect.exe` (`publish\LiteConnect-win-x64.zip`)
- `publish\win-arm64\LiteConnect.exe` (`publish\LiteConnect-win-arm64.zip`)

---

## Portable Mode vs. Installed Mode

LiteConnect automatically detects whether a `portable.marker` file exists next to `LiteConnect.exe`:

| Mode | Condition | Database & Keys Location | Session Logs Location |
|------|-----------|--------------------------|-----------------------|
| **Portable Mode** *(default)* | `portable.marker` exists next to `LiteConnect.exe` | `<exe-dir>\data\connections.db` | `<exe-dir>\logs\` |
| **Installed Mode** | No `portable.marker` file | `%APPDATA%\LiteConnect\connections.db` | `%APPDATA%\LiteConnect\logs\` |

---

## Keyboard Shortcuts (SSH Terminal & App)

| Shortcut | Action |
|----------|--------|
| `Ctrl+Shift+C` (or `Ctrl+C` with selection) | Copy selected terminal text to clipboard |
| `Ctrl+Shift+V` (or `Right-Click`) | Paste clipboard text into terminal (supports bracketed paste) |
| `Shift+PageUp` / `Shift+PageDown` | Scroll up / down through terminal scrollback buffer |
| `Ctrl+Shift+R` | Start or stop SSH session recording |
| `Ctrl+N` | Create a new connection |
| `Ctrl+,` | Open Security & Encryption settings |

---

## Building & Running Tests

### Prerequisites
- Windows 10 or Windows 11 (`x64` or `ARM64`)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build the entire solution
```powershell
dotnet build LiteConnect.sln
```

### Run the unit test suite
```powershell
dotnet test LiteConnect.sln
```

### GitHub Actions Workflows
Both GitHub Actions workflows in `.github/workflows/` are configured for **manual trigger (`workflow_dispatch`)** to conserve Actions minutes:
- **CI (`ci.yml`)**: Runs `dotnet build` and `dotnet test` on `windows-latest`.
- **Release (`release.yml`)**: Computes the next semantic version tag (`patch` / `minor` / `major`), publishes self-contained `win-x64` and `win-arm64` portable `.zip` bundles, and creates a GitHub Release.

---

## Solution Structure

```text
LiteConnect/
├── src/
│   ├── LiteConnect.App/          # WPF application shell, Views, ViewModels, DI host
│   ├── LiteConnect.Core/         # Domain models, abstractions, SQLCipher data layer, session recording
│   ├── LiteConnect.Protocols/    # Protocol implementations (RDP ActiveX interop, SSH.NET client & tunnels)
│   └── LiteConnect.Terminal/     # Custom VT100/xterm hardware-accelerated WPF terminal control
├── tests/
│   └── LiteConnect.Core.Tests/   # xUnit + FluentAssertions unit & integration tests (29 passing tests)
├── .github/workflows/            # Manual CI & Release GitHub Actions workflows
├── build.ps1                     # Single-file portable release publisher (win-x64 & win-arm64)
├── portable.marker               # Enables portable mode when placed next to LiteConnect.exe
└── LiteConnect.sln
```

---

## Current Feature Status

- ✅ **Sprint 1 — Shell & Foundation**: Dark VS Code-inspired WPF UI, custom application icon, Quick Connect bar, tab strip, portable environment detection.
- ✅ **Sprint 2 — Connection Tree & Persistence**: SQLite/SQLCipher database initialization, folder/connection hierarchy CRUD, drag-and-drop organization, live search filter, Connection Properties dialog.
- ✅ **Sprint 3 — RDP Integration**: In-process RDP ActiveX (`AxMsRdpClient9NotSafeForScripting`) sessions in tabs, RD Gateway support, clipboard/drive/printer/audio/smart-card redirection, experience & performance flags, auto-reconnect, and full-screen mode.
- ✅ **Sprint 4 — Credential Storage**: AES-256 SQLCipher database encryption with transparent Windows DPAPI default mode, portable DPAPI mismatch recovery dialog, opt-in PBKDF2 Master Password mode, live mode switching (`PRAGMA rekey`), idle/workstation-lock Auto-Lock, and hierarchical credential inheritance (Connection → Folder → Global).
- ✅ **Sprint 5 — SSH & Terminal Emulator**: SSH.NET session integration (password, PEM/OpenSSH/PPK private key, keyboard-interactive auth), SHA-256 `KnownHosts` fingerprint verification dialog, HTTP/SOCKS4/SOCKS5 proxies, chained SSH Jump Host (`ProxyJump`), Local/Remote/Dynamic SOCKS port forwarding, and custom VT100/xterm `TerminalControl` (16/256/TrueColor, alternate screen buffer, scroll regions, mouse selection, copy/paste, 5 color themes, auto-resize `SIGWINCH`).
- ✅ **Sprint 6 — SSH Session Recording**: Non-blocking `Channel`-buffered `FileSessionRecorder` tapping the raw SSH stream, supporting **Plain Text** (ANSI stripped), **Timestamped** (`[yyyy-MM-dd HH:mm:ss.fff]`), and **HTML** (SGR colors converted to CSS `<span>` tags) formats, per-connection auto-recording with template variables (`{connection}`, `{host}`, `{user}`, `{date}`, `{time}`), and manual Record/Pause/Resume/Stop UI controls (`Ctrl+Shift+R`).
- ⏳ **Sprint 7 — Export / Import**: Encrypted `.lcx` bundles (AES-256-GCM), CSV import/export, and mRemoteNG `confCons.xml` import with conflict resolution.
- ⏳ **Sprint 8 — Quick Connect, Polish & Package**: Recent connections history, tab management shortcuts (`Ctrl+W`, `Ctrl+Tab`, `Ctrl+1..9`), error-handling polish, and final multi-architecture portable packaging verification.
