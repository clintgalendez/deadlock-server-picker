using DeadlockServerPicker.Core.Firewall;
using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Core;

public enum ChangeKind { Add, Remove, Update }

/// <summary>One PoP's change: IPs before (from the firewall) and after (desired).</summary>
public sealed record PopChange(string Pop, ChangeKind Kind, IReadOnlySet<string> Before, IReadOnlySet<string> After);

/// <summary>Desired firewall content: PoP code → relay IPs to block.</summary>
public sealed record DesiredState(IReadOnlyDictionary<string, IReadOnlySet<string>> Blocked)
{
    public static DesiredState Empty { get; } = new(new SortedDictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal));
}

/// <summary>Result of turning user selectors into PoP codes. Notes explain skipped items.</summary>
public sealed record Selection(IReadOnlySet<string> Pops, IReadOnlyList<string> Notes);

public static class Planner
{
    /// <summary>Fewer unblocked, blockable PoPs than this triggers the over-blocking warning.</summary>
    public const int MinOpenPops = 3;

    /// <summary>
    /// Resolves PoP codes, aliases (e.g. bom → bom2) and region names (eu, na, asia, sa, oce, me, af)
    /// to blockable PoP codes. Unknown selectors throw; PoPs without relays are skipped with a note.
    /// </summary>
    public static Selection Resolve(SdrConfig cfg, IEnumerable<string> selectors)
    {
        var pops = new SortedSet<string>(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var raw in selectors)
        {
            var s = raw.Trim().ToLowerInvariant();
            if (Regions.TryParse(s, out var region))
            {
                pops.UnionWith(cfg.Pops.Values.Where(p => p.Blockable && Regions.Of(p, cfg).Region == region).Select(p => p.Code));
                continue;
            }
            var pop = cfg.Pops.GetValueOrDefault(s) ?? cfg.Pops.Values.FirstOrDefault(p => p.Aliases.Contains(s));
            if (pop is null)
                throw new ArgumentException($"'{raw}' is not a PoP code or region. Run `dsp list` to see codes; regions are na, eu, asia, sa, oce, me, af.");
            if (pop.Blockable)
            {
                pops.Add(pop.Code);
                continue;
            }
            var via = cfg.NearbyBlockable(pop.Code);
            notes.Add(via.Count > 0
                ? $"{pop.Code} can't be blocked: Valve publishes no relays for it. It is reached through {string.Join(", ", via.Select(v => $"{v.Code} ({v.Ms} ms)"))}; block those instead."
                : $"{pop.Code} can't be blocked: Valve publishes no relays for it.");
        }
        return new Selection(pops, notes);
    }

    /// <summary>Desired state for exactly these PoPs, using the config's current relay IPs.</summary>
    public static DesiredState Desired(SdrConfig cfg, IEnumerable<string> pops)
    {
        var blocked = new SortedDictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var code in pops)
        {
            if (cfg.Pops.TryGetValue(code, out var p) && p.Blockable)
                blocked[code] = new SortedSet<string>(p.Relays.Select(r => r.Ipv4), StringComparer.Ordinal);
        }
        return new DesiredState(blocked);
    }

    /// <summary>
    /// PoPs blocked right now that still exist in the config. Re-planning them picks up fresh relay IPs
    /// (re-sync); PoPs Valve dropped fall out and get removed.
    /// </summary>
    public static IEnumerable<string> CurrentlyBlocked(FirewallState current) => current.Blocked.Keys;

    public static DesiredState Block(SdrConfig cfg, FirewallState current, IEnumerable<string> pops) =>
        Desired(cfg, CurrentlyBlocked(current).Union(pops));

    public static DesiredState Unblock(SdrConfig cfg, FirewallState current, IEnumerable<string> pops) =>
        Desired(cfg, CurrentlyBlocked(current).Except(pops));

    /// <summary>Preset "Only my region": block every blockable PoP outside the given regions.</summary>
    public static DesiredState OnlyRegions(SdrConfig cfg, IReadOnlySet<Region> keep) =>
        Desired(cfg, cfg.Pops.Values.Where(p => p.Blockable && !keep.Contains(Regions.Of(p, cfg).Region)).Select(p => p.Code));

    /// <summary>
    /// Preset "Only within X ms": block PoPs whose ping is above the limit. PoPs that timed out are NOT
    /// blocked (some relays drop ICMP); they are returned so the caller can tell the user.
    /// </summary>
    public static (DesiredState Desired, IReadOnlyList<string> TimedOut) Within(SdrConfig cfg, IReadOnlyDictionary<string, long?> pings, int maxMs)
    {
        var slow = pings.Where(kv => kv.Value > maxMs).Select(kv => kv.Key);
        var timedOut = pings.Where(kv => kv.Value is null).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        return (Desired(cfg, slow), timedOut);
    }

    public static IReadOnlyList<PopChange> Diff(FirewallState current, DesiredState desired)
    {
        var empty = new SortedSet<string>(StringComparer.Ordinal);
        var changes = new List<PopChange>();
        foreach (var pop in current.Blocked.Keys.Union(desired.Blocked.Keys).Order(StringComparer.Ordinal))
        {
            var had = current.Blocked.TryGetValue(pop, out var before);
            var want = desired.Blocked.TryGetValue(pop, out var after);
            if (had && !want)
                changes.Add(new PopChange(pop, ChangeKind.Remove, before!, empty));
            else if (!had && want)
                changes.Add(new PopChange(pop, ChangeKind.Add, empty, after!));
            else if (!before!.SetEquals(after!))
                changes.Add(new PopChange(pop, ChangeKind.Update, before, after!));
        }
        return changes;
    }

    /// <summary>Blockable PoPs left open by <paramref name="desired"/>.</summary>
    public static IReadOnlyList<string> OpenPops(SdrConfig cfg, DesiredState desired) =>
        cfg.Pops.Values.Where(p => p.Blockable && !desired.Blocked.ContainsKey(p.Code)).Select(p => p.Code).ToList();

    public static string? OverBlockingWarning(SdrConfig cfg, DesiredState desired)
    {
        var open = OpenPops(cfg, desired);
        if (open.Count >= MinOpenPops)
            return null;
        return open.Count == 0
            ? "Every blockable PoP is blocked. Matchmaking will only find servers through relays it can still reach, if any: expect very long queues or no matches at all."
            : $"Only {open.Count} PoP{(open.Count == 1 ? "" : "s")} left open ({string.Join(", ", open)}). Too few open regions cause long queues and match-accept timeouts; keep at least {MinOpenPops} nearby PoPs open.";
    }
}
