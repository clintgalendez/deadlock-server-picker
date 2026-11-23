namespace DeadlockServerPicker.Core.Firewall;

public static class FirewallBackends
{
    public static readonly IReadOnlyList<string> Names = ["windows", "nftables", "iptables"];

    /// <summary>
    /// Windows always uses NetSecurity. On Linux, nftables if <c>nft</c> runs, otherwise iptables.
    /// <paramref name="name"/> (from --backend) overrides detection.
    /// </summary>
    public static async Task<IFirewallBackend> CreateAsync(ICommandRunner runner, string? name = null, CancellationToken ct = default)
    {
        name ??= OperatingSystem.IsWindows() ? "windows" : await DetectLinuxAsync(runner, ct);
        return name switch
        {
            "windows" => new WindowsFirewallBackend(runner, OperatingSystem.IsWindows() ? WindowsFirewallBackend.DefaultPowerShell : "powershell.exe"),
            "nftables" => new NftablesBackend(runner),
            "iptables" => new IptablesBackend(runner),
            _ => throw new FirewallException($"Unknown firewall backend '{name}'. Use one of: {string.Join(", ", Names)}."),
        };
    }

    private static async Task<string> DetectLinuxAsync(ICommandRunner runner, CancellationToken ct)
    {
        if ((await runner.RunAsync(new Command("nft", ["--version"]), ct)).ExitCode == 0)
            return "nftables";
        if ((await runner.RunAsync(new Command("iptables", ["--version"]), ct)).ExitCode == 0)
            return "iptables";
        throw new FirewallException(
            "Neither nft nor iptables was found. Install nftables: Debian/Ubuntu `sudo apt install nftables`, " +
            "Fedora `sudo dnf install nftables`, Arch `sudo pacman -S nftables`.");
    }
}
