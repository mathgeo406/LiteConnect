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
dotnet test tests/LiteConnect.Core.Tests
```

---

## Solution Structure

```text
LiteConnect/
├── src/
│   ├── LiteConnect.App/          # WPF application shell, Views, ViewModels, DI host
│   ├── LiteConnect.Core/         # Domain models, abstractions, SQLCipher/SQLite data layer
│   ├── LiteConnect.Protocols/    # Protocol implementations (RDP ActiveX interop, SSH.NET)
│   └── LiteConnect.Terminal/     # Custom VT100/xterm WPF terminal control
├── tests/
│   └── LiteConnect.Core.Tests/   # xUnit + FluentAssertions unit & integration tests
├── build.ps1                     # Single-file portable release publisher (win-x64 & win-arm64)
├── portable.marker               # Enables portable mode when placed next to LiteConnect.exe
└── LiteConnect.sln
```

---

## Current Feature Status

- ✅ **Sprint 1 — Shell & Foundation**: Dark VS Code-inspired WPF UI, Quick Connect bar, tab strip, portable environment detection.
- ✅ **Sprint 2 — Connection Tree & Persistence**: SQLite/SQLCipher database initialization, folder/connection hierarchy CRUD, live search filter, Connection Properties dialog.
- ✅ **Sprint 3 — RDP Integration**: In-process RDP ActiveX (`AxMsRdpClient9NotSafeForScripting`) sessions in tabs, RD Gateway support, clipboard/drive/printer/audio/smart-card redirection, experience & performance flags, auto-reconnect, and full-screen mode.
- ⏳ **Sprint 4 — Credential Storage**: Windows DPAPI & Master Password encryption via SQLCipher.
- ⏳ **Sprint 5 — SSH & Terminal Emulator**: SSH.NET integration + custom VT100/xterm terminal rendering.
- ⏳ **Sprint 6 — Session Recording**: Real-time SSH session logging (PlainText, Timestamped, HTML).
- ⏳ **Sprint 7 — Import / Export**: mRemoteNG XML, CSV, and encrypted `.lcx` bundle support.
- ⏳ **Sprint 8 — Polish & Packaging**: Global defaults/settings UI, keyboard shortcuts, and final release packaging.
