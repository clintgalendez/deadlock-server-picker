# Install

Download the archive for your OS from [Releases](https://github.com/clintgalendez/deadlock-server-picker/releases):

| OS | File | Contents |
|---|---|---|
| Windows 10/11 x64 | `deadlock-server-picker_<version>_windows_amd64.zip` | `dsp.exe` (CLI), plus the GUI from v0.2.0 |
| Linux x64 | `deadlock-server-picker_<version>_linux_amd64.tar.gz` | `dsp` (CLI), plus the GUI from v0.2.0 |

The binaries are portable and self-contained. There is no installer and you don't need the .NET runtime. Extract the archive anywhere and run from there.

## Verify the download

Every release ships a `SHA256SUMS` file. Check your archive against it.

**Windows (PowerShell):**

```powershell
Get-FileHash .\deadlock-server-picker_0.1.0_windows_amd64.zip -Algorithm SHA256
# compare with the matching line in SHA256SUMS
```

**Linux:**

```sh
sha256sum -c SHA256SUMS --ignore-missing
```

## "Windows protected your PC" (SmartScreen) and antivirus warnings

The binaries are **not code-signed**, because a code-signing certificate costs money this project doesn't have. This leads to two warnings.

**SmartScreen** shows "Windows protected your PC" for new, unsigned programs. If the checksum matches, click **More info → Run anyway**.

**Antivirus false positives** happen sometimes. Two things trigger heuristics:
- self-extracting single-file .NET apps
- any tool that changes firewall rules

If that happens:
- Verify the checksum.
- Optionally check the file on [VirusTotal](https://www.virustotal.com).
- Report the false positive to your antivirus vendor.
- Or build from source (below).

The tool never touches game files and never downloads or runs code. Its only network access is fetching Valve's public relay list from `api.steampowered.com`, plus the pings.

## Linux notes

- Extract with `tar xzf deadlock-server-picker_<version>_linux_amd64.tar.gz`. The binaries keep their executable bit.
- Changing or reading the firewall needs root: `sudo ./dsp block asia`, `sudo ./dsp status`.
- It needs `nftables` (preferred) or `iptables`. Install with `sudo apt install nftables`, `sudo dnf install nftables` or `sudo pacman -S nftables`.
- The GUI (from v0.2.0) needs an X11 or XWayland desktop with `libICE`, `libSM` and `libfontconfig`. Every desktop distro and SteamOS desktop mode has these.
- Blocks don't survive a reboot. See [limitations](limitations.md).

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download), [`just`](https://github.com/casey/just) and bash. On Windows, use Git Bash.

```sh
git clone https://github.com/clintgalendez/deadlock-server-picker
cd deadlock-server-picker
just build        # artifacts/publish/win-x64 and artifacts/publish/linux-x64
```

To build one target only: `dotnet publish src/DeadlockServerPicker.Cli -c Release -r linux-x64 -o out`.

## Uninstall

1. Run `dsp reset` (elevated or with sudo) to remove every firewall rule this tool created.
2. Delete the extracted folder.
3. Optionally delete the config and log folder (`dsp version` prints its path).
