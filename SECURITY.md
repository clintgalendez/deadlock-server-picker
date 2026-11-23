# Security policy

## Supported versions

Only the latest release gets fixes.

## Reporting a vulnerability

**Do not open a public issue.** Use GitHub's private vulnerability reporting:
[Report a vulnerability](https://github.com/clintgalendez/deadlock-server-picker/security/advisories/new).

Please include the version (`dsp version`), the OS, and steps to reproduce. You should get a first response within 7 days.

## In scope

- Firewall changes outside our namespace, or leftovers after `reset`
- Privilege escalation through the elevated GUI (Windows) or the `pkexec` / `sudo` paths (Linux)
- Tampering with the commands we run, e.g. through files, environment variables or a crafted SDR config
- Supply-chain problems in the release artifacts or the build scripts

## Out of scope

- Matchmaking still routing through other relays. This is a documented [limitation](docs/limitations.md), not a vulnerability.
- Antivirus or SmartScreen warnings on unsigned binaries. Verify `SHA256SUMS` or build from source.
