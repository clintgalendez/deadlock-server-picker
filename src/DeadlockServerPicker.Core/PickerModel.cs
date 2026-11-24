using DeadlockServerPicker.Core.Firewall;
using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Core;

public enum PopStatus { NotBlockable, Unknown, Open, Blocked, BlockedStale }

public sealed record PopRow(
    Pop Pop,
    RegionInfo Region,
    long? PingMs,
    bool Pinged,
    PopStatus Status,
    bool Allowed,
    IReadOnlyList<(string Code, int Ms)> ReachedVia);

/// <summary>
/// GUI state without any UI code. "Allowed" is the allow-list the user edits (checked = may be
/// matched there); everything blockable that is not allowed gets blocked on Apply. The firewall
/// state is null until it has been read (on Linux that needs authentication).
/// </summary>
public sealed class PickerModel(SdrConfig config)
{
    private readonly HashSet<string> _allowed = config.Pops.Values.Where(p => p.Blockable).Select(p => p.Code).ToHashSet();
    private IReadOnlyDictionary<string, long?> _pings = new Dictionary<string, long?>();

    public SdrConfig Config { get; } = config;

    public FirewallState? Firewall { get; private set; }

    public IReadOnlySet<string> Allowed => _allowed;

    private IEnumerable<Pop> Blockable => Config.Pops.Values.Where(p => p.Blockable);

    /// <summary>Adopts what the firewall says: blocked PoPs become unchecked, everything else checked.</summary>
    public void SetFirewall(FirewallState state)
    {
        Firewall = state;
        _allowed.Clear();
        _allowed.UnionWith(Blockable.Where(p => !state.Blocked.ContainsKey(p.Code)).Select(p => p.Code));
    }

    public void SetPings(IReadOnlyDictionary<string, long?> pings) => _pings = pings;

    /// <summary>Replaces the allow-list (used to keep the user's pending edits across a re-sync).</summary>
    public void SetAllowedSet(IEnumerable<string> allowed)
    {
        _allowed.Clear();
        _allowed.UnionWith(allowed.Where(c => Config.Pops.TryGetValue(c, out var p) && p.Blockable));
    }

    /// <summary>Blocked PoPs whose relay IPs changed since they were blocked (empty until the firewall is read).</summary>
    public IReadOnlyList<StaleBlock> Stale => Firewall is null ? [] : Planner.StaleBlocks(Config, Firewall);

    public void SetAllowed(string pop, bool allowed)
    {
        if (!Config.Pops.TryGetValue(pop, out var p) || !p.Blockable)
            return;
        if (allowed)
            _allowed.Add(pop);
        else
            _allowed.Remove(pop);
    }

    public IReadOnlyList<Region> RegionsPresent =>
        Blockable.Select(p => Regions.Of(p, Config).Region).Distinct().Order().ToList();

    /// <summary>true = every blockable PoP of the region allowed, false = none, null = mixed.</summary>
    public bool? RegionState(Region region)
    {
        var pops = Blockable.Where(p => Regions.Of(p, Config).Region == region).ToList();
        var allowed = pops.Count(p => _allowed.Contains(p.Code));
        return allowed == pops.Count ? true : allowed == 0 ? false : null;
    }

    public void SetRegion(Region region, bool allowed)
    {
        foreach (var p in Blockable.Where(p => Regions.Of(p, Config).Region == region))
            SetAllowed(p.Code, allowed);
    }

    /// <summary>Preset "Only my region".</summary>
    public void OnlyRegion(Region region)
    {
        _allowed.Clear();
        _allowed.UnionWith(Blockable.Where(p => Regions.Of(p, Config).Region == region).Select(p => p.Code));
    }

    /// <summary>Preset "Only within X ms". Unpinged or timed-out PoPs stay allowed; they are returned.</summary>
    public IReadOnlyList<string> Within(int maxMs)
    {
        var unknown = new List<string>();
        _allowed.Clear();
        foreach (var p in Blockable)
        {
            if (_pings.GetValueOrDefault(p.Code) is { } ms)
            {
                if (ms <= maxMs)
                    _allowed.Add(p.Code);
            }
            else
            {
                _allowed.Add(p.Code);
                unknown.Add(p.Code);
            }
        }
        return unknown;
    }

    public void AllowAll() => _allowed.UnionWith(Blockable.Select(p => p.Code));

    public DesiredState Desired => Planner.Desired(Config, Blockable.Select(p => p.Code).Where(c => !_allowed.Contains(c)));

    /// <summary>Number of PoPs Apply would change (against the firewall if known, else against nothing blocked).</summary>
    public int PendingChanges => Planner.Diff(Firewall ?? FirewallState.Empty, Desired).Count;

    public string? OverBlockingWarning => Planner.OverBlockingWarning(Config, Desired);

    /// <summary>The in-game override matching the selection, when every allowed PoP is in one region that has one.</summary>
    public string? RegionOverrideTip
    {
        get
        {
            var regions = _allowed.Select(c => Regions.Of(Config.Pops[c], Config).Region).Distinct().ToList();
            if (_allowed.Count == Blockable.Count() || regions.Count != 1 || Regions.CitadelRegionOverride(regions[0]) is not { } value)
                return null;
            return $"In Deadlock, also set the matchmaking region: open the console (F7) and run citadel_region_override {value} ({Regions.DisplayName(regions[0])}). Use -1 to go back to automatic.";
        }
    }

    public IEnumerable<PopRow> Rows => Config.Pops.Values.Select(p => new PopRow(
        p,
        Regions.Of(p, Config),
        _pings.GetValueOrDefault(p.Code),
        _pings.ContainsKey(p.Code),
        StatusOf(p),
        _allowed.Contains(p.Code),
        p.Blockable ? [] : Config.NearbyBlockable(p.Code)));

    private PopStatus StatusOf(Pop p)
    {
        if (!p.Blockable)
            return PopStatus.NotBlockable;
        if (Firewall is null)
            return PopStatus.Unknown;
        if (!Firewall.Blocked.TryGetValue(p.Code, out var ips))
            return PopStatus.Open;
        return ips.SetEquals(p.Relays.Select(r => r.Ipv4)) ? PopStatus.Blocked : PopStatus.BlockedStale;
    }
}
