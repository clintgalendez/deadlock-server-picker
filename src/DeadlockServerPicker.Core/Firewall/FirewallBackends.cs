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

    /// <summary>Runs commands in order and stops at the first failure.</summary>
    public static async Task ExecuteAsync(ICommandRunner runner, IReadOnlyList<Command> commands, CancellationToken ct = default)
    {
        foreach (var c in commands)
        {
            var r = await runner.RunAsync(c, ct);
            if (r.ExitCode != 0)
                throw FirewallText.Failure($"`{c.File} {string.Join(' ', c.Args.Take(3))}`", r);
        }
    }

    /// <summary>
    /// Applies <paramref name="desired"/> and reads the firewall back to prove it (we never report
    /// success without checking). Returns the state after applying.
    /// </summary>
    public static async Task<FirewallState> ApplyAndVerifyAsync(IFirewallBackend backend, ICommandRunner runner, IReadOnlyList<Command> commands, DesiredState desired, CancellationToken ct = default)
    {
        await ExecuteAsync(runner, commands, ct);
        var after = await backend.ReadAsync(ct);
        var problems = Planner.Diff(after, desired).Select(c => $"{c.Pop} ({c.Kind.ToString().ToLowerInvariant()} did not take effect)").ToList();
        if (problems.Count > 0)
            throw new FirewallException($"The firewall does not match what was applied: {string.Join(", ", problems)}. See the log file for the commands and their output.");
        return after;
    }

    /// <summary>Removes everything in our namespace and verifies nothing is left.</summary>
    public static async Task ResetAndVerifyAsync(IFirewallBackend backend, ICommandRunner runner, FirewallState current, CancellationToken ct = default)
    {
        await ExecuteAsync(runner, backend.PlanReset(current), ct);
        var after = await backend.ReadAsync(ct);
        if (after.Present)
            throw new FirewallException("Reset ran but some Deadlock Server Picker rules are still present. See the log file.");
    }
}
