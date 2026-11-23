# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

This file is generated from Conventional Commit messages by
[git-cliff](https://git-cliff.org) when a release is prepared (`scripts/release prepare`).

## [0.1.0] - 2026-11-23

### Added

- **core:** Fetch, validate and cache the SDR config with offline fallback (#36)
- **core:** Map PoPs to regions from a data file with a typical_pings and geo fallback (#37)
- **core:** Ping one relay per PoP concurrently without admin rights (#38)
- **core:** Log to a rotating file in the user config directory (#39)
- **firewall:** Read blocked PoPs back from Windows Firewall, nftables and iptables (#40)
- **cli:** Add list, ping, status and version commands with --json (#41)
- **core:** Plan firewall changes from block/unblock intents and presets (#43)
- **firewall:** Apply and reset Windows Firewall rules with read-back verification (#44)
- **firewall:** Apply and reset nftables rules atomically in our own table (#45)
- **firewall:** Apply and reset iptables rules in our own chain (#48)
- **cli:** Add block, unblock, preset and reset with dry-run and verification (#49)

### Fixed

- **firewall:** Rename Linux namespace to avoid clashing with another Deadlock picker (#47)

### Documentation

- Add prior-art research and derived requirements (#32)
- **adr:** Record stack, firewall backend and elevation decisions (#33)
- Add README, limitations, community files, templates and AGENTS.md (#34)

[0.1.0]: https://github.com/clintgalendez/deadlock-server-picker/tree/v0.1.0

