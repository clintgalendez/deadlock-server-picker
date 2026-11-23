# ADR 0003: Elevation (admin / root)

- Status: accepted
- Date: 2026-11-24

## Context

- Changing firewall rules needs admin on Windows and root (`CAP_NET_ADMIN`) on Linux.
- Reading the firewall needs no admin on Windows (`Get-NetFirewallRule` works unelevated, verified). On Linux it needs root.
- Listing PoPs and pinging need no privileges on either OS.

Prior art (docs/research.md):
- Both Windows pickers request admin in the manifest.
- On Linux, running the GUI as root breaks the desktop session (`xdg-open`, Wayland), and `sudo` from a GUI with no TTY fails silently.

## Decision

### CLI (`dsp`), both OSes: never self-elevates

- `list`, `ping`, `status` (on Windows) and every `--dry-run` work unprivileged.
- Mutating commands (`block`, `unblock`, `preset`, `reset`, `resync`) check for elevation **before doing anything**. If it is missing they exit with code 4 and a precise message:
  - Windows: "Run this from an elevated terminal: right-click Terminal → Run as administrator."
  - Linux: "Run with sudo: `sudo dsp …`" (`status` on Linux also needs root).
- The Windows CLI's manifest stays `asInvoker`. Relaunching through `runas` would open a new console window that closes immediately, and its output would be lost.

### GUI on Windows: `requireAdministrator` in the manifest

- One UAC prompt at launch. After that, firewall reads and writes run in-process through the same runner the CLI uses.
- We rejected "elevate only on Apply" with a `runas` self-relaunch or helper, because:
  - `runas` (ShellExecute) cannot pipe stdin or stdout. The elevated helper would have to read a script or plan from a **file written by the unelevated process**. Same-user malware could swap that file between the write and the elevated run, turning our UAC prompt into code execution as admin.
  - The GUI exists to apply rules, so nearly every session needs elevation anyway. Prompting once is less friction than prompting on every Apply.
  - It is what users of the existing pickers already expect.
- What it costs us: the GUI process runs elevated for its whole lifetime. It only talks to `api.steampowered.com` over TLS and parses JSON with System.Text.Json. It loads no plugins and runs no scripts from disk.

### GUI on Linux: unprivileged GUI + `pkexec` per firewall action

- The GUI never runs as root.
- Firewall commands (read and apply) go through a runner that prefixes `pkexec`. The ruleset is passed **on stdin** (`pkexec nft -f -`), never through a temporary file.
- If `pkexec` is missing or the user cancels, the GUI shows a clear message and the exact `sudo dsp …` command to run instead.
- Each firewall action triggers one polkit prompt. The GUI keeps the number of actions low: one read at startup on demand, one apply per click.
- If the GUI is already running as root (`sudo -E`), it skips `pkexec`.

## Consequences

- Windows users see one UAC prompt when they open the GUI.
- Linux users see a polkit dialog per firewall action. On a system without a polkit agent (for example SteamOS Game Mode or a bare WM), they use the CLI with `sudo`, which is documented.
- The elevation check lives in Core (`Elevation.IsElevated()`), so the CLI and GUI behave the same.
