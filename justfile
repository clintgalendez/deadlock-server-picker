# Single source of truth for local CI/CD. GitHub Actions is not used.
# Windows: run `just` from Git Bash (it ships with Git for Windows).
# PowerShell's `bash` is the WSL launcher, so recipes use `sh` there.

set shell := ["bash", "-euo", "pipefail", "-c"]
set windows-shell := ["sh", "-euo", "pipefail", "-c"]

sln := "DeadlockServerPicker.slnx"
version := env("DSP_VERSION", "0.0.0-dev")
rids := "win-x64 linux-x64"
allowed_licenses := "MIT;Apache-2.0;BSD-2-Clause;BSD-3-Clause"

default:
    @just --list

# Install git hooks (pre-commit: fmt + lint, pre-push: quick tests)
hooks:
    git config core.hooksPath .githooks
    @echo "hooks installed from .githooks/"

restore:
    dotnet tool restore
    dotnet restore {{sln}} --locked-mode

# Format code in place
fmt:
    dotnet format {{sln}}

# Fail if code is not formatted
fmt-check: restore
    dotnet format {{sln}} --verify-no-changes --no-restore

# Compile everything with analyzers; warnings are errors
lint: restore
    dotnet build {{sln}} -c Release --no-restore

# Run unit tests
test: restore
    dotnet test --solution {{sln}} -c Release

# Fast tests for the pre-push hook (Debug, incremental)
test-quick:
    dotnet test --solution {{sln}}

# Publish self-contained single-file binaries for every target
build: restore
    for rid in {{rids}}; do \
        dotnet publish src/DeadlockServerPicker.Cli -c Release -r "$rid" -o "artifacts/publish/$rid" -p:Version={{version}}; \
    done

# License allowlist + known-vulnerability scan of all (transitive) packages
audit: restore
    mkdir -p artifacts
    dotnet nuget-license -i {{sln}} -t -a "{{allowed_licenses}}" -o Markdown > artifacts/licenses.md || { cat artifacts/licenses.md; echo "license audit failed"; exit 1; }
    echo "license audit: ok ($(grep -c '^| ' artifacts/licenses.md) rows, allowed: {{allowed_licenses}})"
    dotnet list {{sln}} package --vulnerable --include-transitive | tee artifacts/vulnerable.txt
    if grep -q "has the following vulnerable packages" artifacts/vulnerable.txt; then echo "vulnerable packages found"; exit 1; fi
    echo "vulnerability scan: ok"

# Real nftables/iptables test in a throwaway network namespace (opt-in; WSL on Windows, sudo on Linux)
integration-linux:
    bash scripts/integration-linux

# Everything the merge gate requires
ci: fmt-check lint test build audit
    @echo "just ci: all steps passed"

# Release (see scripts/release): `just release prepare v0.1.0`, then `just release v0.1.0`
release *args:
    bash scripts/release {{args}}
