# Prior-art research

Read-only study done on 2026-11-24, using `gh` and one live `GetSDRConfig` fetch. Most of the projects below are GPL-3.0 and one has no license, so **we copy no code from any of them**. We only take their approach and the user pain recorded in their trackers.

| Project | Language / license | Platform and mechanism | Notes |
|---|---|---|---|
| [FN-FAL113/server-picker-x](https://github.com/FN-FAL113/server-picker-x) | C# .NET 10 + Avalonia 12, GPL-3.0 | Windows: COM `INetFwPolicy2`. Linux: `sudo iptables` | Most complete reference. 60 issues + 24 PRs read |
| [FN-FAL113/cs2-server-picker](https://github.com/FN-FAL113/cs2-server-picker) | VB.NET WinForms, GPL-3.0 | Windows: `netsh` per rule | Legacy. 103 issues read |
| [shibne/DeadlockServerPicker-linux](https://github.com/shibne/DeadlockServerPicker-linux) | Python, GPL-3.0, AUR | iptables or nftables (auto) | No issues filed |
| [holyjoespb/cs2-server-picker.sh](https://github.com/holyjoespb/cs2-server-picker.sh) | bash + curl + jq, no license | iptables INPUT | No issues filed |
| [19EB/cs2-relay-blocker](https://github.com/19EB/cs2-relay-blocker) | TypeScript, MIT | **Windows only**, `netsh` | No issues filed |
| [m13253 gist](https://gist.github.com/m13253/d3abd8be6ff8b0898235643a4308ea7e) | Python | MikroTik address list | per-PoP CIDR merge |
| [fragtion gist](https://gist.github.com/fragtion/8402cd4989b0f0d8b86baf4da38c1dbc) | PHP / Python | MikroTik, netsh, CIDR lists | parses `service_address_ranges` |

Three of the projects have empty issue trackers. To get more user pain we also mined the trackers of close siblings: [Jyben/cs2-mm-server-picker](https://github.com/Jyben/cs2-mm-server-picker), [cankhut/deadlock-server-picker](https://github.com/cankhut/deadlock-server-picker), [dom1torii/yet-another-server-picker](https://github.com/dom1torii/yet-another-server-picker) and [dfrood/SteamRouteTool](https://github.com/dfrood/SteamRouteTool).

## 1. Facts about the live data

From `GetSDRConfig/v1/?appid=1422450`, revision `1794268433`:

- **Size and caching.** About 22 KB. Sent with `Cache-Control: public, max-age=51`.
- **Top-level keys.** `revision`, `pops`, `certs`, `p2p_share_ip`, `relay_public_key`, `revoked_keys`, `typical_pings`, `success`.
- **Relays.** There are 37 PoPs. 33 have relays, 151 relays in all, 151 unique IPv4s, and no IP appears in two PoPs. Each relay has exactly two fields, `ipv4` and `port_range`. Port ranges are `27015-27060`, `27015-27140` or `27015-27078`.
- **Four PoPs have no `relays` key: `hel`, `fsn`, `eat` and `dsto`.** We cannot block them. Every existing picker hides them, so users see "Helsinki is missing" but still get matched there. `typical_pings` shows how these PoPs are actually reached: `hel↔sto 7 ms`, `hel↔sto2 8`, `fsn↔fra 5`, `eat↔sea 3`, `dsto↔sto 12`.
- **Near-duplicate PoPs.** `dfra↔fra 0 ms`, `dvie↔vie 0`, `dlhr↔lhr 1`. Blocking `fra` while leaving `dfra` open does almost nothing.
- **`geo` is not reliable.** It is meant to be `[lon, lat]`, but several Datapacket PoPs have it swapped (`dfra [50.11, 8.68]`, `dlhr [51.51, 0.12]`, `dvie [48.21, 16.36]`). `dsto` points at Moscow, and `mad [3.34, 40.23]` has the wrong sign.
- **Aliases.** `tyo→tyo1,tyo2,tyo3`, `bom2→bom`, `maa2→maa`, `hel→helm`, `eat→mwh`.
- **CS2 is different.** appid 730 has 53 PoPs, but **the same revision number**, so the revision looks like a global counter. The core takes the app ID as a parameter.
- **An old field.** `service_address_ranges` appears in old parsers but in neither config today. Our parser ignores unknown fields.

## 2. What the projects do

### Fetching
- Every project fetches on each launch. None caches the config for offline use.
- shibne fetched the **CS2** config for Deadlock until v1.5.2.
- SteamRouteTool's hardcoded CDN URL (`steamcdn-a.akamaihd.net/apps/sdr/network_config.json`) died ([SteamRouteTool#13](https://github.com/dfrood/SteamRouteTool/issues/13)).
- Two projects hardcode PoP and region tables, which go stale. shibne still references `ams4`/`hkg4` and is missing the Datapacket PoPs.

### Windows firewall
- **server-picker-x** uses COM `HNetCfg.FwPolicy2`. It has one outbound rule per PoP with the IPs comma-joined in `RemoteAddresses`, protocol any, all profiles. The rule name comes from the **description** (`server_picker_x_HongKong`), so a renamed PoP leaves an orphaned rule. Rules have no grouping. Its benchmark ([spx#17](https://github.com/FN-FAL113/server-picker-x/issues/17)) for adding 30 rules: netsh per rule 1847 ms, batched `netsh -f` 136 ms, PowerShell NetSecurity 925 ms, COM 71 ms.
- **cs2-server-picker** spawns one `cmd /c netsh` per rule. It reads state by string-matching `netsh show rule` output, which broke on Russian and Iranian Windows ([L#79](https://github.com/FN-FAL113/cs2-server-picker/issues/79) with 39 comments, [L#96](https://github.com/FN-FAL113/cs2-server-picker/issues/96), [L#97](https://github.com/FN-FAL113/cs2-server-picker/issues/97)). It also broke where `netsh` was not on PATH.
- **19EB** creates one rule per IP, all sharing one name. Deleting by that name cleans up every rule at once.
- **Both FN apps reset with `netsh advfirewall reset`, which wipes the user's entire firewall configuration.**

### Linux firewall
- **server-picker-x** uses `sudo iptables -A INPUT -s ips -j DROP`, appended to INPUT with no tag. Its reset is `iptables -F`, which flushes ufw and docker rules too. On ufw systems that kills all inbound traffic ([PR#80](https://github.com/FN-FAL113/server-picker-x/pull/80)).
- **shibne, iptables:** a dedicated `DEADLOCK_SERVER_PICKER` chain jumped to at OUTPUT/FORWARD position 1, comment-tagged. Rules are deleted by line number (racy), with one sudo process per rule.
- **shibne, nftables:** a table `inet deadlock_server_picker` with one rule per IP and no sets. A temporary file is run with `sudo nft -f`.
- **holyjoespb:** its own chain on INPUT with source matching, and one `sudo` fork per IP.
- **cankhut**'s code comments record the fixes that mattered:
  - `-w 5` for the xtables lock.
  - OUTPUT, **inserted at the top**, because ufw's ESTABLISHED accept rule otherwise wins.
  - Reset removes only rules carrying our tag.
  - Real rules are read back at startup, because reboots drop them.

### Elevation
- Both Windows apps request admin in the manifest (`requireAdministrator`).
- On Linux, server-picker-x runs the GUI unprivileged and calls `sudo iptables`. That fails without a TTY ([PR#79](https://github.com/FN-FAL113/server-picker-x/pull/79) proposes `sudo -n`, then `pkexec`). Its launcher script says not to run the GUI with `sudo` because that breaks `xdg-open`.
- shibne keeps sudo warm with a background `sudo -v` every 60 s.

### Ping
- **server-picker-x** uses ICMP via `System.Net.NetworkInformation.Ping` and fires **all PoPs at once, about 150 pings**. Behind CGNAT/DS-Lite this triggers "admin prohibited" ICMP filters ([spx#75](https://github.com/FN-FAL113/server-picker-x/issues/75)).
- shibne parses the text output of `ping` (50 workers). Jyben shells out to `ping` per server, which burns 100% CPU ([Jyben#95](https://github.com/Jyben/cs2-mm-server-picker/issues/95)) and breaks on localized output ([Jyben#73](https://github.com/Jyben/cs2-mm-server-picker/issues/73)).
- Some relays drop ICMP while the game still connects fine ([Jyben#8](https://github.com/Jyben/cs2-mm-server-picker/issues/8)). **An ICMP timeout does not mean unreachable or blocked.**

### Settings and logs
- server-picker-x keeps `settings.json` in the working directory, which breaks read-only installs ([spx#34](https://github.com/FN-FAL113/server-picker-x/issues/34)). Its log has no stack traces.
- The legacy app writes one `error_<time>.txt` per exception, so users attach 7–10 files per issue.
- When Valve's revision changes, both FN apps **unblock everything**. Users see this as "my blocks keep disappearing" ([spx#45](https://github.com/FN-FAL113/server-picker-x/issues/45), [spx#30](https://github.com/FN-FAL113/server-picker-x/issues/30)).

## 3. Recurring user pain

Counts are approximate, across about 250 issues in all the trackers.

| # | Theme | Count | Examples |
|---|---|---|---|
| A | **"Blocked it, still matched there."** SDR routes through an allowed relay to a blocked region, including the relay-less PoPs | ~40 | [spx#7](https://github.com/FN-FAL113/server-picker-x/issues/7), [spx#18](https://github.com/FN-FAL113/server-picker-x/issues/18), [spx#71](https://github.com/FN-FAL113/server-picker-x/issues/71), [spx#85](https://github.com/FN-FAL113/server-picker-x/issues/85), [cankhut#1](https://github.com/cankhut/deadlock-server-picker/issues/1), [L#39](https://github.com/FN-FAL113/cs2-server-picker/issues/39), [Jyben#21](https://github.com/Jyben/cs2-mm-server-picker/issues/21) |
| B | **The firewall isn't enforcing.** Windows Firewall is off, a third-party AV firewall (Bitdefender, Avast, Norton) is in charge, or the firewall service is broken. The app still reports success | ~20 | [spx#6](https://github.com/FN-FAL113/server-picker-x/issues/6), [spx#29](https://github.com/FN-FAL113/server-picker-x/issues/29), [spx#68](https://github.com/FN-FAL113/server-picker-x/issues/68), [spx#38](https://github.com/FN-FAL113/server-picker-x/issues/38), [L#81](https://github.com/FN-FAL113/cs2-server-picker/issues/81) |
| C | **Linux rules that silently do nothing.** They sit on INPUT or are appended after ufw/docker chains. Mostly on CachyOS | ~11 | [spx#26](https://github.com/FN-FAL113/server-picker-x/issues/26), [spx#42](https://github.com/FN-FAL113/server-picker-x/issues/42), [spx#82](https://github.com/FN-FAL113/server-picker-x/issues/82), [spx#83](https://github.com/FN-FAL113/server-picker-x/issues/83) |
| D | **Leftover rules, no clean reset, "broke my internet."** Includes rules left by *other* pickers | ~15 | [spx#55](https://github.com/FN-FAL113/server-picker-x/issues/55), [L#71](https://github.com/FN-FAL113/cs2-server-picker/issues/71), [Jyben#106](https://github.com/Jyben/cs2-mm-server-picker/issues/106), [Jyben#64](https://github.com/Jyben/cs2-mm-server-picker/issues/64), [SteamRouteTool#8](https://github.com/dfrood/SteamRouteTool/issues/8) |
| E | **Missing PoPs and revision churn.** Relay-less PoPs are hidden, blocks are wiped on revision change, and Linux rules are lost on reboot | ~20 | [spx#52](https://github.com/FN-FAL113/server-picker-x/issues/52), [spx#45](https://github.com/FN-FAL113/server-picker-x/issues/45), [L#110](https://github.com/FN-FAL113/cs2-server-picker/issues/110), [YASP#2](https://github.com/dom1torii/yet-another-server-picker/issues/2) |
| F | **Over-blocking.** Match-accept timeouts, 10-minute queues, "failed to reach any official servers" | ~10 | [spx#84](https://github.com/FN-FAL113/server-picker-x/issues/84), [L#55](https://github.com/FN-FAL113/cs2-server-picker/issues/55), [L#82](https://github.com/FN-FAL113/cs2-server-picker/issues/82), [Jyben#15](https://github.com/Jyben/cs2-mm-server-picker/issues/15) |
| G | **Locale-dependent failures** from parsing `netsh` or `ping` output | ~8 | [L#79](https://github.com/FN-FAL113/cs2-server-picker/issues/79), [L#97](https://github.com/FN-FAL113/cs2-server-picker/issues/97), [Jyben#73](https://github.com/Jyben/cs2-mm-server-picker/issues/73) |
| H | **VPNs and routing tools** (ExitLag) overriding everything | ~9 | [spx#27](https://github.com/FN-FAL113/server-picker-x/issues/27), [spx#67](https://github.com/FN-FAL113/server-picker-x/issues/67), [L#112](https://github.com/FN-FAL113/cs2-server-picker/issues/112) |
| I | **Elevation.** Forgetting admin, sudo with no TTY | ~8 | [L#52](https://github.com/FN-FAL113/cs2-server-picker/issues/52), [Jyben#124](https://github.com/Jyben/cs2-mm-server-picker/issues/124), [PR#79](https://github.com/FN-FAL113/server-picker-x/pull/79) |
| J | **UI freezes.** On Windows 10, firewall work ran on the UI thread while a dialog closed | ~11 | [spx#47](https://github.com/FN-FAL113/server-picker-x/issues/47), [PR#78](https://github.com/FN-FAL113/server-picker-x/pull/78) |
| K | **AV false positives / SmartScreen** on unsigned binaries | 3 + FAQ | [L#88](https://github.com/FN-FAL113/cs2-server-picker/issues/88), [L#106](https://github.com/FN-FAL113/cs2-server-picker/issues/106) |

Users diagnose routing themselves in the Deadlock console (F7). `net_connection_status` prints `Remote host: fra`, and the `Ping location:` line lists relay pings ([spx#47](https://github.com/FN-FAL113/server-picker-x/issues/47), [spx#84](https://github.com/FN-FAL113/server-picker-x/issues/84)).

There is **no evidence at all about Steam Deck / SteamOS** in any tracker. Treat it as untested.

## 4. Adopt

1. **Our own namespace, removed in one pass.**
   - Windows: rule names `DeadlockServerPicker_<pop>_<dir>` plus the group `DeadlockServerPicker`.
   - nftables: our own table `inet deadlock_server_picker`.
   - iptables: our own chain `DEADLOCK_SERVER_PICKER`, with comment-tagged rules.
   - Everything is keyed by **PoP code**, never by description.
2. **nftables first.** A `drop` in our own base chain is final, whatever accept rules ufw, firewalld or docker have in other tables. Apply it as one atomic `nft -f -` transaction.
3. **iptables fallback.** The jump is inserted at position 1 of OUTPUT and INPUT. Use `-w` for the xtables lock and `iptables-restore --noflush` for atomic chain contents.
4. **The firewall is the source of truth.** Read the real state back for `status`, and after every apply to verify it.
5. **Re-apply on revision change.** Diff the old and new relay IPs per PoP and re-apply. Never unblock everything.
6. **Show relay-less PoPs** as "not blockable". Name the relay PoPs they are reached through (from `typical_pings`), e.g. "to avoid hel, block sto + sto2".
7. **Region data file, plus a geo fallback** that is defensive about swapped coordinates. Flag guessed regions.
8. **Ping without admin, with capped concurrency** (8). Use OS ICMP APIs, never parse `ping` text. Show "timeout" without implying "blocked".
9. **A scriptable CLI** with `--json` and a `--dry-run` that prints the exact commands.
10. **One rotating log** in the per-user config dir, with OS, backend and stack traces.
11. **Explain routing up front, in the UI and the README.** Blocking biases relay selection and cannot force a server. Everyone in a party should block the same set. Show how to verify with `net_connection_status`.

## 5. Avoid

1. **Global resets.** No `netsh advfirewall reset`, `iptables -F`, `nft flush ruleset`, or dumping the whole ruleset into `/etc/nftables.conf` or `iptables-save` files.
2. **Silently hiding relay-less PoPs.**
3. **Unblocking everything when the revision changes.**
4. **Reporting "blocked successfully" without checking** exit codes and reading the state back.
5. **Parsing localized output** (`netsh`, `ping`), or relying on PATH for system tools.
6. **About 150 simultaneous pings**, and one process per ping.
7. **One sudo process per IP, line-number deletes, and non-atomic multi-step applies.**
8. **Settings or logs relative to the working directory**, and one file per error.
9. **Running the Linux GUI as root.** Use `sudo -v` keep-alive threads or sudo prompts from a GUI with no TTY instead.
10. **Hardcoded PoP lists or config URLs.**
11. **Firewall work on the UI thread.**
12. **Over-promising.**

## 6. Requirements derived from this research

These are in addition to the must-haves in the Roadmap.

| ID | Requirement | From |
|---|---|---|
| R1 | `reset` removes only our namespace. It works offline (no config needed) and leaves zero traces, verified by an integration test | D, Avoid 1 |
| R2 | `status` reads the firewall, never a local state file. After every apply we read back and verify | B, C, Adopt 4 |
| R3 | Relay-less PoPs are listed as not blockable, with the nearby relay PoPs to block instead | A, E |
| R4 | On a revision change we diff and offer to re-apply. We never unblock automatically | E |
| R5 | Warn before applying a selection that leaves fewer than 3 blockable PoPs unblocked | F |
| R6 | The README and UI state the routing limitation, the party caveat and the `net_connection_status` check | A |
| R7 | On Windows: no `netsh` output parsing, absolute path to `powershell.exe`, locale-independent JSON readback | G |
| R8 | Detect a disabled Windows Firewall profile or service and say so, instead of reporting success | B |
| R9 | Linux iptables: jump at position 1 of OUTPUT and INPUT, `-w`, atomic restore. nftables: our own table with base chains | C |
| R10 | Ping: concurrency 8, OS ICMP API, "timeout" is not "blocked" | G, K |
| R11 | Firewall and network work off the UI thread | J |
| R12 | Troubleshooting docs cover third-party AV firewalls, VPNs/ExitLag, leftover rules from other pickers (read-only detection), SmartScreen | B, D, H, K |
| R13 | Linux blocks don't survive a reboot (by design, nothing to clean up). Say so, and offer opt-in persistence later | E |
