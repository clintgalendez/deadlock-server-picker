# ADR 0002: Firewall backends and "our namespace"

- Status: accepted
- Date: 2026-11-24

## Context

We block a PoP by dropping traffic to and from every relay IPv4 it lists. Constraints:

- **The firewall is the source of truth.** `status` reads it. We keep no local state file that could drift (research R2).
- **Only our own entries.** No code path touches firewall entries outside our namespace. `reset` must leave zero traces (R1).
- **Testable.** Backends run commands through an injectable runner, so tests can assert the exact commands (golden files). `--dry-run` prints those same commands.
- **Locale-independent** on Windows (R7).
- **Effective next to ufw, firewalld and docker** on Linux (R9).

## Decision

### Shared model

- Desired state and current state are both maps of **PoP code → set of IPv4 addresses**.
- The core planner diffs them into per-PoP changes:
  - `add`: newly blocked
  - `remove`: unblocked
  - `update`: relay IPs changed after a revision bump
- An empty diff produces no commands, which makes apply idempotent.
- After applying we read the state back and compare it with the desired state.

We block **all protocols**, not just UDP to the `port_range`:
- Relays serve nothing else we need.
- Port ranges vary per PoP.
- Our own ICMP ping then shows "timeout" for blocked PoPs, a free sanity signal. The UI never treats a timeout alone as "blocked".

### Windows: NetSecurity PowerShell module (not `netsh`)

- One PowerShell process per operation: `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create([Console]::In.ReadToEnd()))"`.
- The script arrives on stdin, so there is no temporary file and nothing to tamper with between writing and running.
- `powershell.exe` is resolved by absolute path (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`).

Per blocked PoP, two rules:

| Name (unique ID) | DisplayName | Group | Direction | RemoteAddress |
|---|---|---|---|---|
| `DeadlockServerPicker_<pop>_out` | `DeadlockServerPicker_<pop>` | `DeadlockServerPicker` | Outbound | all relay IPs |
| `DeadlockServerPicker_<pop>_in` | `DeadlockServerPicker_<pop>` | `DeadlockServerPicker` | Inbound | all relay IPs |

- Both rules: Action Block, Protocol Any, Profile Any.
- **Read:**
  - `Get-NetFirewallRule -Group 'DeadlockServerPicker'`, piped into `Get-NetFirewallAddressFilter`, then `ConvertTo-Json`.
  - The output is structured and identical on every Windows display language. It works without admin.
  - Only rules whose Name matches `^DeadlockServerPicker_[a-z0-9]+_(in|out)$` count as ours.
- **Apply:**
  - Removals go in one `Remove-NetFirewallRule -Name @(...)` call.
  - Additions are `New-NetFirewallRule` calls, all in the same script with `$ErrorActionPreference = 'Stop'`.
- **Reset:** remove every rule in our group whose name carries our prefix. Nothing else.

Why not `netsh`:
- Its output is localized, and parsing it broke the legacy picker on Russian and Iranian Windows.
- It cannot set a rule's unique Name or Group.

Why not COM `INetFwPolicy2` (fastest in server-picker-x's benchmark):
- It is not a command, so `--dry-run` could not print something the user can run or audit, and golden tests would need a COM fake.

The benchmark's slow PowerShell numbers come from one cmdlet invocation per rule. We batch every change into one process.
- A PowerShell process starts in about 0.5–1 s, and a full apply of 33 PoPs is a few seconds.
- If that proves too slow in manual testing, a COM backend behind the same interface is the upgrade path.

### Linux: nftables preferred

- One table, `inet deadlock_server_picker`, containing:
  - one IPv4 set per blocked PoP, `pop_<code>`
  - two base chains, `output` (hook output) and `input` (hook input), at priority `filter`, policy accept
  - one drop rule per set and chain: `ip daddr @pop_ams drop` / `ip saddr @pop_ams drop`
- A drop in our own base chain is final, whatever accept rules ufw, firewalld or docker have in other tables.
- **Apply:** we always regenerate the full table and feed it to `nft -f -` as one atomic transaction:

  ```
  table inet deadlock_server_picker {}
  delete table inet deadlock_server_picker
  table inet deadlock_server_picker { ... }
  ```

  - The first line makes the delete safe when the table does not exist. This works on nft versions older than `destroy` (1.0.8).
  - Re-applying the same state produces the same ruleset.
- **Read:** `nft list table inet deadlock_server_picker`, parsing the set names and elements. Text output works everywhere; JSON output (`-j`) needs libjansson.
- **Reset:** the same create-then-delete pair. It needs no config or network.
- We use per-PoP sets rather than one big set, so the firewall itself records *which PoP* each IP belongs to. `status` then needs no config, and IPs left over from an older revision remain attributable.

### Linux: iptables fallback

- Used when `nft` is missing or not usable (for example, iptables-legacy-only systems).
- Our own chain is `DEADLOCK_SERVER_PICKER`, jumped to at **position 1** of `OUTPUT` and `INPUT`.
- Every rule carries `-m comment --comment dsp:<pop>`.
- **Apply:**

  ```
  iptables-restore -w --noflush
  *filter
  :DEADLOCK_SERVER_PICKER - [0:0]
  -F DEADLOCK_SERVER_PICKER
  -A DEADLOCK_SERVER_PICKER -d 1.2.3.4/32 -m comment --comment dsp:ams -j DROP
  -A DEADLOCK_SERVER_PICKER -s 1.2.3.4/32 -m comment --comment dsp:ams -j DROP
  COMMIT
  ```

  - The chain contents are replaced atomically.
  - The jumps are added with `iptables -w -I OUTPUT 1 -j DEADLOCK_SERVER_PICKER` only when the read step shows they are missing.
- **Read:** `iptables -w -S DEADLOCK_SERVER_PICKER` and `iptables -w -S OUTPUT` / `INPUT`, to see whether the jumps are present.
- **Reset:** delete our jumps from OUTPUT and INPUT, then `-F` and `-X` our chain.

### Detection and override

- Windows always uses NetSecurity.
- On Linux we use nftables if `nft --version` runs, otherwise iptables.
- `--backend nftables|iptables|windows` overrides detection.

### Persistence

- Windows rules persist across reboots. That is the normal user expectation, and `reset` removes them.
- Linux rules (both backends) **do not survive a reboot**. We say so in the UI and docs (R13). Opt-in persistence (a systemd unit that restores only our table) may come later.

## Consequences

- Golden-file tests per backend cover add, remove, update and reset scripts.
- Integration tests:
  - Linux runs in a throwaway network namespace (WSL on the maintainer machine).
  - The Windows test is opt-in and run elevated by the maintainer (manual-test issue).
- A rule someone else creates that happens to match our exact name and group would be treated as ours. That is acceptable, since the prefix is unique to this project.
