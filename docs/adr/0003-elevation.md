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
- On first use (reading or changing the firewall), the GUI starts **one privileged helper for the whole session**: `pkexec <dir>/dsp agent`. The user authenticates **once**. *(Revised 2026-11-24, #23.)*
- The agent speaks JSON lines on stdin/stdout. No temporary files, no sockets.
- It accepts only three high-level operations:
  - `read`
  - `apply` a desired map of PoP code → IPv4 list
  - `reset`
- It validates them with the same rules as the SDR parser (`^[a-z0-9]{2,16}$` codes, literal dotted-quad IPv4, size caps), then plans, executes and verifies **itself** through the backends.
- The GUI never sends commands. The worst a hijacked GUI process can do is block or unblock IPv4 addresses inside our own namespace.
- The agent exits when its stdin closes, i.e. when the GUI exits.
- If `pkexec` is missing or the user cancels (exit 126/127), the GUI shows a clear message and the `sudo ./dsp …` alternative.
- If the GUI is already running as root, it uses the backends in-process instead.

Why not "a `pkexec` per firewall command" (the first version of this ADR):
- An nftables read is two commands. An apply is a write plus a read-back for verification.
- So one click on Apply would have meant three or four password prompts.

## Consequences

- Windows users see one UAC prompt when they open the GUI.
- Linux users see one polkit dialog per GUI session, the first time the GUI reads or changes the firewall. On a system without a polkit agent (for example SteamOS Game Mode or a bare WM), they use the CLI with `sudo`, which is documented.
- Like `sudo ./dsp`, pkexec runs the `dsp` binary from wherever the user extracted it. Anyone who can already write to that folder as the user could swap the binary before the prompt. That is inherent to portable tools that elevate, and it is documented in SECURITY.md's scope.
- The elevation check lives in Core (`Elevation.IsElevated()`), so the CLI, agent and GUI behave the same.
