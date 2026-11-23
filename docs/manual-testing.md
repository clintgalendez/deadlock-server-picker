# Manual test checklists

These cover what automated tests can't: real firewalls on real machines, and real matchmaking. Releases stay **pre-release** until the in-game checklist (§3) has passed.

Record results in the matching `needs-manual-test` issue. Include the OS version, `dsp version`, and anything unexpected. Attach `dsp.log`.

## 1. Windows Defender Firewall (issue #19)

Use Windows 10 or 11 x64, with Windows Defender Firewall as the active firewall and no third-party firewall suite. Run from the release zip.

**Normal terminal:**
- [ ] `dsp version` prints the version, config dir and log path.
- [ ] `dsp list` and `dsp ping` work without admin. Pings are plausible for your location.
- [ ] `dsp status` works without admin and shows "Nothing is blocked" (or your existing blocks).
- [ ] `dsp block asia --dry-run` prints a plan and a PowerShell script, and changes nothing. Check with `wf.msc` → no `DeadlockServerPicker_*` rules.
- [ ] `dsp block asia` without admin exits with code 4 and tells you to use an elevated terminal.

**Elevated terminal** (right-click Terminal → *Run as administrator*):
- [ ] `dsp block asia` reports "Done. Verified in the firewall: 7 PoPs blocked".
- [ ] `wf.msc` shows an Inbound and an Outbound rule per Asia PoP, named `DeadlockServerPicker_<pop>_in` / `_out`, in group `DeadlockServerPicker`, action Block, with the relay IPs as remote addresses.
- [ ] `dsp ping` now shows `timeout` for the Asia PoPs.
- [ ] Running `dsp block asia` again prints "Nothing to change".
- [ ] `dsp unblock hkg` removes only the hkg rules.
- [ ] `dsp preset only-region eu` blocks everything outside Europe and prints the `citadel_region_override 1` tip.
- [ ] Turn off the firewall for the *Public* profile. `dsp status` warns that rules are not enforced there. Turn it back on.
- [ ] `dsp reset` removes every `DeadlockServerPicker_*` rule. Check in `wf.msc` and with `Get-NetFirewallRule -Group DeadlockServerPicker` (nothing returned). All other rules are untouched.
- [ ] From a source checkout, in an elevated shell: `DSP_INTEGRATION_WINDOWS=1 just test` runs `RealFirewallRoundTrip`, which passes and leaves no rules.
- [ ] If possible, on a non-English Windows display language: `dsp status` and `dsp block` still work.

## 2. Linux (issue #20)

Run on as many of these as possible:
- Arch or CachyOS
- Ubuntu or Debian
- Fedora (with firewalld)
- SteamOS desktop mode

For each:
- [ ] `dsp list` and `dsp ping` work as a normal user.
- [ ] `dsp status` without sudo exits with code 4 and suggests sudo.
- [ ] `sudo dsp status` shows the detected backend (nftables, or iptables if `nft` is missing).
- [ ] `sudo dsp block asia`, then `sudo nft list table inet dsp_deadlock_server_picker` (or `sudo iptables -S DSP_DEADLOCK_SERVER_PICKER`) shows the rules. `dsp ping` shows timeouts for Asia.
- [ ] With ufw / firewalld / docker running: their rules are untouched (`sudo nft list ruleset` before and after), and blocking still takes effect.
- [ ] `sudo dsp reset` leaves nothing behind (`sudo nft list ruleset | grep dsp_` and `sudo iptables -S | grep DSP_` both empty).
- [ ] After a reboot, nothing of ours is left. This is expected; the docs say so.
- [ ] SteamOS: note whether `nft`/`iptables` exist and whether `sudo` works (a password must be set with `passwd`).

## 3. In game (issue #29)

Use Deadlock with a fresh Steam start, no VPN, and no game booster.

- [ ] Baseline: queue three times without blocking. Record `net_connection_status` → `Remote host:` each time.
- [ ] `dsp preset only-region <your region>`, then restart Steam. Queue three times. Record the `Remote host:` each time. All matches should be in your region. Note any exceptions: routing through an open relay is a documented limitation.
- [ ] Block a region that you were matched into in the baseline. Over several matches, check that you don't land there, or land there less often.
- [ ] Over-blocking: leave only one far-away PoP open. The CLI warns. Note queue times or match-accept timeouts.
- [ ] `dsp reset`, restart Steam. Matchmaking is back to baseline.
- [ ] The tool never touched game files. Verify integrity of game files in Steam: everything validates.
