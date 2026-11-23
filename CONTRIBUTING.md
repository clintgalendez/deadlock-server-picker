# Contributing

Thanks for helping! Bug reports with logs and real-hardware test results are especially valuable.

## Setup

- [.NET 10 SDK](https://dotnet.microsoft.com/download). `global.json` pins the feature band.
- [`just`](https://github.com/casey/just): `winget install Casey.Just`, `brew install just`, `pacman -S just`, `apt install just`…
- bash. **On Windows, run `just` from Git Bash.** In PowerShell, `bash` is the WSL launcher.
- Optional: [`git-cliff`](https://git-cliff.org) (releases only), WSL with `nftables` and `iptables` (Linux integration tests).

```
just hooks   # pre-commit: format check + lint; pre-push: quick tests
just --list
```

## Workflow

1. **Every change starts from an issue.** Each issue carries acceptance criteria.
2. Branch per issue: `<type>/<issue>-<slug>`, e.g. `feat/14-nftables-backend`.
3. Use [Conventional Commits](https://www.conventionalcommits.org) for commit and PR titles, e.g. `feat(cli): add preset command`. PRs are squash-merged, so **the PR title becomes the changelog entry**. Write it for users.
4. Keep PRs small and put `Closes #N` in the description.
5. Run local CI on the pushed PR head:

   ```
   scripts/ci
   ```

   - It runs `just ci` (format check, lint, tests, builds for every target, license audit, vulnerability scan).
   - It posts the `local-ci` commit status and writes a summary into the PR description.
   - `main` requires a green `local-ci`. GitHub Actions is not used.
6. For firewall changes, paste the `--dry-run` output into the PR description.

## Rules for firewall code

- Only ever touch entries in our namespace:
  - Windows: rules named `DeadlockServerPicker_*` in group `DeadlockServerPicker`
  - nftables: table `inet dsp_deadlock_server_picker`
  - iptables: chain `DSP_DEADLOCK_SERVER_PICKER` and the jumps to it
- `reset` must leave zero traces. The Linux integration test checks this.
- Backends only produce commands. Execution goes through the injectable runner, and unit tests compare generated commands with golden files in `tests/DeadlockServerPicker.Tests/golden/`. To update the golden files after an intended change, run `DSP_UPDATE_GOLDEN=1 just test` and review the diff.
- Never parse localized command output.

## Tests

- `just test` runs the unit tests. Fixtures are checked-in snapshots of the real `GetSDRConfig` responses.
- Integration tests that touch a real firewall are opt-in:
  - Linux: `just integration-linux` runs the CLI inside a throwaway network namespace in WSL or Linux, as root.
  - Windows: `DSP_INTEGRATION_WINDOWS=1 just test` from an **elevated** shell. It creates rules under our prefix only and removes them.
- `scripts/check-sdr-drift` (Python 3, stdlib only) compares the live config with the fixtures. It opens or comments on an `sdr-drift` issue when PoPs or the schema change. `--dry-run` only prints the report, and `--update-fixtures` refreshes the snapshots. On Windows, run it as `python scripts/check-sdr-drift`.

## Docs and changelog

- User-facing changes update the README or `docs/` in the same PR.
- `CHANGELOG.md` is generated from Conventional Commit titles by `git-cliff` at release time.
