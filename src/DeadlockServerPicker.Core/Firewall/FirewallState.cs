namespace DeadlockServerPicker.Core.Firewall;

/// <summary>
/// What the firewall itself says is blocked, read only from our namespace.
/// <see cref="Blocked"/> holds, per PoP code, the IPs that are blocked in BOTH directions and actually
/// hooked in; a PoP whose rules are incomplete appears with fewer (or zero) IPs plus a warning, so the
/// planner re-applies it.
/// </summary>
public sealed record FirewallState(
    IReadOnlyDictionary<string, IReadOnlySet<string>> Blocked,
    IReadOnlyList<string> Warnings,
    IReadOnlySet<string> Hooks)
{
    public static FirewallState Empty { get; } = new(new Dictionary<string, IReadOnlySet<string>>(), [], new HashSet<string>());
}

public sealed class FirewallException(string message) : Exception(message);

public interface IFirewallBackend
{
    /// <summary>"windows", "nftables" or "iptables".</summary>
    string Name { get; }

    Task<FirewallState> ReadAsync(CancellationToken ct = default);
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

    public FirewallState Build(IReadOnlySet<string>? hooks = null)
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
        return new FirewallState(blocked, Warnings, hooks ?? new HashSet<string>());
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
