# Deadlock Server Picker

Choose which server regions you can be matched into in **Deadlock** (Valve, Steam app 1422450).

It works by adding firewall rules that block Valve's **Steam Datagram Relay (SDR)** points of presence (PoPs) in regions you don't want. Matchmaking can't reach those relays, so it prefers the ones you left open.

- Windows 10/11 x64 (Windows Defender Firewall) and Linux x64 (nftables or iptables). Steam Deck / SteamOS is a stretch goal and is untested.
- Portable single-file binaries: a command-line tool (`dsp`) and a desktop GUI. No installer.
- Never touches game files, game memory or launch options. It only adds and removes its own firewall rules.

> **Status:** under active development; releases are marked *pre-release* until they are verified in game. See the pinned [Roadmap](https://github.com/clintgalendez/deadlock-server-picker/issues?q=is%3Aissue+Roadmap) issue.

## Read this first: what it can and can't do

Blocking is a **strong nudge, not a guarantee**:

- **Valve only publishes relay addresses, not game server addresses.** SDR can still route you *through* a relay you left open to a game server in a region you blocked, sometimes even mid-match.
- **Some locations can't be blocked at all.** For example, Helsinki (`hel`) and Falkenstein (`fsn`) have no public relays; you reach them through nearby relays such as Stockholm or Frankfurt. The app shows them as *not blockable* and tells you which relays they sit behind.
- **Blocking too much hurts.** If you leave too few regions open you will see long queues and **match-accept timeouts**. Keep at least two or three nearby regions open.
- **Parties:** matchmaking considers everyone in the party. Your blocks don't apply to your friends.
- **VPNs, "game boosters" (ExitLag and similar) and third-party firewalls or antivirus** can bypass or override Windows Defender Firewall rules.
- **Linux:** blocks are not kept across a reboot. Re-apply after rebooting.

To see where you actually are, open the Deadlock console (F7) in a match and run `net_connection_status`. It prints the data center you are connected to, e.g. `Remote host: fra`.

Full details: [docs/limitations.md](docs/limitations.md).

### In-game region setting (complementary)

Deadlock also has its own matchmaking region override. Set it in the console (F7):

| Command | Region |
|---|---|
| `citadel_region_override -1` | Automatic |
| `citadel_region_override 0` | North America |
| `citadel_region_override 1` | Europe |
| `citadel_region_override 2` | Asia |
| `citadel_region_override 3` | South America |
| `citadel_region_override 5` | Oceania |

This tool never changes it for you; it only shows the matching command as a tip.

## How it works

1. Fetches Valve's public network config: `https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=1422450`. No API key is needed. The last good copy is cached so the app also works offline, with a "stale data" warning.
2. Groups the PoPs into regions (NA, EU, Asia, SA, Oceania, Middle East, Africa) and pings one relay per PoP.
3. For every PoP you block, adds firewall rules dropping traffic to and from all of that PoP's relay IPs:
   - **Windows:** two rules per PoP (in and out) named `DeadlockServerPicker_<pop>_in` / `_out` in the group `DeadlockServerPicker`.
   - **Linux (nftables):** its own table `inet dsp_deadlock_server_picker`.
   - **Linux (iptables fallback):** its own chain `DSP_DEADLOCK_SERVER_PICKER`.
4. Reads the firewall back to show status. **The firewall is the source of truth.**
5. `reset` removes everything the app created and nothing else.

Design records: [docs/adr](docs/adr). Prior art and lessons learned: [docs/research.md](docs/research.md).

## Install

Download the archive for your OS from [Releases](https://github.com/clintgalendez/deadlock-server-picker/releases), verify it, and extract it anywhere. See [docs/install.md](docs/install.md) for checksums, SmartScreen and antivirus warnings (the binaries are unsigned), Linux notes and uninstalling.

## Usage (CLI)

```
dsp list                      # PoPs grouped by region
dsp ping                      # latency per PoP
dsp status                    # what is blocked right now (read from the firewall)
dsp block <pop|region>...     # e.g. dsp block asia sa
dsp unblock <pop|region>...   # or: dsp unblock --all
dsp preset only-region eu     # block everything outside EU
dsp preset within 80          # block every PoP slower than 80 ms
dsp reset                     # remove every rule this tool created
```

Every command accepts `--json`. Commands that change the firewall accept `--dry-run`, which prints the exact commands instead of running them. Changing the firewall needs an elevated terminal on Windows and `sudo` on Linux. On Linux, `status` also needs `sudo`, because reading nftables or iptables requires root.

Other options:
- `--app <id>`: another Steam app ID. Default is 1422450 (Deadlock); 730 is Counter-Strike 2, which uses the same relay network.
- `--offline`: use the cached relay list without downloading.
- `--backend windows|nftables|iptables`: force a backend.

`dsp version` prints the config directory and the log file path.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | ok |
| 1 | error |
| 2 | usage error |
| 3 | relay list unavailable (offline and nothing cached) |
| 4 | needs administrator or root |

## Building from source

Requirements: .NET 10 SDK, [`just`](https://github.com/casey/just), and bash (Git Bash on Windows).

```
just build      # self-contained single-file binaries for win-x64 and linux-x64 in artifacts/
just test
```

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Disclaimer

Not affiliated with, endorsed by or supported by Valve Corporation. Deadlock, Steam and Counter-Strike are trademarks of Valve Corporation. The tool only changes your own operating system's firewall. Use it at your own risk.

## License

[MIT](LICENSE)
