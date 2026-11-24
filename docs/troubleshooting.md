# Troubleshooting

Start with `dsp status`. On Linux, run it with `sudo`. It reads the firewall itself and warns about the common problems listed below.

When you report a bug, attach the log file. `dsp version` prints its path, and the GUI shows it at the bottom.

## "I blocked a region but still get matched there"

1. Read [limitations.md](limitations.md):
   - Valve can route you **through** a relay you left open, even mid-match.
   - Some locations (Helsinki `hel`, Falkenstein `fsn`, …) have **no public relays** and can't be blocked directly. `dsp list` shows which relays they sit behind; block those.
   - In a **party**, the other players' locations count too.
2. Check where you actually are. In a match, open the console (F7) and run `net_connection_status`. The `Remote host:` line shows the data-center code.
3. **Restart Steam** after changing blocks. A running client may keep using relay routes it measured earlier.
4. Make sure the rules are enforced. `dsp status` must not show warnings like *firewall is OFF*, *service is Stopped* or *Another firewall is registered*. See the next sections.
5. **VPNs and "game boosters"** (ExitLag, NoPing, …) route traffic their own way. Turn them off while testing.
6. If Valve changed its relay list since you blocked, `dsp status` says "relay list changed". Run `dsp resync` (elevated or with sudo), or press **Re-apply now** in the GUI.

## Long queues, "match found" that times out, "failed to reach any official servers"

You blocked too much. Keep at least two or three **nearby** regions open. The CLI and GUI warn when fewer than three blockable PoPs are left. To see what's blocked, run `dsp status`. To undo everything, run `dsp unblock --all`.

## The in-game ping list still shows a blocked location

The in-game list is not a reliable indicator after blocking changes. Check with `net_connection_status` during a match instead.

## Windows

### "Windows protected your PC" / antivirus flags the exe
The binaries are unsigned. See [install.md](install.md#windows-protected-your-pc-smartscreen-and-antivirus-warnings): verify the checksum, report the false positive, or build from source.

### "Access is denied" / exit code 4
Changing Windows Defender Firewall rules needs administrator rights.
- **CLI:** right-click Terminal or PowerShell → *Run as administrator*.
- **GUI:** it asks for admin once at startup. If you declined, close it and start it again.
- `dsp list`, `dsp ping`, `dsp status` and every `--dry-run` work without admin.

### `dsp status` warns "Another firewall is registered with Windows: …"
Security suites such as Bitdefender, Norton, Avast, Kaspersky, ESET and McAfee often **replace** Windows Defender Firewall. When they do, our rules exist but are **not enforced**. You have two options:
- Use the suite's own application or IP blocking, with the IPs from `dsp list --json`.
- Switch the suite's firewall component off, so Windows Defender Firewall is active again.

### `dsp status` warns the firewall is OFF or the service is Stopped
- **Profile OFF:** go to Windows Security → *Firewall & network protection* and turn the firewall on for the profile named in the warning.
- **Service stopped:** run `services.msc`, find *Windows Defender Firewall* (`MpsSvc`), set it to Automatic and start it, or restart Windows. If the service won't start, Windows' firewall configuration may be damaged. Microsoft's *Reset firewall defaults* fixes that, but it **erases all custom rules**, so only use it as a last resort.

### `dsp status` warns about rules "from another server picker"
Older or other pickers (`server_picker_x_*`, `CS2ServerPicker_*`, `cs2-relay-*`) leave their own rules behind. They keep blocking relays. This tool never touches rules it didn't create, so remove them with the tool that made them, or by hand:
1. Run `wf.msc` → *Outbound Rules* (then *Inbound Rules*).
2. Sort by *Name*.
3. Delete the rules with that prefix.

### Removing everything this tool did
Run `dsp reset` from an elevated terminal, or press **Unblock all** in the GUI. Only rules named `DeadlockServerPicker_*` in group `DeadlockServerPicker` are removed.

### Non-English Windows
Fully supported. The tool never parses localized command output; it reads the firewall as JSON.

## Linux

### Which firewall backend is used?
`nftables` if `nft` works, otherwise `iptables`. `sudo dsp status` prints the one in use, and `--backend nftables|iptables` forces it. Both iptables flavours work: `iptables-nft` and `iptables-legacy`.

Our entries:
- nftables: table `inet dsp_deadlock_server_picker`
- iptables: chain `DSP_DEADLOCK_SERVER_PICKER`, jumped to from `OUTPUT` and `INPUT`

### "needs root" / exit code 4
Reading or changing nftables or iptables needs root, so run `sudo ./dsp …`. The GUI asks for your password once per session (polkit).

If no password dialog appears in the GUI, your desktop has no polkit agent. Install one, such as `polkit-kde-agent` or `polkit-gnome`, or use the CLI with `sudo`.

### `sudo: dsp: command not found`
`sudo` doesn't search the current directory. Use `sudo ./dsp …`, or the full path.

### Blocks are gone after a reboot
This is by design. Linux rules live in the kernel until reboot, so nothing is left behind. Re-apply after booting: `sudo ./dsp preset only-region eu`, or click Apply in the GUI.

### ufw, firewalld, docker
These were tested with the real tools in throwaway WSL distros:
- **ufw:** our rules survive `ufw reload` and `ufw disable` / `ufw enable`. With the iptables backend, our jump stays *ahead of* ufw's chains in `OUTPUT` and `INPUT`.
- **firewalld:** our rules survive `firewall-cmd --reload` and `--complete-reload`.
- **docker:** we use our own table or chain and never flush anything, so docker's rules are untouched.

Something that flushes the **entire** ruleset (`nft flush ruleset`, or a hand-written `/etc/nftables.conf` that starts with `flush ruleset`) removes our rules too. Afterwards `sudo dsp status` shows nothing blocked; re-apply.

### Logs when using sudo
With `sudo`, the config and log live in root's home: `/root/.config/deadlock-server-picker/dsp.log`.

## Tested Linux distributions

The opt-in real-firewall suite (`just integration-linux`) creates and removes real rules in a throwaway network namespace on each of these. It covers block, re-block, stale-relay resync, unblock, unblock-all, the privileged agent, reset, and byte-identical foreign rules. Run it on another WSL distro with `DSP_WSL_DISTRO=<name> just integration-linux`.

| Distribution (WSL, 2026-11-24) | nftables | iptables | Result |
|---|---|---|---|
| Ubuntu 26.04 LTS | 1.1.6 | 1.8.11 (nf_tables) | all checks pass |
| Debian 13 "trixie" | 1.1.3 | 1.8.11 (nf_tables) | all checks pass |
| Debian 13, iptables switched to legacy | 1.1.3 | 1.8.11 (legacy) | all checks pass |
| Arch Linux | 1.1.7 | 1.8.13 (nf_tables) | all checks pass |
| Fedora 45 (+ firewalld) | 1.1.6 | 1.8.13 (nf_tables) | all checks pass; survives firewalld reloads |
| Debian 13 + ufw | 1.1.3 | 1.8.11 (nf_tables) | survives ufw reload and restart |

On real desktops, CachyOS and Manjaro behave like Arch, Pop!_OS and Mint like Ubuntu, and Nobara like Fedora. Real-hardware results are tracked in issue #20.

## Steam Deck / SteamOS (untested)

Nobody has reported results yet. This is what we expect; please report what you find in #20.
- Use **Desktop Mode**. It has a polkit agent, so the GUI's password prompt works.
- `sudo` needs a password. If you never set one, open Konsole and run `passwd`.
- The tool is portable. Run it from your home folder; it never writes to the read-only system partition.
- Rules stay in place when you switch to **Game Mode**, which is a session switch, not a reboot. They are gone after a reboot.
- If `nft` is missing, the tool falls back to `iptables`. If neither exists, it says so. Installing packages on SteamOS means unlocking the read-only root (`steamos-readonly disable`), which a SteamOS update undoes.
