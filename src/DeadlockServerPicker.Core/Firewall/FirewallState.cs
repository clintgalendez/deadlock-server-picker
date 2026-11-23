namespace DeadlockServerPicker.Core.Firewall;

/// <summary>
/// What the firewall itself says is blocked, read only from our namespace.
/// <see cref="Blocked"/> holds, per PoP code, the IPs that are blocked in BOTH directions and actually
/// hooked in; a PoP whose rules are incomplete appears with fewer (or zero) IPs plus a warning, so the
/// planner re-applies it. <see cref="Present"/> is true when anything of ours exists (table, chain,
/// rules), even if unparseable, so reset knows there is something to remove. <see cref="Hooks"/> lists
/// iptables jumps into our chain, one entry per jump rule (duplicates included).
/// </summary>
public sealed record FirewallState(
    IReadOnlyDictionary<string, IReadOnlySet<string>> Blocked,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Hooks,
    bool Present = true)
{
    public static FirewallState Empty { get; } = new(new Dictionary<string, IReadOnlySet<string>>(), [], [], Present: false);
}

public sealed class FirewallException(string message) : Exception(message);

public interface IFirewallBackend
{
    /// <summary>"windows", "nftables" or "iptables".</summary>
    string Name { get; }

    Task<FirewallState> ReadAsync(CancellationToken ct = default);

    /// <summary>Commands that turn <paramref name="current"/> into <paramref name="desired"/>; empty when nothing changes.</summary>
    IReadOnlyList<Command> PlanApply(FirewallState current, DesiredState desired);

    /// <summary>Commands that remove everything in our namespace and nothing else; empty when nothing is there.</summary>
    IReadOnlyList<Command> PlanReset(FirewallState current);
}

/// <summary>Collects per-direction IPs per PoP and turns them into a <see cref="FirewallState"/>.</summary>
internal sealed class StateBuilder
{
    private readonly SortedDictionary<string, (SortedSet<string> In, SortedSet<string> Out)> _pops = new(StringComparer.Ordinal);

    public List<string> Warnings { get; } = [];

    public void Add(string pop, string ip, bool inbound)
    {
        if (!_pops.TryGetValue(pop, out var sets))
            _pops[pop] = sets = (new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
        (inbound ? sets.In : sets.Out).Add(ip);
    }

    /// <summary>Registers a PoP whose rules exist but carry no usable IPs, so it shows up as incomplete.</summary>
    public void Touch(string pop)
    {
        if (!_pops.ContainsKey(pop))
            _pops[pop] = (new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
    }

    public FirewallState Build(bool present, IReadOnlyList<string>? hooks = null)
    {
        var blocked = new SortedDictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var (pop, (inbound, outbound)) in _pops)
        {
            var both = new SortedSet<string>(inbound, StringComparer.Ordinal);
            both.IntersectWith(outbound);
            if (!inbound.SetEquals(outbound))
                Warnings.Add($"{pop}: inbound and outbound rules differ (in {inbound.Count}, out {outbound.Count} IPs); re-apply to repair.");
            blocked[pop] = both;
        }
        return new FirewallState(blocked, Warnings, hooks ?? [], present);
    }
}

internal static class FirewallText
{
    /// <summary>Accepts "1.2.3.4", "1.2.3.4/32" and "1.2.3.4/255.255.255.255"; anything else is null.</summary>
    public static string? HostIp(string s)
    {
        var slash = s.IndexOf('/');
        if (slash >= 0)
        {
            var mask = s[(slash + 1)..];
            if (mask is not ("32" or "255.255.255.255"))
                return null;
            s = s[..slash];
        }
        return Sdr.SdrConfig.NormalizeIpv4(s);
    }

    public static FirewallException Failure(string what, CommandResult r)
    {
        var hint = Elevation.IsElevated() ? "" : " " + Elevation.Hint;
        return new FirewallException($"{what} failed (exit {r.ExitCode}): {r.Stderr.Trim()}{hint}");
    }
}
