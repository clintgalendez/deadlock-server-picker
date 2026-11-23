# ADR 0001: Language and GUI toolkit

- Status: accepted
- Date: 2026-11-24

## Context

We need:
- A CLI and a GUI for Windows 10/11 x64 (primary), Linux x64 (secondary) and SteamOS (stretch goal).
- Portable single-file binaries with no installer.
- Every release artifact must be buildable from the maintainer's machine **with one command**. GitHub Actions is not available. Docker is allowed but not installed.

Maintainer machine (checked 2026-11-24):
- Windows 11 x64, git, gh, WSL2 Ubuntu, Python, Node, .NET SDK 10.0.302.
- **No Go, no Docker, no C compiler.**

The brief's default recommendation was Go, provided the GUI constraint holds. We also need:
- ICMP ping without admin rights.
- Clean process execution for the firewall backends.

## Options considered

| Option | Windows GUI | Linux GUI from a Windows host | Notes |
|---|---|---|---|
| Go + Fyne | needs cgo + a mingw gcc | needs cgo + a Linux sysroot, or `fyne-cross` (Docker) | toolchain not installed; cross-cgo is the hard part |
| Go + Wails | pure Go (WebView2) | needs cgo + webkit2gtk headers; runtime needs webkit2gtk (often absent on SteamOS) | |
| Go + local web UI | pure Go | pure Go | means running a **privileged localhost HTTP server** (CSRF / DNS-rebinding surface) and opening a browser tab |
| Tauri (Rust) | WebView2 | needs a Linux build host + webkit2gtk | Rust not installed |
| **.NET 10 + Avalonia 12** | native | **`dotnet publish -r linux-x64` works from Windows**, no extra toolchain | SDK already installed; self-contained single file |

## Spike (time-boxed, about 20 minutes)

- Built a code-only Avalonia 12 window (no XAML) in one project with `PublishSingleFile`, `SelfContained`, `PublishTrimmed` and `InvariantGlobalization`.
- `dotnet publish -c Release -r win-x64` took about 65 s cold and produced a 37 MB exe. The window opened and closed itself.
- `dotnet publish -c Release -r linux-x64`, **run on the same Windows machine**, took about 31 s and produced a 35 MB ELF.
- Under WSL2 (WSLg) the Linux binary first failed with `Unable to load shared library 'libICE.so.6'`. After `apt install libice6 libsm6 libfontconfig1` the window opened.
  - Those libraries ship with every desktop distro and with SteamOS desktop mode.
  - We document them as runtime requirements.
- The CLI (trimmed, single file) is about 13 MB on Windows and 15 MB on Linux.

We did not install Go just to confirm that cgo cross-compilation fails. The Fyne and Wails docs both require cgo for Linux targets, and this machine has no C toolchain or Docker.

## Decision

- **C# on .NET 10, Avalonia 12 for the GUI.**
  - Build the GUI in code only (no XAML compiler, trim-safe), with no MVVM framework.
  - Ship self-contained, trimmed, single-file binaries.
- **Projects:**
  - `DeadlockServerPicker.Core` (class library, trimmable)
  - `dsp` (CLI)
  - `DeadlockServerPicker` (GUI)
  - one xUnit v3 test project on Microsoft.Testing.Platform
- **Tooling:**
  - The task runner is `just`. Recipes run in bash (on Windows, from Git Bash).
  - Lint: `dotnet format` plus .NET analyzers, with warnings as errors.
  - Audit: NuGet lock files with `--locked-mode`, NuGet audit, `dotnet list package --vulnerable`, and `nuget-license` with an allowlist.
- **ICMP ping:** `System.Net.NetworkInformation.Ping`. On Windows it calls IcmpSendEcho (no admin). On Linux it uses unprivileged ICMP sockets, falling back to the `ping` binary.
- **CLI argument parsing:** hand-written. There are seven verbs and a few flags, so this avoids a dependency.

## Consequences

- Release artifacts are larger than Go's: about 13–15 MB for the CLI and about 35 MB for the GUI, before compression.
- Linux GUI runtime needs glibc, `libICE`, `libSM`, `libfontconfig` and X11 or XWayland. The CLI has no GUI dependencies.
- Contributors need the .NET 10 SDK and `just`. The `global.json` file pins the SDK feature band.
- Self-extracting single-file .NET apps (and Go binaries) are known antivirus false-positive magnets. We document checksum verification and building from source.
- Antivirus heuristics and SmartScreen warnings are expected, because there is no code-signing certificate.
