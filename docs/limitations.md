# Limitations: please read before reporting "it doesn't work"

Deadlock Server Picker **nudges** matchmaking away from regions you don't want. It cannot force you onto a particular server. Here is why, in plain language.

## 1. We can only block relays, not game servers

- Valve publishes the addresses of its **relays** (Steam Datagram Relay points of presence). It does not publish the addresses of the **game servers** behind them.
- Your game talks to a relay, and the relay forwards traffic over Valve's private network to a game server, which can be in another city.
- When you block a region's relays, the game can no longer use them. But if Valve decides the best match for you is in that region, it can still reach it **through a relay you left open**. This can happen even mid-match.
- So blocking makes unwanted regions much less likely, but never impossible.

## 2. Some locations have no relays at all

Valve's config lists some data centers with **no relays**. As of November 2026 these are:

- Helsinki (`hel`)
- Falkenstein (`fsn`)
- Wenatchee (`eat`)
- one Datapacket Stockholm entry (`dsto`)

You reach them through nearby relays, for example Helsinki through Stockholm (`sto`, `sto2`) and Falkenstein through Frankfurt (`fra`, `dfra`). The app lists them as **not blockable** and shows which relays they sit behind. Blocking those relays is the closest you can get.

## 3. Blocking too much breaks matchmaking

- If you leave very few regions open, the match search has fewer places to put you. You may see:
  - very long queues
  - "match found" popups that **time out** before everyone accepts
  - "failed to reach any official servers"
- Keep at least two or three **nearby** regions open. The app warns you when you leave fewer than three blockable PoPs unblocked.

## 4. Parties

Matchmaking considers everyone in the party. Your firewall only affects your own PC, so if your friends haven't blocked the same regions you may still end up elsewhere.

## 5. Other software can override the rules

- **VPNs and "game boosters"** (ExitLag, NoPing and similar) route your traffic differently, so rules aimed at relay IPs may never match. Turn them off while testing.
- **Third-party firewalls and antivirus suites** (Bitdefender, Norton, Avast, Kaspersky…) often replace Windows Defender Firewall. Our rules are then present but **not enforced**. Use that product's own blocking features, or switch back to Windows Defender Firewall.
- **Windows Defender Firewall turned off:** rules are ignored. The app tells you when it detects this.

## 6. Linux specifics

- Rules **don't survive a reboot**. This is deliberate: nothing is left behind. Re-apply after rebooting.
- The tool uses its own nftables table (or its own iptables chain), so it works alongside ufw, firewalld and docker. Another tool that flushes the *whole* ruleset (for example `nft flush ruleset` in a reload) removes our rules too.
- Steam Deck / SteamOS is untested.

## 7. Valve changes the relay list

- Relay addresses change over time; the config's `revision` number tells us when.
- On launch the app compares your blocked relays with the latest list and offers to re-apply. It never silently unblocks you.

## How to check where you really are

Open the Deadlock console (F7) during a match and run:

```
net_connection_status
```

Look for the `Remote host:` line. It shows the data center code (e.g. `fra`, `sto`), which matches the PoP codes in this app.

## In-game region setting

Deadlock has its own matchmaking region preference, set with `citadel_region_override` in the console:

- `-1` automatic
- `0` North America
- `1` Europe
- `2` Asia
- `3` South America
- `5` Oceania

It complements firewall blocking. This tool never edits game files to set it.
